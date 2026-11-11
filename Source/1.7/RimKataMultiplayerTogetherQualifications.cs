using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Together changes the visiting faction to Ally/Enemy on the host. Qualification
    // is therefore a host result, not a second calculation against the guest's player faction.
    internal static class RimKataMultiplayerTogetherQualifications
    {
        internal sealed class View : IExposable
        {
            public Pawn pawn;
            public bool hostile, unrestricted, enhancedGrip;
            public string profileKey;
            internal RimKataSettings settings;
            public View() { }
            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_Values.Look(ref hostile, "hostile");
                Scribe_Values.Look(ref unrestricted, "unrestricted");
                Scribe_Values.Look(ref enhancedGrip, "enhancedGrip");
                Scribe_Values.Look(ref profileKey, "profileKey");
            }
        }

        private sealed class MapView
        {
            public MapView() { }
            internal readonly List<Pawn> pawns = new();
            internal readonly Dictionary<Pawn, View> byPawn = new();
        }

        private static readonly ConditionalWeakTable<Map, MapView> maps = new();
        private static readonly Pawn[] empty = Array.Empty<Pawn>();
        private static readonly Dictionary<RimKataSettings, string> keysBySettings = new();
        private static readonly Dictionary<string, RimKataSettings> settingsByKey = new(StringComparer.Ordinal);
        private static bool profilesPrepared;
        private static bool initialized;

        internal static void Initialize(Harmony harmony)
        {
            if (initialized) return;
            Patch(typeof(RimKataEligibilityCache), "IsCachedQualifiedPawn", nameof(Qualified));
            Patch(typeof(RimKataEligibilityCache), "IsRegisteredUser", nameof(Qualified));
            Patch(typeof(RimKataEligibilityCache), "HasAnyAccessSource", nameof(Qualified));
            Patch(typeof(RimKataEligibilityCache), "TryGetCachedAccess", nameof(CachedAccess));
            Patch(typeof(RimKataEligibilityCache), "TryGetRegisteredSecondaryWeapon", nameof(Secondary));
            Patch(typeof(RimKataEligibilityCache), "GetQualifiedPawns", nameof(QualifiedPawns));
            Patch(typeof(RimKataEligibilityCache), "NotifyPawnSpawned", nameof(AllowLocalPublication));
            Patch(typeof(RimKataEligibilityCache), "SetQualified", nameof(AllowLocalPublication));
            Patch(typeof(RimKataEligibility), "HasRimKataAccess", nameof(Qualified));
            Patch(typeof(RimKataEligibility), "IsHostileToPlayerFaction", nameof(Hostile));
            Patch(typeof(RimKataTargetAccess), "IsEnabled", nameof(Unrestricted));
            Patch(typeof(RimKataTargetAccess), "SettingsFor", nameof(Settings));
            Patch(typeof(RimKataStrengthUtility), "HasEnhancedGrip", nameof(EnhancedGrip));
            Patch(typeof(RimKataResponseVisualParticipantCache), "NotifyStrengthChanged", nameof(StrengthPublished));
            initialized = true;

            void Patch(Type type, string name, string prefix)
            {
                MethodInfo method = AccessTools.DeclaredMethod(type, name)
                    ?? throw new MissingMethodException(type.FullName, name);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RimKataMultiplayerTogetherQualifications), prefix));
            }
        }

        internal static List<View> Capture(Map map)
        {
            PrepareProfiles();
            var result = new List<View>();
            IReadOnlyList<Pawn> pawns = RimKataEligibilityCache.GetQualifiedPawns(map);
            foreach (Pawn pawn in pawns)
            {
                if (pawn?.MapHeld != map) continue;
                RimKataSettings settings = RimKataTargetAccess.SettingsFor(pawn);
                if (!keysBySettings.TryGetValue(settings, out string profileKey))
                    throw new InvalidOperationException("The active RimKata profile changed during the Together session.");
                result.Add(new View
                {
                    pawn = pawn,
                    hostile = RimKataEligibility.IsHostileToPlayerFaction(pawn),
                    unrestricted = RimKataTargetAccess.IsEnabled(pawn),
                    enhancedGrip = RimKataStrengthUtility.HasEnhancedGrip(pawn),
                    profileKey = profileKey
                });
            }
            return result;
        }

        internal static void Validate(List<View> values)
        {
            PrepareProfiles();
            if (values == null) throw new InvalidOperationException("The host RimKata qualification list is missing.");
            var seen = new HashSet<Pawn>();
            foreach (View value in values)
            {
                if (value?.pawn == null || !seen.Add(value.pawn))
                    throw new InvalidOperationException("The host RimKata qualification list has a missing or duplicate pawn.");
                ResolveProfile(value.profileKey);
            }
        }

        internal static void Apply(Map map, List<View> values)
        {
            if (!Replica(map)) return;
            PrepareProfiles();
            MapView previous = maps.GetOrCreateValue(map);
            var next = new MapView();
            if (values != null)
                foreach (View value in values)
                {
                    Pawn pawn = value?.pawn;
                    if (pawn == null || pawn.MapHeld != map || next.byPawn.ContainsKey(pawn)) continue;
                    value.settings = ResolveProfile(value.profileKey);
                    next.pawns.Add(pawn);
                    next.byPawn.Add(pawn, value);
                }
            maps.Remove(map);
            maps.Add(map, next);
            foreach (Pawn pawn in previous.pawns)
                if (!next.byPawn.ContainsKey(pawn))
                    RimKataResponseVisualParticipantCache.NotifyQualificationChanged(pawn, false);
            foreach (Pawn pawn in next.pawns)
            {
                RimKataResponseVisualParticipantCache.NotifyQualificationChanged(pawn, true);
                RimKataResponseVisualParticipantCache.NotifyStrengthChanged(pawn, next.byPawn[pawn].enhancedGrip);
            }
        }

        internal static void Clear(Map map)
        {
            profilesPrepared = false;
            keysBySettings.Clear();
            settingsByKey.Clear();
            if (map == null || !maps.TryGetValue(map, out MapView previous)) return;
            maps.Remove(map);
            foreach (Pawn pawn in previous.pawns)
                RimKataResponseVisualParticipantCache.NotifyQualificationChanged(pawn, false);
        }

        private static void PrepareProfiles()
        {
            if (profilesPrepared) return;
            keysBySettings.Clear();
            settingsByKey.Clear();
            Add(RimKataMod.Settings, RimKataSettingsProfile.Capture(RimKataMod.Settings));
            if (RimKataMod.Profiles != null)
                foreach (RimKataStoredProfile profile in RimKataMod.Profiles.Profiles)
                    Add(profile.RuntimeSettings, profile.Values);
            profilesPrepared = true;

            void Add(RimKataSettings settings, RimKataSettingsProfile profile)
            {
                string key = RimKataMultiplayerSettings.ProfileKey(profile);
                keysBySettings[settings] = key;
                // Equivalent values share a key even when local profile names/IDs differ.
                if (!settingsByKey.ContainsKey(key)) settingsByKey.Add(key, settings);
            }
        }

        private static RimKataSettings ResolveProfile(string key)
        {
            if (key != null && settingsByKey.TryGetValue(key, out RimKataSettings settings)) return settings;
            throw new InvalidOperationException("The host RimKata profile values are unavailable: " + key);
        }

        private static bool Replica(Map map) => map != null && RimKataMultiplayerTogetherCompat.IsReplicaClient(map);
        private static bool Replica(Pawn pawn) => Replica(pawn?.MapHeld);
        private static View Find(Pawn pawn)
            => pawn?.MapHeld != null && maps.TryGetValue(pawn.MapHeld, out MapView map)
                && map.byPawn.TryGetValue(pawn, out View value) ? value : null;
        private static bool AllowLocalPublication(Pawn pawn) => !Replica(pawn);
        private static bool Qualified(Pawn pawn, ref bool __result)
        {
            if (!Replica(pawn)) return true;
            __result = Find(pawn) != null;
            return false;
        }
        private static bool CachedAccess(Pawn pawn, ref bool cachedAccess, ref bool __result)
        {
            if (!Replica(pawn)) return true;
            cachedAccess = Find(pawn) != null;
            __result = true;
            return false;
        }
        private static bool Secondary(Pawn pawn, ref ThingWithComps secondaryWeapon, ref bool __result)
        {
            if (!Replica(pawn)) return true;
            __result = Find(pawn) != null;
            secondaryWeapon = __result ? RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn) : null;
            return false;
        }
        private static bool QualifiedPawns(Map map, ref IReadOnlyList<Pawn> __result)
        {
            if (!Replica(map)) return true;
            __result = maps.TryGetValue(map, out MapView view) ? view.pawns : (IReadOnlyList<Pawn>)empty;
            return false;
        }
        private static bool Hostile(Pawn pawn, ref bool __result)
        {
            View view = Replica(pawn) ? Find(pawn) : null;
            if (view == null) return true;
            __result = view.hostile;
            return false;
        }
        private static bool Unrestricted(Pawn pawn, ref bool __result)
        {
            if (!Replica(pawn)) return true;
            __result = Find(pawn)?.unrestricted == true;
            return false;
        }
        private static bool Settings(Pawn pawn, ref RimKataSettings __result)
        {
            View view = Replica(pawn) ? Find(pawn) : null;
            if (view == null) return true;
            __result = view.settings;
            return false;
        }
        private static bool EnhancedGrip(Pawn pawn, ref bool __result)
        {
            if (!Replica(pawn)) return true;
            __result = Find(pawn)?.enhancedGrip == true;
            return false;
        }
        private static void StrengthPublished(Pawn pawn, ref bool enhancedGrip)
        {
            if (Replica(pawn)) enhancedGrip = Find(pawn)?.enhancedGrip == true;
        }
    }
}
