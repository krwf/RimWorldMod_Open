using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataTraitActivationRule : IExposable
    {
        public string defName;
        public int degree;
        public bool friendly;
        public bool hostile;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref degree, "degree", 0);
            Scribe_Values.Look(ref friendly, "friendly", false);
            Scribe_Values.Look(ref hostile, "hostile", false);
        }
    }

    // Prepared when settings are applied. Pawn events inspect only the changed pawn.
    internal static class RimKataPersonalActivation
    {
        private static readonly Dictionary<TraitDef, Dictionary<int, byte>> traits =
            new Dictionary<TraitDef, Dictionary<int, byte>>();
        private static readonly List<RimKataTraitActivationRule> appliedRules = new List<RimKataTraitActivationRule>();
        private static bool initialized;
        private static bool shootingEnabled, meleeEnabled;
        private static int shootingRequirement, meleeRequirement;
        internal static bool TraitsEnabled => traits.Count != 0;
        internal static bool SkillsEnabled => shootingEnabled || meleeEnabled;

        internal static bool Rebuild(RimKataSettings settings)
        {
            if (settings == null) return false;
            settings.SanitizePersonalActivation();
            bool changed = !initialized || shootingEnabled != settings.enableShootingLevel
                || meleeEnabled != settings.enableMeleeLevel
                || shootingRequirement != settings.shootingLevelRequirement
                || meleeRequirement != settings.meleeLevelRequirement
                || appliedRules.Count != settings.traitActivationRules.Count;
            if (!changed)
                for (int i = 0; i < appliedRules.Count; i++)
                {
                    RimKataTraitActivationRule old = appliedRules[i], next = settings.traitActivationRules[i];
                    if (old.defName != next.defName || old.degree != next.degree
                        || old.friendly != next.friendly || old.hostile != next.hostile)
                    { changed = true; break; }
                }
            if (!changed) return false;
            initialized = true;
            shootingEnabled = settings.enableShootingLevel;
            meleeEnabled = settings.enableMeleeLevel;
            shootingRequirement = settings.shootingLevelRequirement;
            meleeRequirement = settings.meleeLevelRequirement;
            traits.Clear();
            appliedRules.Clear();
            foreach (RimKataTraitActivationRule rule in settings.traitActivationRules)
            {
                appliedRules.Add(new RimKataTraitActivationRule
                { defName = rule.defName, degree = rule.degree, friendly = rule.friendly, hostile = rule.hostile });
                TraitDef def = DefDatabase<TraitDef>.GetNamedSilentFail(rule.defName);
                if (def == null) continue;
                if (!traits.TryGetValue(def, out Dictionary<int, byte> degrees))
                    traits.Add(def, degrees = new Dictionary<int, byte>());
                degrees.TryGetValue(rule.degree, out byte flags);
                degrees[rule.degree] = (byte)(flags | (rule.friendly ? 1 : 0) | (rule.hostile ? 2 : 0));
            }
            return true;
        }

        internal static bool EnabledFor(SkillDef skill) =>
            (shootingEnabled && skill == SkillDefOf.Shooting) || (meleeEnabled && skill == SkillDefOf.Melee);

        internal static void ReadSkills(Pawn pawn, out bool shooting, out bool melee)
        {
            shooting = shootingEnabled && pawn.skills?.GetSkill(SkillDefOf.Shooting) is SkillRecord shot
                && shot.Level >= shootingRequirement;
            melee = meleeEnabled && pawn.skills?.GetSkill(SkillDefOf.Melee) is SkillRecord close
                && close.Level >= meleeRequirement;
        }

        internal static Trait[] ReadTraits(Pawn pawn)
        {
            if (!TraitsEnabled) return Array.Empty<Trait>();
            List<Trait> all = pawn.story?.traits?.allTraits;
            if (all == null) return Array.Empty<Trait>();
            int side = RimKataEligibility.IsHostileToPlayerFaction(pawn) ? 2 : 1;
            List<Trait> matches = null;
            for (int i = 0; i < all.Count; i++)
            {
                Trait trait = all[i];
                if (!trait.Suppressed && traits.TryGetValue(trait.def, out Dictionary<int, byte> degrees)
                    && degrees.TryGetValue(trait.Degree, out byte flags) && (flags & side) != 0)
                    (matches ??= new List<Trait>()).Add(trait);
            }
            return matches?.ToArray() ?? Array.Empty<Trait>();
        }
    }
}
