using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public static class RimKataActivationSettings
    {
        private sealed class ItemGenerationDefaults
        {
            public float commonality;
            public float allowChance;
            public Tradeability tradeability;
        }

        private sealed class GeneGenerationDefaults
        {
            public float selectionWeight;
            public bool canGenerate;
        }

        private static readonly Dictionary<ThingDef, ItemGenerationDefaults> itemDefaults =
            new Dictionary<ThingDef, ItemGenerationDefaults>();
        private static readonly Dictionary<GeneDef, GeneGenerationDefaults> geneDefaults =
            new Dictionary<GeneDef, GeneGenerationDefaults>();
        private static PreceptDef roleDef;
        private static float roleSelectionWeight;
        private static bool roleCanGenerate;
        private static HediffDef ampouleEffectDef;
        private static bool ampouleScenarioCanAdd;
        private static int appliedMask = -1;
        private static Game appliedGame;
        private static bool applying;
        private static List<GeneDef> unfilteredGeneList;
        private static List<GeneDef> filteredGeneList;
        private static int filteredGeneMask = -1;

        public static bool AEnabled => RimKataMod.Settings?.enableRimKataA != false;
        public static bool PEnabled => RimKataMod.Settings?.enableRimKataP != false;
        public static bool IEnabled => RimKataMod.Settings?.enableRimKataI != false;
        public static bool GEnabled => RimKataMod.Settings?.enableRimKataG != false;
        public static bool DependencyEnabled => RimKataMod.Settings?.enableSerumDependency != false;

        public static bool IsGeneEnabled(GeneDef gene)
        {
            if (gene == null) return true;
            if (gene == RimKataDefOf.RimKata_G) return GEnabled;
            if (gene.defName == RimKataAnomalyUtility.DependencyGeneDefName)
                return DependencyEnabled;
            return true;
        }

        internal static bool IsItemEnabled(ThingDef def)
        {
            if (def == null || (AEnabled && PEnabled)) return true;
            if (def.defName == "RimKata_A") return AEnabled;
            if (!PEnabled && def.comps != null)
            {
                for (int i = 0; i < def.comps.Count; i++)
                    if (def.comps[i] is CompProperties_UseEffect_GainAbility comp
                        && comp.ability == RimKataDefOf.RimKata_P)
                        return false;
            }
            return true;
        }

        private static bool IsAccessItem(ThingDef def)
        {
            if (def.defName == "RimKata_A") return true;
            if (RimKataDefOf.RimKata_P == null || def.comps == null) return false;
            for (int i = 0; i < def.comps.Count; i++)
                if (def.comps[i] is CompProperties_UseEffect_GainAbility comp
                    && comp.ability == RimKataDefOf.RimKata_P)
                    return true;
            return false;
        }

        internal static bool IsRecipeEnabled(RecipeDef recipe)
        {
            if ((AEnabled && PEnabled) || recipe?.products == null) return true;
            for (int i = 0; i < recipe.products.Count; i++)
                if (!IsItemEnabled(recipe.products[i].thingDef)) return false;
            return true;
        }

        private static int Mask => (AEnabled ? 1 : 0) | (PEnabled ? 2 : 0)
            | (IEnabled ? 4 : 0) | (GEnabled ? 8 : 0)
            | (DependencyEnabled ? 16 : 0);

        public static void Apply(bool force = false)
        {
            RimKataMod.Settings?.SanitizeCreepJoinerGeneChances();
            if (applying) return;
            int mask = Mask;
            if (!force && appliedMask == mask && appliedGame == Current.Game) return;
            applying = true;
            try
            {
                ApplyDefinitionAvailability();
                filteredGeneMask = -1;
                if (Current.Game != null && Find.World != null)
                {
                    // This snapshot also includes caravans, suspended Pawns,
                    // corpses, and temporary generation holders.
                    List<Pawn> pawns = new List<Pawn>(
                        PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead);
                    HashSet<Ideo> ideos = new HashSet<Ideo>();
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        Pawn pawn = pawns[i];
                        if (pawn == null) continue;
                        if (pawn.Ideo != null) ideos.Add(pawn.Ideo);
                        RemoveDisabledSources(pawn);
                        if (pawn.Spawned && !IsRecipeEnabled(pawn.CurJob?.bill?.recipe))
                            pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                        RimKataEligibilityCache.InvalidateActivationSources(pawn);
                        RimKataWeaponSlotUtility.NotifyTargetProfileChanged(pawn);
                        StopUnavailableCombat(pawn);
                    }
                    if (!IEnabled && Find.IdeoManager != null)
                    {
                        foreach (Ideo ideo in Find.IdeoManager.IdeosListForReading)
                            ideos.Add(ideo);
                        foreach (Ideo ideo in ideos)
                        {
                            List<Precept> precepts = ideo.PreceptsListForReading;
                            for (int i = precepts.Count - 1; i >= 0; i--)
                                if (precepts[i].def == RimKataDefOf.RimKata_I)
                                    ideo.RemovePrecept(precepts[i]);
                        }
                    }
                    RimKataEligibilityCache.RefreshSettings();
                    RimKataWeaponSlotUtility.NormalizeAllSpawnedLoadouts();
                }
                appliedMask = mask;
                appliedGame = Current.Game;
            }
            finally
            {
                applying = false;
            }
        }

        internal static void RemoveDisabledSources(Pawn pawn)
        {
            if (pawn == null) return;
            if (!AEnabled && pawn.health?.hediffSet?.hediffs != null)
            {
                List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
                for (int i = hediffs.Count - 1; i >= 0; i--)
                    if (hediffs[i].def == RimKataDefOf.RimKata_A_Effect)
                        pawn.health.RemoveHediff(hediffs[i]);
            }
            if (!PEnabled && RimKataDefOf.RimKata_P != null
                && pawn.abilities?.GetAbility(RimKataDefOf.RimKata_P, true) != null)
                pawn.abilities.RemoveAbility(RimKataDefOf.RimKata_P);
            if (!IEnabled && pawn.Ideo?.GetRole(pawn) is Precept_Role role
                && role.def == RimKataDefOf.RimKata_I)
                role.Unassign(pawn, false);
            if (pawn.genes != null && (!GEnabled || !DependencyEnabled))
            {
                List<Gene> genes = new List<Gene>(pawn.genes.GenesListForReading);
                for (int i = genes.Count - 1; i >= 0; i--)
                    if (!IsGeneEnabled(genes[i].def)) pawn.genes.RemoveGene(genes[i]);
                pawn.needs?.mood?.thoughts?.situational?.Notify_SituationalThoughtsDirty();
            }
        }

        private static void StopUnavailableCombat(Pawn pawn)
        {
            if (!pawn.Spawned || RimKataEligibility.HasRimKataAccess(pawn)) return;
            if (RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner))
            {
                RimKataPawnCombatState state = owner.GetState(pawn, false);
                RimKataDualWeaponController.CancelOffenseForMentalState(pawn, state);
            }
            if (pawn.CurJobDef == RimKataDefOf.RimKata_Attack)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        private static void ApplyDefinitionAvailability()
        {
            List<ThingDef> things = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < things.Count; i++)
            {
                ThingDef def = things[i];
                if (!IsAccessItem(def)) continue;
                if (!itemDefaults.TryGetValue(def, out ItemGenerationDefaults defaults))
                {
                    defaults = new ItemGenerationDefaults
                    {
                        commonality = def.generateCommonality,
                        allowChance = def.generateAllowChance,
                        tradeability = def.tradeability
                    };
                    itemDefaults.Add(def, defaults);
                }
                bool enabled = IsItemEnabled(def);
                def.generateCommonality = enabled ? defaults.commonality : 0f;
                def.generateAllowChance = enabled ? defaults.allowChance : 0f;
                def.tradeability = enabled ? defaults.tradeability : Tradeability.None;
            }
            ApplyGeneAvailability(RimKataDefOf.RimKata_G);
            ApplyGeneAvailability(RimKataAnomalyUtility.DependencyGeneDef);
            PreceptDef currentRole = RimKataDefOf.RimKata_I;
            if (currentRole != null)
            {
                if (roleDef != currentRole)
                {
                    roleDef = currentRole;
                    roleSelectionWeight = roleDef.selectionWeight;
                    roleCanGenerate = roleDef.canGenerateAsSpecialPrecept;
                }
                roleDef.selectionWeight = IEnabled ? roleSelectionWeight : 0f;
                roleDef.canGenerateAsSpecialPrecept = IEnabled && roleCanGenerate;
            }
            HediffDef currentEffect = RimKataDefOf.RimKata_A_Effect;
            if (currentEffect != null)
            {
                if (ampouleEffectDef != currentEffect)
                {
                    ampouleEffectDef = currentEffect;
                    ampouleScenarioCanAdd = currentEffect.scenarioCanAdd;
                }
                currentEffect.scenarioCanAdd = AEnabled && ampouleScenarioCanAdd;
            }
        }

        private static void ApplyGeneAvailability(GeneDef def)
        {
            if (def == null) return;
            if (!geneDefaults.TryGetValue(def, out GeneGenerationDefaults defaults))
            {
                defaults = new GeneGenerationDefaults
                { selectionWeight = def.selectionWeight, canGenerate = def.canGenerateInGeneSet };
                geneDefaults.Add(def, defaults);
            }
            bool enabled = IsGeneEnabled(def);
            def.selectionWeight = enabled ? defaults.selectionWeight : 0f;
            def.canGenerateInGeneSet = enabled && defaults.canGenerate;
        }

        internal static List<GeneDef> FilterSelectableGenes(List<GeneDef> genes)
        {
            if (genes == null || (GEnabled && DependencyEnabled)) return genes;
            int mask = Mask;
            if (unfilteredGeneList != genes || filteredGeneMask != mask)
            {
                unfilteredGeneList = genes;
                filteredGeneMask = mask;
                filteredGeneList = genes.FindAll(IsGeneEnabled);
            }
            return filteredGeneList;
        }
    }

    public sealed class RimKataActivationGameComponent : GameComponent
    {
        public RimKataActivationGameComponent(Game game) { }
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            RimKataActivationSettings.Apply(true);
        }
    }

    [HarmonyPatch(typeof(Pawn_GeneTracker), nameof(Pawn_GeneTracker.AddGene),
        new Type[] { typeof(Gene), typeof(bool) })]
    internal static class Patch_PawnGenes_RimKataActivation
    {
        public static bool Prefix(Gene __0, ref Gene __result)
        {
            if (RimKataActivationSettings.IsGeneEnabled(__0?.def)) return true;
            // AddGene(GeneDef, ...) first creates this Gene with its Pawn set.
            // Keep the non-null return expected by callers, without inserting
            // it into either gene list or running its PostAdd effects.
            __result = __0;
            return false;
        }
    }

    [HarmonyPatch(typeof(GeneSet), nameof(GeneSet.AddGene))]
    internal static class Patch_GeneSet_RimKataActivation
    {
        public static bool Prefix(GeneDef __0) => RimKataActivationSettings.IsGeneEnabled(__0);
    }

    [HarmonyPatch(typeof(GeneUtility), nameof(GeneUtility.GenesInOrder), MethodType.Getter)]
    internal static class Patch_GeneSelection_RimKataActivation
    {
        public static void Postfix(ref List<GeneDef> __result) =>
            __result = RimKataActivationSettings.FilterSelectableGenes(__result);
    }

    [HarmonyPatch(typeof(Pawn_AbilityTracker), nameof(Pawn_AbilityTracker.GainAbility))]
    internal static class Patch_AbilityGain_RimKataActivation
    {
        public static bool Prefix(AbilityDef __0) =>
            __0 != RimKataDefOf.RimKata_P || RimKataActivationSettings.PEnabled;
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff),
        new Type[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
    internal static class Patch_AmpouleHediff_RimKataActivation
    {
        public static bool Prefix(Hediff __0) =>
            __0?.def != RimKataDefOf.RimKata_A_Effect || RimKataActivationSettings.AEnabled;
    }

    [HarmonyPatch(typeof(Precept_RoleMulti), nameof(Precept_RoleMulti.Assign))]
    internal static class Patch_RoleAssign_RimKataActivation
    {
        public static bool Prefix(Precept_RoleMulti __instance) =>
            __instance.def != RimKataDefOf.RimKata_I || RimKataActivationSettings.IEnabled;
    }

    [HarmonyPatch(typeof(Ideo), nameof(Ideo.AddPrecept))]
    internal static class Patch_IdeoPrecept_RimKataActivation
    {
        public static bool Prefix(Precept __0) =>
            __0?.def != RimKataDefOf.RimKata_I || RimKataActivationSettings.IEnabled;
    }

    [HarmonyPatch(typeof(IdeoUIUtility), nameof(IdeoUIUtility.CanListPrecept))]
    internal static class Patch_RoleSelection_RimKataActivation
    {
        public static void Postfix(PreceptDef __1, ref AcceptanceReport __result)
        {
            if (__1 == RimKataDefOf.RimKata_I && !RimKataActivationSettings.IEnabled)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(RecipeDef), nameof(RecipeDef.AvailableNow), MethodType.Getter)]
    internal static class Patch_AccessRecipe_RimKataActivation
    {
        public static void Postfix(RecipeDef __instance, ref bool __result)
        {
            if (__result && !RimKataActivationSettings.IsRecipeEnabled(__instance))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(RecipeDef), nameof(RecipeDef.AvailableOnNow))]
    internal static class Patch_AccessRecipeAvailableOn_RimKataActivation
    {
        public static void Postfix(RecipeDef __instance, ref bool __result)
        {
            if (__result && !RimKataActivationSettings.IsRecipeEnabled(__instance))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.ShouldDoNow))]
    internal static class Patch_AccessBill_RimKataActivation
    {
        public static void Postfix(Bill_Production __instance, ref bool __result)
        {
            if (__result && !RimKataActivationSettings.IsRecipeEnabled(__instance.recipe))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.IngestibleNow), MethodType.Getter)]
    internal static class Patch_AmpouleIngestible_RimKataActivation
    {
        public static void Postfix(Thing __instance, ref bool __result)
        {
            if (!RimKataActivationSettings.AEnabled && __instance.def.defName == "RimKata_A")
                __result = false;
        }
    }

    [HarmonyPatch(typeof(CompUseEffect_GainAbility), nameof(CompUseEffect_GainAbility.CanBeUsedBy))]
    internal static class Patch_PsytrainerUse_RimKataActivation
    {
        public static void Postfix(CompUseEffect_GainAbility __instance, ref AcceptanceReport __result)
        {
            if (!RimKataActivationSettings.IsItemEnabled(__instance.parent.def)) __result = false;
        }
    }
}
