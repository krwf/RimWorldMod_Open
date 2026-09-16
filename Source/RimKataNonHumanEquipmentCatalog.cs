using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataNonHumanEquipmentCatalog
    {
        private const int NonHumanCategories = (1 << (int)RimKataTargetCategory.Mechanoid)
            | (1 << (int)RimKataTargetCategory.Insect)
            | (1 << (int)RimKataTargetCategory.Animal)
            | (1 << (int)RimKataTargetCategory.Other);

        private static Dictionary<ThingDef, int> categories;

        internal static bool IsCandidate(ThingDef def, RimKataDefSelectionKind kind)
        {
            if (def == null || (kind == RimKataDefSelectionKind.Weapon ? !def.IsWeapon : !def.IsApparel))
                return false;

            EnsureInitialized();
            return categories.TryGetValue(def, out int mask) && (mask & NonHumanCategories) != 0;
        }

        internal static bool IsInCategory(ThingDef def, RimKataTargetCategory category)
        {
            if (def == null || category == RimKataTargetCategory.Humanlike)
                return false;

            EnsureInitialized();
            return categories.TryGetValue(def, out int mask) && (mask & (1 << (int)category)) != 0;
        }

        private static void EnsureInitialized()
        {
            if (categories != null)
                return;

            Dictionary<ThingDef, int> result = new Dictionary<ThingDef, int>();
            Dictionary<string, List<ThingDef>> weaponsByTag = new Dictionary<string, List<ThingDef>>(StringComparer.Ordinal);
            Dictionary<string, List<ThingDef>> apparelByTag = new Dictionary<string, List<ThingDef>>(StringComparer.Ordinal);
            List<ThingDef> apparel = new List<ThingDef>();
            List<ThingDef> equipment = new List<ThingDef>();
            List<ThingDef> definitions = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < definitions.Count; i++)
            {
                ThingDef def = definitions[i];
                if (string.IsNullOrEmpty(def.defName) || (!def.IsWeapon && !def.IsApparel))
                    continue;

                equipment.Add(def);
                if (def.IsWeapon)
                    IndexTags(weaponsByTag, def.weaponTags, def);
                if (def.IsApparel)
                {
                    apparel.Add(def);
                    IndexTags(apparelByTag, def.apparel?.tags, def);
                }
            }

            List<PawnKindDef> kinds = DefDatabase<PawnKindDef>.AllDefsListForReading;
            for (int i = 0; i < kinds.Count; i++)
            {
                PawnKindDef kind = kinds[i];
                if (!TryGetCategory(kind, out RimKataTargetCategory category))
                    continue;

                AddTagged(result, weaponsByTag, kind.weaponTags, category);
                AddTagged(result, apparelByTag, kind.apparelTags, category, kind.apparelDisallowTags);
                AddApparel(result, kind.apparelRequired, category);
                AddSpecificApparel(result, apparel, kind.specificApparelRequirements, category);
            }

            // Boss waves can equip apparel which is not assigned by the pawn kind itself.
            List<BossgroupDef> bossgroups = DefDatabase<BossgroupDef>.AllDefsListForReading;
            for (int i = 0; i < bossgroups.Count; i++)
            {
                BossgroupDef group = bossgroups[i];
                if (!TryGetCategory(group.boss?.kindDef, out RimKataTargetCategory category) || group.waves == null)
                    continue;

                for (int j = 0; j < group.waves.Count; j++)
                    AddApparel(result, group.waves[j]?.bossApparel, category);
            }

            for (int i = 0; i < equipment.Count; i++)
            {
                ThingDef def = equipment[i];
                // PlayerAcquirable also depends on factions in the current world. Only its
                // permanent destroy-on-drop exclusion is suitable for this definition cache.
                // Known humanlike-only equipment is retained in result but never exposed here.
                if (def.destroyOnDrop && !result.ContainsKey(def))
                    Add(result, def, RimKataTargetCategory.Other);
            }

            categories = result;
        }

        private static bool TryGetCategory(PawnKindDef kind, out RimKataTargetCategory category)
        {
            RaceProperties race = kind?.race?.race;
            category = RimKataTargetCategory.Other;
            if (race == null)
                return false;

            // Keep the same priority as the target catalog for races with overlapping flags.
            if (race.IsMechanoid)
                category = RimKataTargetCategory.Mechanoid;
            else if (race.Insect)
                category = RimKataTargetCategory.Insect;
            else if (race.Humanlike)
                category = RimKataTargetCategory.Humanlike;
            else if (race.Animal)
                category = RimKataTargetCategory.Animal;
            return true;
        }

        private static void IndexTags(Dictionary<string, List<ThingDef>> index, List<string> tags, ThingDef def)
        {
            if (tags == null)
                return;

            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (string.IsNullOrEmpty(tag))
                    continue;
                if (!index.TryGetValue(tag, out List<ThingDef> values))
                {
                    values = new List<ThingDef>();
                    index.Add(tag, values);
                }
                values.Add(def);
            }
        }

        private static void AddTagged(Dictionary<ThingDef, int> result,
            Dictionary<string, List<ThingDef>> index, List<string> tags,
            RimKataTargetCategory category, List<string> disallowedTags = null)
        {
            if (tags == null)
                return;

            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (string.IsNullOrEmpty(tag) || !index.TryGetValue(tag, out List<ThingDef> values))
                    continue;

                for (int j = 0; j < values.Count; j++)
                {
                    ThingDef def = values[j];
                    if (!HasAnyTag(def.apparel?.tags, disallowedTags))
                        Add(result, def, category);
                }
            }
        }

        private static void AddApparel(Dictionary<ThingDef, int> result, List<ThingDef> apparel,
            RimKataTargetCategory category)
        {
            if (apparel == null)
                return;

            for (int i = 0; i < apparel.Count; i++)
            {
                ThingDef def = apparel[i];
                if (def != null && def.IsApparel)
                    Add(result, def, category);
            }
        }

        private static void AddSpecificApparel(Dictionary<ThingDef, int> result, List<ThingDef> apparel,
            List<SpecificApparelRequirement> requirements, RimKataTargetCategory category)
        {
            if (requirements == null)
                return;

            for (int i = 0; i < requirements.Count; i++)
            {
                SpecificApparelRequirement requirement = requirements[i];
                if (requirement == null)
                    continue;

                ThingDef requiredDef = requirement.ApparelDef;
                BodyPartGroupDef bodyPartGroup = requirement.BodyPartGroup;
                ApparelLayerDef layer = requirement.ApparelLayer;
                string requiredTag = requirement.RequiredTag;
                List<SpecificApparelRequirement.TagChance> alternatives = requirement.AlternateTagChoices;
                for (int j = 0; j < apparel.Count; j++)
                {
                    ThingDef def = apparel[j];
                    ApparelProperties properties = def.apparel;
                    if (properties == null || (requiredDef != null && def != requiredDef)
                        || (bodyPartGroup != null && properties.bodyPartGroups?.Contains(bodyPartGroup) != true)
                        || (layer != null && properties.layers?.Contains(layer) != true))
                        continue;

                    // Match the native requirement's body/layer/definition and tag rules.
                    bool matches = string.IsNullOrEmpty(requiredTag) || properties.tags?.Contains(requiredTag) == true;
                    if (!matches && alternatives != null)
                    {
                        for (int k = 0; k < alternatives.Count; k++)
                        {
                            if (properties.tags?.Contains(alternatives[k].tag) == true)
                            {
                                matches = true;
                                break;
                            }
                        }
                    }
                    if (matches)
                        Add(result, def, category);
                }
            }
        }

        private static bool HasAnyTag(List<string> tags, List<string> matches)
        {
            if (tags == null || matches == null)
                return false;

            for (int i = 0; i < matches.Count; i++)
            {
                if (tags.Contains(matches[i]))
                    return true;
            }
            return false;
        }

        private static void Add(Dictionary<ThingDef, int> result, ThingDef def, RimKataTargetCategory category)
        {
            result.TryGetValue(def, out int mask);
            result[def] = mask | (1 << (int)category);
        }
    }
}
