using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public enum RimKataStatComparison { Greater, GreaterOrEqual, Equal, LessOrEqual, Less, NotEqual }

    public sealed class RimKataStatActivationRule : IExposable
    {
        public string key;
        public bool enabled;
        public RimKataStatComparison comparison = RimKataStatComparison.GreaterOrEqual;
        public float value;

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref enabled, "enabled", false);
            Scribe_Values.Look(ref comparison, "comparison", RimKataStatComparison.GreaterOrEqual);
            Scribe_Values.Look(ref value, "value", 0f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Sanitize();
        }

        public RimKataStatActivationRule Copy() => new RimKataStatActivationRule
        { key = key, enabled = enabled, comparison = comparison, value = value };

        public void Sanitize()
        {
            if (comparison < RimKataStatComparison.Greater || comparison > RimKataStatComparison.NotEqual)
                comparison = RimKataStatComparison.GreaterOrEqual;
            if (float.IsNaN(value) || float.IsInfinity(value)) { value = 0f; enabled = false; }
        }
    }

    internal sealed class RimKataActivationStatItem
    {
        internal string Key { get; }
        internal string Label { get; }
        internal int Category { get; }
        internal float DefaultValue { get; }
        internal StatDef Stat { get; }
        internal PawnCapacityDef Capacity { get; }
        internal RecordDef Record { get; }
        internal bool IsHealth => Capacity != null || Key == "health:Pain" || Category == 1;
        internal bool NeedsPeriodic => Stat != null || Key.StartsWith("special:", StringComparison.Ordinal);
        internal string DefaultLabel => Format(DefaultValue);
        private readonly Func<Pawn, float> specialReader;
        private readonly ToStringStyle style;

        internal RimKataActivationStatItem(string key, string label, int category, float defaultValue,
            StatDef stat = null, PawnCapacityDef capacity = null, RecordDef record = null,
            Func<Pawn, float> reader = null, ToStringStyle style = ToStringStyle.FloatTwo)
        {
            Key = key; Label = label; Category = category; DefaultValue = defaultValue;
            Stat = stat; Capacity = capacity; Record = record; specialReader = reader;
            this.style = stat?.toStringStyle ?? style;
        }

        internal bool TryRead(Pawn pawn, out float raw)
        {
            raw = 0f;
            if (pawn == null || pawn.Dead) return false;
            try { return Read(pawn, out raw); }
            catch (Exception)
            {
                // A modded worker can reject this pawn. Keep the rule for other pawns and later changes.
                raw = 0f;
                return false;
            }
        }

        private bool Read(Pawn pawn, out float raw)
        {
            raw = 0f;
            if (Capacity != null)
            {
                if (pawn.health?.capacities == null || !Capacity.CanShowOnPawn(pawn)
                    || !PawnCapacityUtility.BodyCanEverDoCapacity(pawn.RaceProps.body, Capacity)) return false;
                raw = pawn.health.capacities.GetLevel(Capacity);
            }
            else if (Stat != null)
            {
                StatWorker worker = Stat.Worker;
                if (worker.IsDisabledFor(pawn) || !worker.ShouldShowFor(StatRequest.For(pawn))) return false;
                raw = pawn.GetStatValue(Stat);
            }
            else if (Record != null)
            {
                if (pawn.records == null) return false;
                raw = pawn.records.GetValue(Record);
            }
            else
            {
                if (specialReader == null || pawn.health == null) return false;
                raw = specialReader(pawn);
            }
            return !float.IsNaN(raw) && !float.IsInfinity(raw);
        }

        internal float ToDisplay(float raw)
        {
            if (IsPercent) return raw * 100f;
            if (style == ToStringStyle.Temperature) return GenTemperature.CelsiusTo(raw, Prefs.TemperatureMode);
            if (style == ToStringStyle.TemperatureOffset) return GenTemperature.CelsiusToOffset(raw, Prefs.TemperatureMode);
            return style == ToStringStyle.WorkAmount ? raw / 60f : raw;
        }

        internal float FromDisplay(float display)
        {
            if (IsPercent) return display / 100f;
            if (style == ToStringStyle.Temperature || style == ToStringStyle.TemperatureOffset)
            {
                float zero = style == ToStringStyle.Temperature
                    ? GenTemperature.CelsiusTo(0f, Prefs.TemperatureMode) : 0f;
                float unit = GenTemperature.CelsiusToOffset(1f, Prefs.TemperatureMode);
                return (display - zero) / unit;
            }
            return style == ToStringStyle.WorkAmount ? display * 60f : display;
        }

        internal bool Compare(float raw, RimKataStatActivationRule rule)
        {
            if (rule == null || float.IsNaN(raw) || float.IsInfinity(raw)) return false;
            double left = Comparable(raw), right = Comparable(rule.value);
            switch (rule.comparison)
            {
                case RimKataStatComparison.Greater: return left > right;
                case RimKataStatComparison.Equal: return left == right;
                case RimKataStatComparison.LessOrEqual: return left <= right;
                case RimKataStatComparison.Less: return left < right;
                case RimKataStatComparison.NotEqual: return left != right;
                default: return left >= right;
            }
        }

        private bool IsPercent => style == ToStringStyle.PercentZero || style == ToStringStyle.PercentOne
            || style == ToStringStyle.PercentTwo;

        private double Comparable(float raw)
        {
            double displayed = ToDisplay(raw);
            if (style == ToStringStyle.WorkAmount) return Math.Ceiling(displayed);
            int decimals;
            switch (style)
            {
                case ToStringStyle.Integer: case ToStringStyle.PercentZero: decimals = 0; break;
                case ToStringStyle.FloatOne: case ToStringStyle.FloatMaxOne: case ToStringStyle.PercentOne:
                case ToStringStyle.Temperature: case ToStringStyle.TemperatureOffset: decimals = 1; break;
                case ToStringStyle.FloatThree: case ToStringStyle.FloatMaxThree: decimals = 3; break;
                case ToStringStyle.FloatTwoOrThree: decimals = raw != 0f && Mathf.Abs(raw) < 0.01f ? 3 : 2; break;
                default: decimals = 2; break;
            }
            return Math.Round(displayed, decimals, MidpointRounding.ToEven);
        }

        private string Format(float raw)
        {
            string result = Stat != null ? Stat.ValueToString(raw, Stat.toStringNumberSense)
                : raw.ToStringByStyle(style, ToStringNumberSense.Absolute);
            return Key == "health:Bleeding" ? result + "/" + "LetterDay".Translate().ToString() : result;
        }
    }

    internal static class RimKataActivationStatCatalog
    {
        private static List<RimKataActivationStatItem> all;
        private static Dictionary<string, RimKataActivationStatItem> byKey;
        private static readonly string[] categoryNames = { null, "PawnHealth", "BasicsPawn", "PawnPsyfocus",
            "PawnResistances", "PawnFood", "PawnCombat", "PawnSocial", "PawnWork" };
        private static readonly string[] recordNames = { "Kills", "KillsHumanlikes", "KillsAnimals", "KillsMechanoids",
            "KillsEntities", "PawnsDowned", "PawnsDownedHumanlikes", "PawnsDownedAnimals", "PawnsDownedMechanoids",
            "PawnsDownedEntities", "ShotsFired", "Headshots", "DamageDealt", "DamageTaken" };

        internal static IReadOnlyList<RimKataActivationStatItem> All { get { EnsureBuilt(); return all; } }
        internal static RimKataActivationStatItem Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            EnsureBuilt();
            return byKey.TryGetValue(key, out RimKataActivationStatItem item) ? item : null;
        }

        internal static string CategoryLabel(int category)
        {
            if (category == 0) return "KRWF_RimKata_FilterAll".Translate();
            if (category == 9) return "KRWF_RimKata_StatCategoryRecords".Translate();
            StatCategoryDef def = DefDatabase<StatCategoryDef>.GetNamedSilentFail(categoryNames[category]);
            return def?.LabelCap.ToString() ?? categoryNames[category];
        }

        private static void EnsureBuilt()
        {
            if (all != null) return;
            var items = new List<RimKataActivationStatItem>();
            ThingDef human = DefDatabase<ThingDef>.GetNamedSilentFail("Human");
            foreach (PawnCapacityDef def in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
                items.Add(new RimKataActivationStatItem("capacity:" + def.defName, def.LabelCap.ToString(), 1, 1f,
                    capacity: def, style: ToStringStyle.PercentZero));
            items.Add(new RimKataActivationStatItem("health:Pain", "PainLevel".Translate(), 1, 0f,
                reader: pawn => pawn.health.hediffSet.PainTotal, style: ToStringStyle.PercentZero));
            items.Add(new RimKataActivationStatItem("health:Bleeding", "BleedingRate".Translate(), 1, 0f,
                reader: pawn => pawn.health.hediffSet.BleedRateTotal, style: ToStringStyle.PercentZero));
            items.Add(new RimKataActivationStatItem("special:BodySize", "BodySize".Translate(), 2, 1f,
                reader: pawn => pawn.BodySize));
            if (ModsConfig.BiotechActive)
            {
                items.Add(new RimKataActivationStatItem("special:Growth", "Growth".Translate(), 2, 1f,
                    reader: pawn => pawn.ageTracker.Growth, style: ToStringStyle.PercentZero));
                items.Add(new RimKataActivationStatItem("special:AgeRate", "StatsReport_AgeRateMultiplier".Translate(), 1, 1f,
                    reader: pawn => pawn.ageTracker.BiologicalTicksPerTick, style: ToStringStyle.PercentZero));
            }
            foreach (StatDef def in DefDatabase<StatDef>.AllDefsListForReading)
            {
                int category = CategoryOf(def.category?.defName);
                if (category == 0 || !def.showOnPawns || def.alwaysHide || !def.CanShowWithLoadedMods()) continue;
                float initial = def.defaultBaseValue;
                if (human?.statBases != null)
                    foreach (StatModifier modifier in human.statBases)
                        if (modifier.stat == def) { initial = modifier.value; break; }
                items.Add(new RimKataActivationStatItem("stat:" + def.defName, def.LabelForFullStatListCap, category,
                    initial, stat: def));
            }
            foreach (string name in recordNames)
            {
                RecordDef def = DefDatabase<RecordDef>.GetNamedSilentFail(name);
                if (def != null) items.Add(new RimKataActivationStatItem("record:" + name, def.LabelCap.ToString(), 9, 0f,
                    record: def, style: ToStringStyle.FloatMaxTwo));
            }
            items.Sort((left, right) =>
            {
                int compared = StringComparer.CurrentCultureIgnoreCase.Compare(left.Label, right.Label);
                return compared != 0 ? compared : StringComparer.Ordinal.Compare(left.Key, right.Key);
            });
            var lookup = new Dictionary<string, RimKataActivationStatItem>(StringComparer.Ordinal);
            foreach (RimKataActivationStatItem item in items) lookup[item.Key] = item;
            byKey = lookup;
            all = items;
        }

        private static int CategoryOf(string name)
        {
            if (name == "Basics" || name == "BasicsImportant" || name == "BasicsPawnImportant") return 2;
            for (int i = 1; i < categoryNames.Length; i++) if (categoryNames[i] == name) return i;
            return 0;
        }
    }
}
