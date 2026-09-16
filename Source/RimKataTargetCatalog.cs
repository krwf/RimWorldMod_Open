using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal enum RimKataTargetCategory
    {
        Humanlike,
        Mechanoid,
        Insect,
        Animal,
        Other
    }

    internal sealed class RimKataTargetEntry
    {
        public string Key { get; }
        public string Label { get; }
        public string SearchText { get; }
        public RimKataTargetCategory Category { get; }
        public ThingDef RaceDef { get; }
        public XenotypeDef XenotypeDef { get; }

        internal RimKataTargetEntry(string key, string label, RimKataTargetCategory category,
            ThingDef raceDef, XenotypeDef xenotypeDef = null)
        {
            Key = key;
            Label = label;
            Category = category;
            RaceDef = raceDef;
            XenotypeDef = xenotypeDef;
            Def definition = (Def)xenotypeDef ?? raceDef;
            SearchText = label + " " + key + " " + (definition?.modContentPack?.Name ?? string.Empty);
        }
    }

    internal static class RimKataTargetCatalog
    {
        private static IReadOnlyList<RimKataTargetEntry> entries;
        private static Dictionary<ThingDef, RimKataTargetEntry> raceEntries;
        private static Dictionary<XenotypeDef, RimKataTargetEntry> xenotypeEntries;
        private static Dictionary<string, RimKataTargetEntry> entriesByKey;
        private static ThingDef humanDef;
        private static RimKataTargetEntry baselinerEntry;
        private static RimKataTargetEntry customEntry;
        private static RimKataTargetEntry hybridEntry;

        public static IReadOnlyList<RimKataTargetEntry> Entries
        {
            get
            {
                EnsureInitialized();
                return entries;
            }
        }

        public static RimKataTargetEntry Resolve(Pawn pawn)
        {
            if (pawn?.def == null)
                return null;

            EnsureInitialized();
            if (pawn.def != humanDef)
            {
                raceEntries.TryGetValue(pawn.def, out RimKataTargetEntry raceEntry);
                return raceEntry;
            }

            Pawn_GeneTracker genes = pawn.genes;
            if (genes == null)
                return baselinerEntry;
            if (genes.UniqueXenotype)
                return genes.hybrid ? hybridEntry : customEntry;

            XenotypeDef xenotype = genes.Xenotype;
            if (xenotype == null)
                return baselinerEntry;
            return xenotypeEntries.TryGetValue(xenotype, out RimKataTargetEntry entry)
                ? entry : customEntry;
        }

        public static bool TryGetEntry(string key, out RimKataTargetEntry entry)
        {
            EnsureInitialized();
            if (key == null)
            {
                entry = null;
                return false;
            }
            return entriesByKey.TryGetValue(key, out entry);
        }

        private static void EnsureInitialized()
        {
            if (entries != null)
                return;

            List<RimKataTargetEntry> result = new List<RimKataTargetEntry>();
            raceEntries = new Dictionary<ThingDef, RimKataTargetEntry>();
            xenotypeEntries = new Dictionary<XenotypeDef, RimKataTargetEntry>();
            entriesByKey = new Dictionary<string, RimKataTargetEntry>(StringComparer.Ordinal);
            humanDef = ThingDefOf.Human;

            List<XenotypeDef> xenotypes = DefDatabase<XenotypeDef>.AllDefsListForReading;
            for (int i = 0; i < xenotypes.Count; i++)
            {
                XenotypeDef xenotype = xenotypes[i];
                if (string.IsNullOrEmpty(xenotype.defName))
                    continue;
                RimKataTargetEntry entry = new RimKataTargetEntry("xenotype:" + xenotype.defName,
                    xenotype.LabelCap.ToString(), RimKataTargetCategory.Humanlike, humanDef, xenotype);
                Add(result, entry);
                xenotypeEntries.Add(xenotype, entry);
                if (xenotype.defName == "Baseliner")
                    baselinerEntry = entry;
            }

            if (baselinerEntry == null)
            {
                // Baseliner itself is a Biotech def; keep the same saved key without that DLC.
                baselinerEntry = new RimKataTargetEntry("xenotype:Baseliner",
                    "KRWF_RimKata_TargetBaseliner".Translate(), RimKataTargetCategory.Humanlike, humanDef);
                Add(result, baselinerEntry);
            }

            if (ModsConfig.BiotechActive || xenotypes.Count > 0)
            {
                customEntry = new RimKataTargetEntry("synthetic:user-xenotype",
                    "KRWF_RimKata_TargetCustomXenotype".Translate(), RimKataTargetCategory.Humanlike, humanDef);
                hybridEntry = new RimKataTargetEntry("synthetic:hybrid",
                    "KRWF_RimKata_TargetHybridXenotype".Translate(), RimKataTargetCategory.Humanlike, humanDef);
                Add(result, customEntry);
                Add(result, hybridEntry);
            }

            List<ThingDef> races = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < races.Count; i++)
            {
                ThingDef race = races[i];
                if (race.race == null || race == humanDef || string.IsNullOrEmpty(race.defName))
                    continue;

                RimKataTargetCategory category;
                if (race.race.IsMechanoid)
                    category = RimKataTargetCategory.Mechanoid;
                else if (race.race.Insect)
                    category = RimKataTargetCategory.Insect;
                else if (race.race.Humanlike)
                    category = RimKataTargetCategory.Humanlike;
                else if (race.race.Animal)
                    category = RimKataTargetCategory.Animal;
                else
                    category = RimKataTargetCategory.Other;

                RimKataTargetEntry entry = new RimKataTargetEntry("race:" + race.defName,
                    race.LabelCap.ToString(), category, race);
                Add(result, entry);
                raceEntries.Add(race, entry);
            }

            result.Sort((left, right) =>
            {
                int comparison = left.Category.CompareTo(right.Category);
                if (comparison == 0)
                    comparison = StringComparer.CurrentCultureIgnoreCase.Compare(left.Label, right.Label);
                return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Key, right.Key);
            });
            entries = result.AsReadOnly();
        }

        private static void Add(List<RimKataTargetEntry> result, RimKataTargetEntry entry)
        {
            result.Add(entry);
            entriesByKey.Add(entry.Key, entry);
        }
    }
}
