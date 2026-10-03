using System;
using Verse;

namespace KRWF.RimKata
{
    public enum RimKataStrengthSourceKind
    {
        Apparel,
        Implant
    }

    public sealed class RimKataStrengthRule : IExposable
    {
        public RimKataStrengthSourceKind sourceKind;
        public string defName;
        public bool enabled;
        public bool hasOverride;
        public float percent;

        internal string Key => sourceKind + ":" + defName;

        public RimKataStrengthRule Copy() => new RimKataStrengthRule
        {
            sourceKind = sourceKind,
            defName = defName,
            enabled = enabled,
            hasOverride = hasOverride,
            percent = percent
        };

        public float EffectivePercent(float defaultPercent) => SanitizePercent(hasOverride ? percent : defaultPercent);

        public void Sanitize()
        {
            percent = SanitizePercent(percent);
        }

        internal static float SanitizePercent(float value)
            => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Math.Max(0f, value);

        public void ExposeData()
        {
            Scribe_Values.Look(ref sourceKind, "sourceKind", RimKataStrengthSourceKind.Apparel);
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref enabled, "enabled", false);
            Scribe_Values.Look(ref hasOverride, "hasOverride", false);
            Scribe_Values.Look(ref percent, "percent", 0f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Sanitize();
        }
    }
}
