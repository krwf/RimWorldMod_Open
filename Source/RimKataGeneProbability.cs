using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public enum RimKataGeneChoice { RimKata, MindNumbSerumDependency }

    public sealed class RimKataGeneProbabilityRule : IExposable
    {
        public string key;
        public float rimKataChancePercent;
        public float dependencyChancePercent;

        public RimKataGeneProbabilityRule Copy() => new RimKataGeneProbabilityRule
        {
            key = key,
            rimKataChancePercent = rimKataChancePercent,
            dependencyChancePercent = dependencyChancePercent
        };

        public void Sanitize(bool rimKataEnabled = true, bool dependencyEnabled = true)
        {
            rimKataChancePercent = rimKataEnabled ? Percent(rimKataChancePercent) : 0f;
            dependencyChancePercent = dependencyEnabled
                ? Mathf.Min(Percent(dependencyChancePercent), 100f - rimKataChancePercent) : 0f;
        }

        public RimKataGeneChoice? GeneChoiceForRoll(float rollPercent,
            bool rimKataEnabled = true, bool dependencyEnabled = true)
        {
            float rimKata = rimKataEnabled ? Percent(rimKataChancePercent) : 0f;
            float dependency = dependencyEnabled
                ? Mathf.Min(Percent(dependencyChancePercent), 100f - rimKata) : 0f;
            if (float.IsNaN(rollPercent) || float.IsInfinity(rollPercent)) return null;
            if (rimKata > 0f && (rimKata >= 100f || rollPercent < rimKata)) return RimKataGeneChoice.RimKata;
            if (dependency > 0f && (rimKata + dependency >= 100f || rollPercent < rimKata + dependency))
                return RimKataGeneChoice.MindNumbSerumDependency;
            return null;
        }

        private static float Percent(float value)
            => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp(value, 0f, 100f);

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref rimKataChancePercent, "rimKataChancePercent", 0f);
            Scribe_Values.Look(ref dependencyChancePercent, "dependencyChancePercent", 0f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Sanitize();
        }
    }

    internal static class RimKataGeneProbability
    {
        internal const string ShamblerKey = "mutant:Shambler";
        internal const string CreepJoinerKey = "special:creepjoiner";
        private sealed class Encounter
        {
            internal bool checkedSpawn;
        }

        private static readonly ConditionalWeakTable<Pawn, Encounter> encounters =
            new ConditionalWeakTable<Pawn, Encounter>();
        private static readonly HashSet<string> observedGeneKinds = new HashSet<string>(StringComparer.Ordinal);
        private static IReadOnlyList<RimKataTargetEntry> eligibleEntries;
        private static RimKataTargetEntry creepJoinerEntry;
        private static RimKataSettings cachedSettings;
        private static Dictionary<string, RimKataGeneProbabilityRule> rules;

        internal static IReadOnlyList<RimKataTargetEntry> EligibleEntries
        {
            get
            {
                if (eligibleEntries != null) return eligibleEntries;
                var entries = new List<RimKataTargetEntry>();
                foreach (RimKataTargetEntry entry in RimKataTargetCatalog.Entries)
                    if (!entry.IsCorpse && (IsShamblerEntry(entry) || entry.Category == RimKataTargetCategory.Humanlike
                        || observedGeneKinds.Contains(entry.Key))) entries.Add(entry);
                if (ModsConfig.AnomalyActive) entries.Add(CreepJoinerEntry);
                return eligibleEntries = entries.AsReadOnly();
            }
        }

        private static RimKataTargetEntry CreepJoinerEntry => creepJoinerEntry ??
            (creepJoinerEntry = new RimKataTargetEntry(CreepJoinerKey,
                "KRWF_RimKata_GeneProbabilityCreepJoiner".Translate(),
                RimKataTargetCategory.Humanlike, ThingDefOf.Human));

        internal static bool IsShamblerEntry(RimKataTargetEntry entry) => entry?.Key == ShamblerKey;
        internal static bool IsCreepJoinerEntry(RimKataTargetEntry entry) => entry?.Key == CreepJoinerKey;

        internal static RimKataTargetEntry Resolve(Pawn pawn)
        {
            if (pawn == null) return null;
            if (pawn.IsShambler)
                return RimKataTargetCatalog.TryGetEntry(ShamblerKey, out RimKataTargetEntry shambler) ? shambler : null;
            if (pawn.IsCreepJoiner) return CreepJoinerEntry;
            // Restriction overrides classify every mutation; gene chances retain their existing groups.
            return RimKataTargetCatalog.Resolve(pawn, includeMutants: false);
        }

        internal static void InvalidateSettings()
        {
            cachedSettings = null;
            rules = null;
        }

        private static RimKataGeneProbabilityRule RuleFor(RimKataSettings settings, string key)
        {
            if (cachedSettings != settings || rules == null)
            {
                cachedSettings = settings;
                rules = new Dictionary<string, RimKataGeneProbabilityRule>(StringComparer.Ordinal);
                if (settings.geneProbabilityRules != null)
                    foreach (RimKataGeneProbabilityRule rule in settings.geneProbabilityRules)
                        if (rule?.key != null && !rules.ContainsKey(rule.key)) rules.Add(rule.key, rule);
            }
            return key != null && rules.TryGetValue(key, out RimKataGeneProbabilityRule value) ? value : null;
        }

        internal static void NotifySpawned(Pawn pawn, bool respawningAfterLoad, bool wasSeen)
        {
            if (pawn?.Spawned != true) return;
            RimKataTargetEntry entry = null;
            if (pawn.genes != null)
            {
                entry = Resolve(pawn);
                if (entry != null && observedGeneKinds.Add(entry.Key)) eligibleEntries = null;
            }
            Encounter encounter = encounters.GetOrCreateValue(pawn);
            bool checkedSpawn = encounter.checkedSpawn;
            encounter.checkedSpawn = true;
            if (checkedSpawn || respawningAfterLoad || wasSeen || pawn.Dead || pawn.genes == null) return;

            RimKataSettings settings = RimKataMod.Settings;
            if (settings == null || (!settings.enableRimKataG && !settings.enableSerumDependency)) return;
            GeneDef rimKata = RimKataDefOf.RimKata_G;
            GeneDef dependency = RimKataAnomalyUtility.DependencyGeneDef;
            if ((rimKata != null && pawn.genes.GetGene(rimKata) != null)
                || (dependency != null && pawn.genes.GetGene(dependency) != null)) return;
            RimKataGeneProbabilityRule rule = RuleFor(settings, entry?.Key);
            if (rule == null || rule.rimKataChancePercent <= 0f && rule.dependencyChancePercent <= 0f) return;
            RimKataGeneChoice? choice = rule.GeneChoiceForRoll(Rand.Value * 100f,
                settings.enableRimKataG && rimKata != null,
                settings.enableSerumDependency && dependency != null);
            GeneDef selected = choice == RimKataGeneChoice.RimKata ? rimKata
                : choice == RimKataGeneChoice.MindNumbSerumDependency ? dependency : null;
            if (selected != null) pawn.genes.AddGene(selected, true);
        }

        internal static void ExposeData(Pawn pawn)
        {
            bool checkedSpawn = encounters.TryGetValue(pawn, out Encounter encounter) && encounter.checkedSpawn;
            Scribe_Values.Look(ref checkedSpawn, "rimKataGeneProbabilityChecked", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && checkedSpawn)
                encounters.GetOrCreateValue(pawn).checkedSpawn = true;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExposeData))]
    internal static class Patch_Pawn_RimKataGeneProbabilitySave
    {
        private static void Postfix(Pawn __instance) => RimKataGeneProbability.ExposeData(__instance);
    }
}
