using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataSkillActivationRule : IExposable
    {
        public string defName;
        public bool enabled;
        public int requiredLevel;

        public RimKataSkillActivationRule Copy() => new RimKataSkillActivationRule
        { defName = defName, enabled = enabled, requiredLevel = requiredLevel };

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref enabled, "enabled", false);
            Scribe_Values.Look(ref requiredLevel, "requiredLevel", 0);
        }
    }

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
        private static readonly Dictionary<SkillDef, int> skills = new Dictionary<SkillDef, int>();
        private static readonly List<RimKataSkillActivationRule> appliedSkillRules = new List<RimKataSkillActivationRule>();
        internal static bool TraitsEnabled => traits.Count != 0;
        internal static bool SkillsEnabled => skills.Count != 0;

        internal static bool Rebuild(RimKataSettings settings)
        {
            if (settings == null) return false;
            settings.SanitizePersonalActivation();
            bool changed = !initialized || appliedSkillRules.Count != settings.skillActivationRules.Count
                || appliedRules.Count != settings.traitActivationRules.Count;
            if (!changed)
                for (int i = 0; i < appliedSkillRules.Count; i++)
                {
                    RimKataSkillActivationRule old = appliedSkillRules[i], next = settings.skillActivationRules[i];
                    if (old.defName != next.defName || old.enabled != next.enabled || old.requiredLevel != next.requiredLevel)
                    { changed = true; break; }
                }
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
            skills.Clear();
            appliedSkillRules.Clear();
            foreach (RimKataSkillActivationRule rule in settings.skillActivationRules)
            {
                appliedSkillRules.Add(rule.Copy());
                if (!rule.enabled) continue;
                SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(rule.defName);
                if (def != null) skills[def] = rule.requiredLevel;
            }
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

        internal static bool EnabledFor(SkillDef skill) => skill != null && skills.ContainsKey(skill);

        internal static bool ReadSkills(Pawn pawn)
        {
            if (!SkillsEnabled || pawn?.skills == null) return false;
            foreach (KeyValuePair<SkillDef, int> requirement in skills)
                if (pawn.skills.GetSkill(requirement.Key) is SkillRecord record
                    && record.Level >= requirement.Value) return true;
            return false;
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
