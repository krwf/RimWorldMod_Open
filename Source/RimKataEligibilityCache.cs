using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public static class RimKataEligibilityCache
    {
        private const int AccessUnknown = 0;
        private const int AccessDenied = 1;
        private const int AccessGranted = 2;

        private sealed class Entry
        {
            public volatile int accessState;
            public bool rimKataGeneKnown;
            public bool hasRimKataGene;
            public bool ampouleKnown;
            public bool hasAmpoule;
            public bool psycastKnown;
            public bool hasPsycast;
            public bool roleKnown;
            public bool hasRole;
            public bool dependencyGeneKnown;
            public bool hasDependencyGene;
            public Gene_MindNumbSerumDependency dependencyGene;
            public bool mindNumbedKnown;
            public bool mindNumbed;
            public bool bondKnown;
            public bool bond;
            public Map publishedMap;
            public bool qualified;
        }

        private sealed class RegisteredUser
        {
            public volatile ThingWithComps secondaryWeapon;
        }

        private sealed class MapUsers
        {
            internal readonly HashSet<Pawn> permittedSources = new HashSet<Pawn>();
            internal readonly List<Pawn> qualified = new List<Pawn>();
            internal readonly Dictionary<Pawn, int> qualifiedIndices = new Dictionary<Pawn, int>();
            internal bool initialized;
        }

        private static ConditionalWeakTable<Pawn, Entry> entries = new ConditionalWeakTable<Pawn, Entry>();
        private static readonly ConditionalWeakTable<Pawn, Entry>.CreateValueCallback CreateEntry = delegate { return new Entry(); };
        private static ConditionalWeakTable<Pawn, RegisteredUser>
            registeredUsers = new ConditionalWeakTable<Pawn, RegisteredUser>();
        private static readonly ConditionalWeakTable<Pawn, RegisteredUser>.CreateValueCallback
            CreateRegisteredUser = delegate { return new RegisteredUser(); };
        private static ConditionalWeakTable<Map, MapUsers> mapUsers = new ConditionalWeakTable<Map, MapUsers>();
        private static readonly ConditionalWeakTable<Map, MapUsers>.CreateValueCallback
            CreateMapUsers = delegate { return new MapUsers(); };
        private static readonly Pawn[] NoQualifiedPawns = Array.Empty<Pawn>();
        private static int publishedTargetRulesRevision;
        private static HediffDef mindNumbSerumDef;
        private static HediffDef psychicBondDef;
        private static bool anomalyDefsResolved;

        // Read-only cache probe. A miss must not resolve a previously unseen Pawn.
        public static bool TryGetCachedAccess(
            Pawn pawn,
            out bool cachedAccess)
        {
            cachedAccess = false;

            if (!TryGetEntry(pawn, out Entry entry))
            {
                return false;
            }

            int accessState = entry.accessState;
            if (accessState == AccessUnknown)
            {
                return false;
            }

            cachedAccess = accessState == AccessGranted;
            return true;
        }

        internal static IReadOnlyList<Pawn> GetQualifiedPawns(Map map)
        {
            return map != null && mapUsers.TryGetValue(map, out MapUsers users)
                ? users.qualified
                : (IReadOnlyList<Pawn>)NoQualifiedPawns;
        }

        internal static bool IsCachedQualifiedPawn(Pawn pawn)
        {
            return TryGetEntry(pawn, out Entry entry) && entry.qualified;
        }

        internal static void InitializeMap(Map map)
        {
            if (map == null) return;
            MapUsers users = mapUsers.GetValue(map, CreateMapUsers);
            if (users.initialized) return;
            users.initialized = true;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++) NotifyPawnSpawned(pawns[i]);
        }

        internal static void NotifyPawnSpawned(Pawn pawn)
        {
            if (pawn?.Spawned != true || pawn.Dead) return;
            bool wasQualified = IsCachedQualifiedPawn(pawn);
            bool hasSource = HasAnyAccessSource(pawn);
            PublishAccess(pawn, hasSource);
            if (wasQualified && IsCachedQualifiedPawn(pawn))
                RimKataDormantHostileMovementRegistry.NotifyAccessChanged(pawn, true);
        }

        internal static void NotifyPawnDespawned(Pawn pawn)
        {
            if (TryGetEntry(pawn, out Entry entry)) RemovePublishedAccess(pawn, entry);
        }

        internal static void NotifyTargetChanged(Pawn pawn)
        {
            if (pawn?.Spawned != true) return;
            NotifyPawnSpawned(pawn);
            RimKataWeaponSlotUtility.NotifyTargetProfileChanged(pawn);
        }

        internal static void ForgetMap(Map map)
        {
            if (map == null || !mapUsers.TryGetValue(map, out MapUsers users)) return;
            foreach (Pawn pawn in users.permittedSources)
            {
                if (TryGetEntry(pawn, out Entry entry) && entry.publishedMap == map)
                {
                    entry.publishedMap = null;
                    entry.qualified = false;
                }
            }
            mapUsers.Remove(map);
        }

        internal static void ResetGame()
        {
            mapUsers = new ConditionalWeakTable<Map, MapUsers>();
            entries = new ConditionalWeakTable<Pawn, Entry>();
            registeredUsers = new ConditionalWeakTable<Pawn, RegisteredUser>();
            publishedTargetRulesRevision = RimKataTargetAccess.Revision;
        }

        internal static void RefreshFaction(Faction faction)
        {
            if (Current.Game == null) return;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (!mapUsers.TryGetValue(maps[i], out MapUsers users)) continue;
                foreach (Pawn pawn in users.permittedSources)
                {
                    if (pawn.Faction == faction && TryGetEntry(pawn, out Entry entry))
                        SetQualified(pawn, entry, users, RimKataEligibility.FactionEffectsEnabled(pawn));
                }
            }
        }

        internal static void RefreshPermissions()
        {
            if (Current.Game == null) return;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (!mapUsers.TryGetValue(maps[i], out MapUsers users)) continue;
                foreach (Pawn pawn in users.permittedSources)
                {
                    if (!TryGetEntry(pawn, out Entry entry)) continue;
                    bool wasQualified = entry.qualified;
                    SetQualified(pawn, entry, users, RimKataEligibility.FactionEffectsEnabled(pawn));
                    if (wasQualified && entry.qualified)
                        RimKataDormantHostileMovementRegistry.NotifyAccessChanged(pawn, true);
                }
            }
        }

        internal static void RefreshSettings()
        {
            if (Current.Game == null) return;
            int revision = RimKataTargetAccess.Revision;
            if (publishedTargetRulesRevision == revision)
            {
                RefreshPermissions();
                return;
            }
            publishedTargetRulesRevision = revision;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                // An override can qualify a previously negative Pawn; only this event needs all Pawns.
                IReadOnlyList<Pawn> pawns = maps[i].mapPawns.AllPawnsSpawned;
                for (int j = 0; j < pawns.Count; j++)
                {
                    NotifyPawnSpawned(pawns[j]);
                }
            }
        }

        private static void PublishAccess(Pawn pawn, bool hasSource)
        {
            if (pawn?.Spawned != true || pawn.Dead) return;
            Entry entry = entries.GetValue(pawn, CreateEntry);
            Map map = pawn.Map;
            bool permittedSource = hasSource || RimKataTargetAccess.IsEnabled(pawn);
            if (entry.publishedMap != null && (entry.publishedMap != map || !permittedSource))
                RemovePublishedAccess(pawn, entry);
            if (!permittedSource || map == null) return;
            MapUsers users = mapUsers.GetValue(map, CreateMapUsers);
            users.permittedSources.Add(pawn);
            entry.publishedMap = map;
            SetQualified(pawn, entry, users, RimKataEligibility.FactionEffectsEnabled(pawn));
        }

        private static void SetQualified(Pawn pawn, Entry entry, MapUsers users, bool qualified)
        {
            if (entry.qualified == qualified) return;
            entry.qualified = qualified;
            if (qualified)
            {
                users.qualifiedIndices.Add(pawn, users.qualified.Count);
                users.qualified.Add(pawn);
            }
            else if (users.qualifiedIndices.TryGetValue(pawn, out int index))
            {
                int lastIndex = users.qualified.Count - 1;
                Pawn last = users.qualified[lastIndex];
                users.qualified[index] = last;
                users.qualifiedIndices[last] = index;
                users.qualified.RemoveAt(lastIndex);
                users.qualifiedIndices.Remove(pawn);
            }
            RimKataDualWeaponController.InvalidateWeaponBindings(pawn);
            RimKataDormantHostileMovementRegistry.NotifyAccessChanged(pawn, qualified);
        }

        private static void RemovePublishedAccess(Pawn pawn, Entry entry)
        {
            if (entry.publishedMap != null && mapUsers.TryGetValue(entry.publishedMap, out MapUsers users))
            {
                SetQualified(pawn, entry, users, false);
                users.permittedSources.Remove(pawn);
            }
            entry.qualified = false;
            entry.publishedMap = null;
        }

        public static bool HasAnyAccessSource(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            if (registeredUsers.TryGetValue(pawn, out RegisteredUser _))
            {
                return true;
            }

            Entry entry = entries.GetValue(pawn, CreateEntry);
            int accessState = entry.accessState;
            if (accessState == AccessDenied)
            {
                return false;
            }

            if (accessState == AccessGranted)
            {
                UpdateRegisteredUser(pawn, true);
                return true;
            }

            lock (entry)
            {
                accessState = entry.accessState;
                if (accessState != AccessUnknown)
                {
                    bool cachedAccess = accessState == AccessGranted;
                    UpdateRegisteredUser(pawn, cachedAccess);
                    return cachedAccess;
                }

                bool hasAccess = ResolveAccess(pawn, entry);
                UpdateRegisteredUser(pawn, hasAccess);
                return StoreAccess(entry, hasAccess);
            }
        }

        public static bool IsRegisteredUser(Pawn pawn)
        {
            return pawn != null
                && registeredUsers.TryGetValue(
                    pawn,
                    out RegisteredUser _);
        }

        public static bool TryGetRegisteredSecondaryWeapon(
            Pawn pawn,
            out ThingWithComps secondaryWeapon)
        {
            secondaryWeapon = null;
            if (pawn == null
                || !registeredUsers.TryGetValue(
                    pawn,
                    out RegisteredUser registeredUser))
            {
                return false;
            }

            secondaryWeapon = registeredUser.secondaryWeapon;
            return true;
        }

        public static void NotifySecondaryWeaponChanged(
            Pawn pawn,
            ThingWithComps secondaryWeapon,
            bool accessVerified = false,
            bool slotVerified = false)
        {
            RimKataDualWeaponController.InvalidateWeaponBindings(pawn);
            UpdateRegisteredSecondaryWeapon(pawn, secondaryWeapon, accessVerified, slotVerified);
        }

        internal static void UpdateRegisteredSecondaryWeapon(
            Pawn pawn,
            ThingWithComps secondaryWeapon,
            bool accessVerified = false,
            bool slotVerified = false,
            ThingWithComps heldPairPrimary = null)
        {
            if (pawn != null
                && registeredUsers.TryGetValue(
                    pawn,
                    out RegisteredUser registeredUser))
            {
                registeredUser.secondaryWeapon = secondaryWeapon;
            }

            RimKataColonistBarWeaponCache.Set(
                pawn, secondaryWeapon, accessVerified, slotVerified, heldPairPrimary);
        }

        public static bool HasActiveDependencyGene(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            Entry entry = entries.GetValue(pawn, CreateEntry);
            lock (entry)
            {
                ResolveDependencyGene(pawn, entry);
                return entry.hasDependencyGene;
            }
        }

        public static Gene_MindNumbSerumDependency DependencyGene(Pawn pawn)
        {
            if (pawn == null)
            {
                return null;
            }

            Entry entry = entries.GetValue(pawn, CreateEntry);
            lock (entry)
            {
                ResolveDependencyGene(pawn, entry);
                return entry.dependencyGene;
            }
        }

        public static bool IsMindNumbed(Pawn pawn)
        {
            if (pawn?.health?.hediffSet == null)
            {
                return false;
            }

            Entry entry = entries.GetValue(pawn, CreateEntry);
            lock (entry)
            {
                if (!entry.mindNumbedKnown)
                {
                    ResolveAnomalyDefs();
                    entry.mindNumbed = mindNumbSerumDef != null
                        && pawn.health.hediffSet.HasHediff(mindNumbSerumDef);
                    entry.mindNumbedKnown = true;
                }

                return entry.mindNumbed;
            }
        }

        public static bool DependencyOvercomeByBond(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }

            Entry entry = entries.GetValue(pawn, CreateEntry);
            lock (entry)
            {
                if (!entry.bondKnown)
                {
                    entry.bond = ScanForOvercomingBond(pawn);
                    entry.bondKnown = true;
                }

                return entry.bond;
            }
        }

        public static void InvalidateGenes(Pawn pawn)
        {
            ThingWithComps registeredSecondary =
                BeginAccessInvalidation(pawn);
            if (TryGetEntry(pawn, out Entry entry))
            {
                lock (entry)
                {
                    entry.accessState = AccessUnknown;
                    entry.rimKataGeneKnown = false;
                    entry.dependencyGeneKnown = false;
                    entry.dependencyGene = null;
                }
            }

            FinishAccessInvalidation(pawn, registeredSecondary);
            RimKataWeaponSlotUtility.NotifyTargetProfileChanged(pawn);
        }

        public static void InvalidatePsycast(Pawn pawn)
        {
            ThingWithComps registeredSecondary =
                BeginAccessInvalidation(pawn);
            if (TryGetEntry(pawn, out Entry entry))
            {
                lock (entry)
                {
                    entry.accessState = AccessUnknown;
                    entry.psycastKnown = false;
                }
            }

            FinishAccessInvalidation(pawn, registeredSecondary);
        }

        internal static void InvalidateActivationSources(Pawn pawn)
        {
            ThingWithComps secondary = BeginAccessInvalidation(pawn);
            if (TryGetEntry(pawn, out Entry entry))
            {
                lock (entry)
                {
                    entry.accessState = AccessUnknown;
                    entry.rimKataGeneKnown = false;
                    entry.ampouleKnown = false;
                    entry.psycastKnown = false;
                    entry.roleKnown = false;
                    entry.dependencyGeneKnown = false;
                    entry.dependencyGene = null;
                }
            }
            FinishAccessInvalidation(pawn, secondary);
        }

        public static void InvalidateRole(Pawn pawn)
        {
            ThingWithComps registeredSecondary =
                BeginAccessInvalidation(pawn);
            if (TryGetEntry(pawn, out Entry entry))
            {
                lock (entry)
                {
                    entry.accessState = AccessUnknown;
                    entry.roleKnown = false;
                }
            }

            FinishAccessInvalidation(pawn, registeredSecondary);
        }

        public static void InvalidateRelations(Pawn pawn)
        {
            if (TryGetEntry(pawn, out Entry entry))
            {
                lock (entry)
                {
                    entry.bondKnown = false;
                }
            }
        }

        public static void InvalidateHediff(Pawn pawn, HediffDef changedDef)
        {
            if (changedDef == null)
            {
                return;
            }

            ThingWithComps registeredSecondary =
                changedDef == RimKataDefOf.RimKata_A_Effect
                    ? BeginAccessInvalidation(pawn)
                    : null;

            if (TryGetEntry(pawn, out Entry entry))
            {
                lock (entry)
                {
                    if (changedDef == RimKataDefOf.RimKata_A_Effect)
                    {
                        entry.accessState = AccessUnknown;
                        entry.ampouleKnown = false;
                    }

                    if (changedDef.defName == "MindNumbSerum")
                    {
                        entry.mindNumbedKnown = false;
                    }

                    if (changedDef.defName == "PsychicBond")
                    {
                        entry.bondKnown = false;
                    }
                }
            }

            if (changedDef == RimKataDefOf.RimKata_A_Effect)
            {
                FinishAccessInvalidation(pawn, registeredSecondary);
            }
        }

        private static ThingWithComps BeginAccessInvalidation(Pawn pawn)
        {
            RimKataDualWeaponController.InvalidateWeaponBindings(pawn);
            ThingWithComps registeredSecondary = null;
            if (pawn?.Spawned == true
                && !TryGetRegisteredSecondaryWeapon(pawn, out registeredSecondary)
                && IsCachedQualifiedPawn(pawn))
            {
                registeredSecondary = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn);
            }
            RemoveRegisteredUser(pawn);
            return registeredSecondary;
        }

        private static void FinishAccessInvalidation(
            Pawn pawn,
            ThingWithComps registeredSecondary)
        {
            if (pawn?.Spawned != true)
            {
                RimKataColonistBarWeaponCache.Refresh(pawn);
                return;
            }

            NotifyPawnSpawned(pawn);
            bool hasAccess = IsCachedQualifiedPawn(pawn);
            if (registeredSecondary == null)
            {
                RimKataColonistBarWeaponCache.Refresh(pawn);
            }
            else if (hasAccess)
            {
                UpdateRegisteredSecondaryWeapon(pawn, registeredSecondary, accessVerified: true);
            }
            else
            {
                RimKataWeaponSlotUtility.RemoveInvalidSecondary(
                    pawn, registeredSecondary);
            }
        }

        private static bool TryGetEntry(Pawn pawn, out Entry entry)
        {
            entry = null;
            return pawn != null && entries.TryGetValue(pawn, out entry);
        }

        private static bool StoreAccess(Entry entry, bool value)
        {
            entry.accessState = value
                ? AccessGranted
                : AccessDenied;
            return value;
        }

        private static bool ResolveAccess(Pawn pawn, Entry entry)
        {
            ResolveRimKataGene(pawn, entry);
            if (entry.hasRimKataGene)
            {
                return true;
            }

            ResolveAmpoule(pawn, entry);
            if (entry.hasAmpoule)
            {
                return true;
            }

            ResolvePsycast(pawn, entry);
            if (entry.hasPsycast)
            {
                return true;
            }

            ResolveRole(pawn, entry);
            if (entry.hasRole)
            {
                return true;
            }

            ResolveDependencyGene(pawn, entry);
            return entry.hasDependencyGene;
        }

        private static void UpdateRegisteredUser(Pawn pawn, bool hasAccess)
        {
            if (pawn == null)
            {
                return;
            }

            if (hasAccess)
            {
                RegisteredUser registeredUser = registeredUsers.GetValue(
                    pawn,
                    CreateRegisteredUser);
                registeredUser.secondaryWeapon =
                    RimKataSecondaryWeaponRegistry.CurrentRegistry?.Get(pawn);
            }
            else
            {
                registeredUsers.Remove(pawn);
            }
            PublishAccess(pawn, hasAccess);
        }

        private static void RemoveRegisteredUser(Pawn pawn)
        {
            if (pawn != null)
            {
                registeredUsers.Remove(pawn);
            }
        }

        private static void ResolveRimKataGene(Pawn pawn, Entry entry)
        {
            if (entry.rimKataGeneKnown)
            {
                return;
            }

            entry.hasRimKataGene = RimKataActivationSettings.GEnabled
                && ModsConfig.BiotechActive
                && pawn.genes != null
                && RimKataDefOf.RimKata_G != null
                && pawn.genes.HasActiveGene(RimKataDefOf.RimKata_G);
            entry.rimKataGeneKnown = true;
        }

        private static void ResolveAmpoule(Pawn pawn, Entry entry)
        {
            if (entry.ampouleKnown)
            {
                return;
            }

            entry.hasAmpoule = RimKataActivationSettings.AEnabled
                && RimKataDefOf.RimKata_A_Effect != null
                && pawn.health?.hediffSet?.HasHediff(RimKataDefOf.RimKata_A_Effect) == true;
            entry.ampouleKnown = true;
        }

        private static void ResolvePsycast(Pawn pawn, Entry entry)
        {
            if (entry.psycastKnown)
            {
                return;
            }

            entry.hasPsycast = RimKataActivationSettings.PEnabled
                && ModsConfig.RoyaltyActive
                && RimKataDefOf.RimKata_P != null
                && pawn.abilities?.GetAbility(RimKataDefOf.RimKata_P, true) != null;
            entry.psycastKnown = true;
        }

        private static void ResolveRole(Pawn pawn, Entry entry)
        {
            if (entry.roleKnown)
            {
                return;
            }

            entry.hasRole = RimKataActivationSettings.IEnabled
                && ModsConfig.IdeologyActive
                && RimKataDefOf.RimKata_I != null
                && pawn.Ideo?.GetRole(pawn)?.def == RimKataDefOf.RimKata_I;
            entry.roleKnown = true;
        }

        private static void ResolveDependencyGene(Pawn pawn, Entry entry)
        {
            if (entry.dependencyGeneKnown)
            {
                return;
            }

            GeneDef geneDef = RimKataAnomalyUtility.DependencyGeneDef;
            Gene gene = geneDef == null || !RimKataActivationSettings.DependencyEnabled
                ? null : pawn.genes?.GetGene(geneDef);
            entry.dependencyGene = gene as Gene_MindNumbSerumDependency;
            entry.hasDependencyGene = gene?.Active == true;
            entry.dependencyGeneKnown = true;
        }

        private static bool ScanForOvercomingBond(Pawn pawn)
        {
            if (pawn.relations?.DirectRelations != null)
            {
                for (int i = 0; i < pawn.relations.DirectRelations.Count; i++)
                {
                    if (IsOvercomingRelation(pawn.relations.DirectRelations[i].def))
                    {
                        return true;
                    }
                }
            }

            if (pawn.relations?.VirtualRelations != null)
            {
                for (int i = 0; i < pawn.relations.VirtualRelations.Count; i++)
                {
                    if (IsOvercomingRelation(pawn.relations.VirtualRelations[i].def))
                    {
                        return true;
                    }
                }
            }

            ResolveAnomalyDefs();
            return psychicBondDef != null && pawn.health?.hediffSet?.HasHediff(psychicBondDef) == true;
        }

        private static bool IsOvercomingRelation(PawnRelationDef relation)
        {
            return relation == PawnRelationDefOf.Lover
                || relation == PawnRelationDefOf.Fiance
                || relation == PawnRelationDefOf.Spouse;
        }

        private static void ResolveAnomalyDefs()
        {
            if (anomalyDefsResolved)
            {
                return;
            }

            mindNumbSerumDef = DefDatabase<HediffDef>.GetNamedSilentFail("MindNumbSerum");
            psychicBondDef = DefDatabase<HediffDef>.GetNamedSilentFail("PsychicBond");
            anomalyDefsResolved = true;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    internal static class Patch_PawnDeSpawn_RimKataEligibilityCache
    {
        private static void Prefix(Pawn __instance)
        {
            RimKataEligibilityCache.NotifyPawnDespawned(__instance);
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), "Notify_GenesChanged")]
    public static class Patch_PawnGeneTracker_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.InvalidateGenes(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.SetXenotype))]
    internal static class Patch_PawnGeneTracker_SetXenotype_RimKataTargetAccess
    {
        private static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.NotifyTargetChanged(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.SetXenotypeDirect))]
    internal static class Patch_PawnGeneTracker_SetXenotypeDirect_RimKataTargetAccess
    {
        private static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.NotifyTargetChanged(___pawn);
        }
    }

    // These operations can set the custom name after their last gene notification.
    [HarmonyPatch(typeof(GeneUtility), nameof(GeneUtility.ReimplantXenogerm))]
    internal static class Patch_GeneUtility_Reimplant_RimKataTargetAccess
    {
        private static void Postfix(Pawn recipient)
        {
            RimKataEligibilityCache.NotifyTargetChanged(recipient);
        }
    }

    [HarmonyPatch(typeof(GeneUtility), nameof(GeneUtility.ImplantXenogermItem))]
    internal static class Patch_GeneUtility_ImplantItem_RimKataTargetAccess
    {
        private static void Postfix(Pawn pawn)
        {
            RimKataEligibilityCache.NotifyTargetChanged(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.GainAbility))]
    public static class Patch_PawnAbilityTracker_Gain_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn, AbilityDef __0)
        {
            if (__0 == RimKataDefOf.RimKata_P)
            {
                RimKataEligibilityCache.InvalidatePsycast(___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.RemoveAbility))]
    public static class Patch_PawnAbilityTracker_Remove_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn, AbilityDef __0)
        {
            if (__0 == RimKataDefOf.RimKata_P)
            {
                RimKataEligibilityCache.InvalidatePsycast(___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_IdeoTracker), nameof(Pawn_IdeoTracker.SetIdeo))]
    public static class Patch_PawnIdeoTracker_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.InvalidateRole(___pawn);
        }
    }

    [HarmonyPatch(typeof(Precept_RoleMulti), nameof(Precept_RoleMulti.Assign))]
    public static class Patch_PreceptRoleMulti_Assign_RimKataEligibilityCache
    {
        public static void Postfix(Precept_RoleMulti __instance, Pawn p)
        {
            if (__instance?.def == RimKataDefOf.RimKata_I)
            {
                RimKataEligibilityCache.InvalidateRole(p);
            }
        }
    }

    [HarmonyPatch(typeof(Precept_RoleMulti), nameof(Precept_RoleMulti.Unassign))]
    public static class Patch_PreceptRoleMulti_Unassign_RimKataEligibilityCache
    {
        public static void Postfix(Precept_RoleMulti __instance, Pawn p)
        {
            if (__instance?.def == RimKataDefOf.RimKata_I)
            {
                RimKataEligibilityCache.InvalidateRole(p);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_HediffChanged))]
    public static class Patch_PawnHealthTracker_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn, Hediff __0)
        {
            if (__0?.Part != null)
            {
                RimKataWeaponSlotUtility.InvalidateCombatVerbCache(___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff), new Type[]
    {
        typeof(Hediff),
        typeof(BodyPartRecord),
        typeof(DamageInfo?),
        typeof(DamageWorker.DamageResult)
    })]

    public static class Patch_PawnHealthTracker_AddHediff_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn, Hediff __0)
        {
            RimKataEligibilityCache.InvalidateHediff(___pawn, __0?.def);
            RimKataSecondaryHandRequirement.NotifyMissingPartAdded(___pawn, __0);
            if (__0?.def?.defName == "MindNumbSerum")
            {
                Gene_MindNumbSerumDependency gene =
                    RimKataAnomalyUtility.DependencyGene(___pawn);
                if (gene?.Active == true)
                {
                    gene.ResetWithoutSerumTicks();
                }
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.RemoveHediff))]
    public static class Patch_PawnHealthTracker_RemoveHediff_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn, Hediff __0)
        {
            RimKataEligibilityCache.InvalidateHediff(___pawn, __0?.def);
        }
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), "GainedOrLostDirectRelation")]
    public static class Patch_PawnRelationsTracker_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.InvalidateRelations(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.RemoveRelation))]
    public static class Patch_PawnRelationsTracker_RemoveVirtual_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.InvalidateRelations(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), "CleanupVirtualRelationReferences")]
    public static class Patch_PawnRelationsTracker_CleanupVirtual_RimKataEligibilityCache
    {
        public static void Postfix(Pawn ___pawn)
        {
            RimKataEligibilityCache.InvalidateRelations(___pawn);
        }
    }
}
