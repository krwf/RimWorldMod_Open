using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataStrengthUtility
    {
        private sealed class Entry
        {
            internal readonly float bonus;
            internal readonly bool hasHands;
            internal Entry(float bonus, bool hasHands)
            { this.bonus = bonus; this.hasHands = hasHands; }
        }

        private static readonly ConcurrentDictionary<Pawn, Entry> entries = new ConcurrentDictionary<Pawn, Entry>();
        private static readonly Dictionary<ThingDef, RimKataStrengthRule> apparelRules = new Dictionary<ThingDef, RimKataStrengthRule>();
        private static readonly Dictionary<HediffDef, RimKataStrengthRule> implantRules = new Dictionary<HediffDef, RimKataStrengthRule>();
        private static readonly HashSet<Pawn> refreshing = new HashSet<Pawn>();
        private static readonly HashSet<Pawn> installing = new HashSet<Pawn>();
        private static readonly Dictionary<Pawn, bool> droppingApparel = new Dictionary<Pawn, bool>();
        private static bool rulesReady;
        private static StatDef ceCarryWeight;
        [ThreadStatic] private static Pawn bodyMassPawn;

        internal static void Initialize()
        {
            ceCarryWeight = DefDatabase<StatDef>.GetNamedSilentFail("CarryWeight");
            if (RimKataActiveModTypes.Find("CombatExtended.CompInventory") == null)
                ceCarryWeight = null;
            if (ceCarryWeight != null)
            {
                ceCarryWeight.parts ??= new List<StatPart>();
                if (!ceCarryWeight.parts.Exists(part => part is StatPart_RimKataStrength))
                    ceCarryWeight.parts.Add(new StatPart_RimKataStrength { parentStat = ceCarryWeight });
            }
            RebuildRules();
        }

        internal static bool UsesCombatExtendedWeight => ceCarryWeight != null;
        internal static bool HasEnhancedGrip(Pawn pawn)
            => pawn != null && entries.TryGetValue(pawn, out Entry entry) && entry.hasHands;
        internal static float Bonus(Pawn pawn)
            => pawn != null && entries.TryGetValue(pawn, out Entry entry) ? entry.bonus : 0f;
        internal static float IncreaseCapacity(float capacity, float bonus)
            => (float)Math.Min((double)capacity * (1d + bonus), float.MaxValue);
        internal static bool IsBodyMassRequest(StatRequest request)
            => bodyMassPawn != null && request.Thing == bodyMassPawn;

        internal static float BodyMass(Pawn pawn)
        {
            Pawn previous = bodyMassPawn;
            bodyMassPawn = pawn;
            try { return StatDefOf.Mass.Worker.GetValue(StatRequest.For(pawn)); }
            finally { bodyMassPawn = previous; }
        }

        internal static void ResetGame()
        {
            entries.Clear();
            refreshing.Clear();
            installing.Clear();
            droppingApparel.Clear();
        }

        private static void RebuildRules()
        {
            apparelRules.Clear();
            implantRules.Clear();
            List<RimKataStrengthRule> rules = RimKataMod.Settings?.strengthRules;
            if (rules != null) foreach (RimKataStrengthRule rule in rules)
            {
                if (rule == null || (!rule.enabled && rule.AdditionalPercent <= 0f)
                    || rule.defName.NullOrEmpty()) continue;
                if (rule.sourceKind == RimKataStrengthSourceKind.Apparel)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(rule.defName);
                    if (def?.IsApparel == true) apparelRules[def] = rule;
                }
                else
                {
                    HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail(rule.defName);
                    if (def != null) implantRules[def] = rule;
                }
            }
            rulesReady = true;
        }

        internal static void NotifySettingsChanged(bool notifyLoadout = true)
        {
            RebuildRules();
            RefreshAll(notifyLoadout);
        }

        internal static void RefreshAll(bool notifyLoadout = true)
        {
            if (Current.Game == null) return;
            var pawns = new HashSet<Pawn>(entries.Keys);
            foreach (Map map in Find.Maps)
            {
                IReadOnlyList<Pawn> qualified = RimKataEligibilityCache.GetQualifiedPawns(map);
                if (qualified == null) continue;
                for (int i = 0; i < qualified.Count; i++) pawns.Add(qualified[i]);
            }
            if (Find.WorldObjects != null) foreach (Caravan caravan in Find.WorldObjects.Caravans)
                foreach (Pawn pawn in caravan.PawnsListForReading) pawns.Add(pawn);
            foreach (Pawn pawn in pawns)
            {
                Refresh(pawn, notifyLoadout: notifyLoadout);
                if (!notifyLoadout && !pawn.Spawned) RimKataCaravanEquipment.NormalizeStrengthLoadout(pawn);
            }
        }

        internal static void InitializeCaravans()
        {
            if (Current.Game == null || Find.WorldObjects == null) return;
            foreach (Caravan caravan in Find.WorldObjects.Caravans)
                foreach (Pawn pawn in caravan.PawnsListForReading) Refresh(pawn);
        }

        internal static void NotifyQualificationChanged(Pawn pawn, bool qualified)
            => Refresh(pawn, qualified, false);

        internal static void Refresh(Pawn pawn, bool? qualified = null, bool notifyLoadout = true)
        {
            if (pawn == null || installing.Contains(pawn) || !refreshing.Add(pawn)) return;
            try
            {
                if (!rulesReady) RebuildRules();
                entries.TryGetValue(pawn, out Entry previous);
                if (previous == null && apparelRules.Count == 0 && implantRules.Count == 0) return;
                Entry current = null;
                bool allowed = !pawn.Dead && (qualified ?? (pawn.Spawned
                    ? RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    : RimKataEligibility.HasRimKataAccess(pawn)));
                if (allowed && (apparelRules.Count != 0 || implantRules.Count != 0))
                {
                    bool enhancedGrip = false;
                    double percent = 0d;
                    float defaultPercent = RimKataTargetAccess.SettingsFor(pawn)?.strengthIncreasePercent ?? 0f;
                    List<Apparel> apparel = pawn.apparel?.WornApparel;
                    if (apparel != null) foreach (Apparel item in apparel)
                    {
                        if (!apparelRules.TryGetValue(item.def, out RimKataStrengthRule rule)) continue;
                        enhancedGrip |= rule.enabled;
                        percent += rule.EffectivePercent(defaultPercent);
                    }
                    List<Hediff> hediffs = pawn.health?.hediffSet?.hediffs;
                    if (hediffs != null) foreach (Hediff hediff in hediffs)
                    {
                        if (!implantRules.TryGetValue(hediff.def, out RimKataStrengthRule rule)
                            || (hediff.Part != null && pawn.health.hediffSet.PartIsMissing(hediff.Part))) continue;
                        enhancedGrip |= rule.enabled;
                        percent += rule.EffectivePercent(defaultPercent);
                    }
                    if (enhancedGrip || percent > 0d)
                        current = new Entry((float)Math.Min(percent / 100d, float.MaxValue),
                            enhancedGrip && !RimKataSecondaryHandRequirement.HasMissingHand(pawn));
                }
                if (current == null) entries.TryRemove(pawn, out _);
                else entries[pawn] = current;
                RimKataResponseVisualParticipantCache.NotifyStrengthChanged(pawn, current?.hasHands == true);
                if ((previous == null) == (current == null)
                    && (current == null || (previous.bonus == current.bonus && previous.hasHands == current.hasHands))) return;
                ceCarryWeight?.Worker.ClearCacheForThing(pawn);
                RimKataDownedWeaponUtility.NotifyStrengthChanged(pawn);
                RimKataSubdueUtility.NotifyEquipmentChanged(pawn);
                if (!notifyLoadout) return;
                RimKataWeaponSlotUtility.NotifyLoadoutChanged(pawn);
                if (pawn.Spawned) RimKataWeaponSlotUtility.ValidateLoadout(pawn);
                else RimKataCaravanEquipment.NormalizeStrengthLoadout(pawn);
                RimKataColonistBarWeaponCache.Refresh(pawn);
            }
            finally { refreshing.Remove(pawn); }
        }

        internal static void NotifyApparelChanged(Pawn pawn, Apparel apparel)
        {
            if (!rulesReady) RebuildRules();
            if (pawn == null || apparel?.def == null || !apparelRules.ContainsKey(apparel.def)) return;
            if (droppingApparel.ContainsKey(pawn)) droppingApparel[pawn] = true;
            else Refresh(pawn);
        }

        internal static bool BeginApparelDrop(Pawn pawn, Apparel apparel)
        {
            if (!rulesReady) RebuildRules();
            if (pawn == null || apparel?.def == null || !apparelRules.ContainsKey(apparel.def)
                || droppingApparel.ContainsKey(pawn)) return false;
            droppingApparel.Add(pawn, false);
            return true;
        }

        internal static void EndApparelDrop(Pawn pawn)
        {
            if (!droppingApparel.TryGetValue(pawn, out bool changed)) return;
            droppingApparel.Remove(pawn);
            if (changed) Refresh(pawn);
        }

        internal static void NotifyHediffChanged(Pawn pawn, Hediff hediff)
        {
            if (!rulesReady) RebuildRules();
            if (hediff is Hediff_MissingPart || (hediff?.def != null && implantRules.ContainsKey(hediff.def)))
                Refresh(pawn);
        }

        internal static void NotifyPartRestored(Pawn pawn)
        {
            if (!rulesReady) RebuildRules();
            if (implantRules.Count != 0 || entries.ContainsKey(pawn)) Refresh(pawn);
        }

        internal static bool BeginInstallation(Pawn pawn)
        {
            if (!rulesReady) RebuildRules();
            return pawn != null && implantRules.Count != 0 && installing.Add(pawn);
        }

        internal static void EndInstallation(Pawn pawn)
        {
            if (installing.Remove(pawn)) Refresh(pawn);
        }
    }

    public sealed class RimKataStrengthGameComponent : GameComponent
    {
        public RimKataStrengthGameComponent(Game game) { }
        public override void FinalizeInit()
            => LongEventHandler.ExecuteWhenFinished(RimKataStrengthUtility.InitializeCaravans);
    }

    internal sealed class StatPart_RimKataStrength : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.Thing is Pawn pawn) val = RimKataStrengthUtility.IncreaseCapacity(val, RimKataStrengthUtility.Bonus(pawn));
        }
        public override string ExplanationPart(StatRequest req)
        {
            float bonus = req.Thing is Pawn pawn ? RimKataStrengthUtility.Bonus(pawn) : 0f;
            return bonus > 0f ? "KRWF_RimKata_StrengthIncrease".Translate() + ": " + bonus.ToStringPercent() : null;
        }
    }

    [HarmonyPatch(typeof(StatPart_GearAndInventoryMass), nameof(StatPart_GearAndInventoryMass.TransformValue))]
    internal static class Patch_StatPartGearMass_RimKataBodyMass
    {
        private static bool Prefix(StatRequest req) => !RimKataStrengthUtility.IsBodyMassRequest(req);
    }

    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    internal static class Patch_MassUtility_RimKataStrength
    {
        private static void Postfix(Pawn p, StringBuilder explanation, ref float __result)
        {
            if (RimKataStrengthUtility.UsesCombatExtendedWeight || __result <= 0f) return;
            float bonus = RimKataStrengthUtility.Bonus(p);
            if (bonus <= 0f) return;
            __result = RimKataStrengthUtility.IncreaseCapacity(__result, bonus);
            explanation?.Append(" (" + "KRWF_RimKata_StrengthIncrease".Translate() + " +" + bonus.ToStringPercent() + ")");
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelAdded))]
    internal static class Patch_ApparelAdded_RimKataStrength
    {
        private static void Postfix(Pawn ___pawn, Apparel apparel) => RimKataStrengthUtility.NotifyApparelChanged(___pawn, apparel);
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelRemoved))]
    internal static class Patch_ApparelRemoved_RimKataStrength
    {
        private static void Postfix(Pawn ___pawn, Apparel apparel) => RimKataStrengthUtility.NotifyApparelChanged(___pawn, apparel);
    }

    [HarmonyPatch]
    internal static class Patch_ApparelDrop_RimKataStrength
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.TryDrop),
            new[] { typeof(Apparel), typeof(Apparel).MakeByRefType(), typeof(IntVec3), typeof(bool) });

        private static void Prefix(Pawn ___pawn, Apparel ap, out bool __state)
            => __state = RimKataStrengthUtility.BeginApparelDrop(___pawn, ap);

        private static Exception Finalizer(Pawn ___pawn, bool __state, Exception __exception)
        {
            if (!__state) return __exception;
            try { RimKataStrengthUtility.EndApparelDrop(___pawn); }
            catch (Exception exception)
            {
                if (__exception == null) return exception;
                Log.Error("[RimKata] Strength refresh after apparel drop failed: " + exception);
            }
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.RestorePart))]
    internal static class Patch_RestorePart_RimKataStrength
    {
        private static void Postfix(Pawn ___pawn) => RimKataStrengthUtility.NotifyPartRestored(___pawn);
    }

    [HarmonyPatch(typeof(Caravan), nameof(Caravan.AddPawn))]
    internal static class Patch_CaravanAddPawn_RimKataStrength
    {
        private static void Postfix(Pawn p) => RimKataStrengthUtility.Refresh(p);
    }

    [HarmonyPatch(typeof(Caravan), nameof(Caravan.RemovePawn))]
    internal static class Patch_CaravanRemovePawn_RimKataStrength
    {
        private static void Postfix(Pawn p) => RimKataStrengthUtility.Refresh(p, false, false);
    }

    [HarmonyPatch(typeof(Recipe_InstallArtificialBodyPart), nameof(Recipe_InstallArtificialBodyPart.ApplyOnPawn))]
    internal static class Patch_InstallPart_RimKataStrength
    {
        private static void Prefix(Pawn pawn, out bool __state)
            => __state = RimKataStrengthUtility.BeginInstallation(pawn);
        private static Exception Finalizer(Pawn pawn, bool __state, Exception __exception)
        {
            if (!__state) return __exception;
            try { RimKataStrengthUtility.EndInstallation(pawn); }
            catch (Exception exception)
            {
                if (__exception == null) return exception;
                Log.Error("[RimKata] Strength refresh after implant installation failed: " + exception);
            }
            return __exception;
        }
    }
}
