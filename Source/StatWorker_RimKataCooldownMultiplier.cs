using System;
using System.Text;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public sealed class StatWorker_RimKataCooldownMultiplier : StatWorker
    {
        public override bool ShouldShowFor(StatRequest req)
        {
            Pawn pawn = req.Thing as Pawn;
            return pawn != null
                && RimKataEligibility.HasRimKataAccess(pawn)
                && base.ShouldShowFor(req);
        }

        public override float GetValueUnfinalized(StatRequest req, bool applyPostProcess = true)
        {
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null)
            {
                return 1f;
            }

            return VanillaCooldownFactor(pawn) * ArmorCooldownFactor(pawn);
        }

        public override string GetExplanationUnfinalized(StatRequest req, ToStringNumberSense numberSense)
        {
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null)
            {
                return string.Empty;
            }

            float vanillaFactor = VanillaCooldownFactor(pawn);
            bool armorApplied = RimKataEquipmentUtility.HasEnabledArmor(pawn);
            float armorFactor = ArmorCooldownFactor(pawn);

            string armorLine = armorApplied
                ? "KRWF_RimKata_CooldownArmorFactorApplied".Translate(FormatFactor(armorFactor))
                : "KRWF_RimKata_CooldownArmorFactorInactive".Translate();

            StringBuilder explanation = new StringBuilder();
            explanation.AppendLine("KRWF_RimKata_CooldownVanillaBreakdown".Translate());

            string vanillaBreakdown = VanillaCooldownBreakdown(req, vanillaFactor);
            if (!vanillaBreakdown.NullOrEmpty())
            {
                explanation.AppendLine(vanillaBreakdown);
            }

            explanation.AppendLine("KRWF_RimKata_CooldownVanillaFactor".Translate(FormatFactor(vanillaFactor)));
            explanation.Append(armorLine);
            return explanation.ToString();
        }

        private static float VanillaCooldownFactor(Pawn pawn)
        {
            return pawn?.GetStatValue(StatDefOf.RangedCooldownFactor) ?? 1f;
        }

        private static float ArmorCooldownFactor(Pawn pawn)
        {
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(pawn);
            return settings != null && RimKataEquipmentUtility.HasEnabledArmor(pawn)
                ? settings.GetArmorCooldownFactor(pawn)
                : 1f;
        }

        private static string VanillaCooldownBreakdown(StatRequest req, float vanillaFactor)
        {
            StatDef vanillaStat = StatDefOf.RangedCooldownFactor;
            string fullExplanation = vanillaStat.Worker.GetExplanationFull(req, vanillaStat.toStringNumberSense, vanillaFactor);
            if (fullExplanation.NullOrEmpty())
            {
                return string.Empty;
            }

            string finalLabel = "StatsReport_FinalValue".Translate();
            string vanillaFinalLine = finalLabel + ": " + vanillaStat.ValueToString(vanillaFactor, vanillaStat.toStringNumberSense, true);
            string[] lines = fullExplanation.Replace("\r\n", "\n").Split('\n');
            StringBuilder breakdown = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                if (string.Equals(lines[i], vanillaFinalLine, StringComparison.Ordinal))
                {
                    continue;
                }

                if (breakdown.Length > 0)
                {
                    breakdown.AppendLine();
                }

                breakdown.Append(lines[i]);
            }

            return breakdown.ToString().TrimEnd('\r', '\n');
        }

        private string FormatFactor(float value)
        {
            return stat.ValueToString(value, ToStringNumberSense.Absolute, false);
        }
    }
}
