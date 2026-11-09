using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static partial class RimKataSubdueCombat
    {
        // Selection only. The caller separately checks the held-target execution bridge.
        private static bool TryGetHeldWeaponAttack(Pawn pawn,
            out ThingWithComps weapon, out Verb verb)
        {
            weapon = null;
            verb = null;
            ThingWithComps primary = pawn?.equipment?.Primary;
            if (primary == null || primary.Destroyed
                || !RimKataEquipmentUtility.IsWeaponEnabled(primary.def)) return false;
            Verb selected = RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            if (selected == null || !selected.IsStillUsableBy(pawn)) return false;
            weapon = primary;
            verb = selected;
            return true;
        }

        private static Verb SelectBodyMelee(Pawn pawn)
        {
            if (pawn?.meleeVerbs == null) return null;
            Verb best = null;
            float score = -1f;
            // false is the native non-terrain list. Keep its usability checks and the
            // existing subdue damage comparison while narrowing the actual owner.
            foreach (VerbEntry entry in pawn.meleeVerbs.GetUpdatedAvailableVerbsList(false))
            {
                Verb verb = entry.verb;
                if (!(verb is Verb_MeleeAttackDamage) || !IsBodyMeleeSource(pawn, verb)
                    || !verb.IsStillUsableBy(pawn)) continue;
                float damage = verb.verbProps.AdjustedMeleeDamageAmount(verb, pawn);
                if (damage > score) { best = verb; score = damage; }
            }
            return best;
        }

        private static bool IsBodyMeleeSource(Pawn pawn, Verb verb)
        {
            Tool tool = verb.tool;
            if (tool == null || verb.EquipmentSource != null || verb.TerrainSource != null)
                return false;
            if (ReferenceEquals(verb.DirectOwner, pawn))
                return pawn.def.tools?.Contains(tool) == true;

            HediffComp_VerbGiver giver = verb.HediffCompSource;
            if (giver != null)
            {
                // A bionic fist is body-attached even though its owner is a Hediff.
                // Whole-body buffs/abilities without a specific part are not body tools.
                return giver.parent?.pawn == pawn && giver.parent.Part != null
                    && giver.Props.tools?.Contains(tool) == true;
            }

            // Mutants own their replacement body tools separately from the race tracker.
            return pawn.mutant != null && ReferenceEquals(verb.DirectOwner, pawn.mutant)
                && pawn.mutant.Def.tools?.Contains(tool) == true;
        }
    }
}
