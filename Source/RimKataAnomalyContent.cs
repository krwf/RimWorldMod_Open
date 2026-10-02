using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public static class RimKataAnomalyUtility
    {
        public const string DependencyGeneDefName = "ChemicalDependency_MindNumbSerum";
        private static GeneDef dependencyGeneDef;
        private static bool defsResolved;

        public static GeneDef DependencyGeneDef
        {
            get
            {
                ResolveDefs();
                return dependencyGeneDef;
            }
        }

        public static string DependencyLabel
        {
            get
            {
                ThingDef serum = DefDatabase<ThingDef>.GetNamedSilentFail("MindNumbSerum");
                return serum == null
                    ? "mind-numb serum dependency"
                    : "ChemicalDependency".Translate(serum.Named("CHEMICAL"));
            }
        }

        public static bool HasActiveDependencyGene(Pawn pawn)
        {
            return RimKataEligibilityCache.HasActiveDependencyGene(pawn);
        }

        public static Gene_MindNumbSerumDependency DependencyGene(Pawn pawn)
        {
            return RimKataEligibilityCache.DependencyGene(pawn);
        }

        public static bool DependencyOvercomeByBond(Pawn pawn)
        {
            return RimKataEligibilityCache.DependencyOvercomeByBond(pawn);
        }

        public static void RefreshDependencyGeneLabel()
        {
            GeneDef geneDef = DependencyGeneDef;
            if (geneDef == null)
            {
                return;
            }

            string label = DependencyLabel;
            geneDef.label = label;
            geneDef.labelShortAdj = label;
        }

        private static void ResolveDefs()
        {
            if (defsResolved)
            {
                return;
            }

            dependencyGeneDef = DefDatabase<GeneDef>.GetNamedSilentFail(DependencyGeneDefName);
            defsResolved = true;
        }
    }

    [StaticConstructorOnStartup]
    public static class RimKataAnomalyContentInitializer
    {
        static RimKataAnomalyContentInitializer()
        {
            RimKataAnomalyUtility.RefreshDependencyGeneLabel();
        }
    }

    public sealed class Gene_MindNumbSerumDependency : Gene
    {
        private const int MoodNeedIntervalTicks = 150;
        private int ticksWithoutSerum;

        public override string Label => RimKataAnomalyUtility.DependencyLabel;

        public int DaysWithoutSerum => Math.Max(0, ticksWithoutSerum / GenDate.TicksPerDay);

        internal void AdvanceWithoutSerumInterval()
        {
            if (!Active
                || pawn == null
                || RimKataSerumUtility.IsMindNumbed(pawn))
            {
                return;
            }

            ticksWithoutSerum = ticksWithoutSerum
                > int.MaxValue - MoodNeedIntervalTicks
                ? int.MaxValue
                : ticksWithoutSerum + MoodNeedIntervalTicks;
        }

        internal void ResetWithoutSerumTicks()
        {
            ticksWithoutSerum = 0;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ticksWithoutSerum, "ticksWithoutSerum", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && ticksWithoutSerum < 0)
            {
                ticksWithoutSerum = 0;
            }
        }
    }

    [HarmonyPatch(typeof(Need_Mood), nameof(Need_Mood.NeedInterval))]
    public static class Patch_NeedMood_RimKataDependencyInterval
    {
        private static readonly Func<Need, bool> IsFrozen =
            AccessTools.MethodDelegate<Func<Need, bool>>(
                AccessTools.PropertyGetter(typeof(Need), "IsFrozen"));

        public static void Postfix(Need_Mood __instance, Pawn ___pawn)
        {
            if (__instance == null
                || ___pawn == null
                || IsFrozen(__instance))
            {
                return;
            }

            Gene_MindNumbSerumDependency gene =
                RimKataAnomalyUtility.DependencyGene(___pawn);
            if (gene?.Active == true)
            {
                gene.AdvanceWithoutSerumInterval();
            }
        }
    }

    public sealed class ThoughtWorker_MindNumbSerumWithdrawal : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn pawn)
        {
            return RimKataAnomalyUtility.HasActiveDependencyGene(pawn)
                && !RimKataSerumUtility.IsMindNumbed(pawn)
                && !RimKataAnomalyUtility.DependencyOvercomeByBond(pawn);
        }
    }

    public sealed class Thought_MindNumbSerumWithdrawal : Thought_Situational
    {
        public override float MoodOffset()
        {
            Gene_MindNumbSerumDependency gene = RimKataAnomalyUtility.DependencyGene(pawn);
            RimKataMindNumbSerumWithdrawalExtension extension = def.GetModExtension<RimKataMindNumbSerumWithdrawalExtension>();
            return gene == null
                ? 0f
                : base.MoodOffset() + gene.DaysWithoutSerum * (extension?.dailyMoodOffset ?? -5f);
        }
    }

    public sealed class ThoughtWorker_MindNumbSerumEmotionRemoval : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn pawn)
        {
            return RimKataAnomalyUtility.HasActiveDependencyGene(pawn) && RimKataSerumUtility.IsMindNumbed(pawn);
        }
    }

    [HarmonyPatch(typeof(ThoughtUtility), nameof(ThoughtUtility.NullifyingHediff))]
    public static class Patch_ThoughtUtility_RimKataEmotionRemoval
    {
        public static void Postfix(ThoughtDef def, ref Hediff __result)
        {
            if (def?.defName == "MindNumbSerumEmotionRemoval")
            {
                __result = null;
            }
        }
    }

    public sealed class RimKataMindNumbSerumWithdrawalExtension : DefModExtension
    {
        public float dailyMoodOffset = -5f;
    }

    public sealed class ThoughtWorker_MindNumbSerumDependencyOvercome : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn pawn)
        {
            return RimKataAnomalyUtility.HasActiveDependencyGene(pawn)
                && !RimKataSerumUtility.IsMindNumbed(pawn)
                && RimKataAnomalyUtility.DependencyOvercomeByBond(pawn);
        }
    }

}
