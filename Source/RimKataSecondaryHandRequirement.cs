using System.Collections.Generic;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataSecondaryHandRequirement
    {
        // Only used by the equipment menu, the pickup toil, and a missing-part event.
        // General slot validation deliberately allows developer-forced equipment.
        internal static bool HasMissingHand(Pawn pawn)
        {
            HediffSet hediffs = pawn?.health?.hediffSet;
            List<BodyPartRecord> parts = pawn?.RaceProps?.body?.AllParts;
            if (hediffs == null || parts == null) return false;

            for (int i = 0; i < parts.Count; i++)
            {
                BodyPartRecord part = parts[i];
                if (IsHand(part) && IsMissing(hediffs, part))
                    return true;
            }
            return false;
        }

        private static bool IsMissing(HediffSet hediffs, BodyPartRecord part)
        {
            // An added arm supplies the hand although vanilla marks its natural children missing.
            BodyPartRecord effectivePart = part;
            for (BodyPartRecord ancestor = part; ancestor != null; ancestor = ancestor.parent)
            {
                if (hediffs.HasDirectlyAddedPartFor(ancestor)) effectivePart = ancestor;
            }
            for (BodyPartRecord ancestor = effectivePart; ancestor != null; ancestor = ancestor.parent)
            {
                if (hediffs.PartIsMissing(ancestor)) return true;
            }
            return false;
        }

        private static bool IsHand(BodyPartRecord part)
        {
            if (part?.def == null) return false;
            if (part.def == BodyPartDefOf.Hand) return true;
            // The segment tag also marks shoulders and internal arm bones.
            if (part.depth != BodyPartDepth.Outside
                || part.def.tags?.Contains(BodyPartTagDefOf.ManipulationLimbSegment) != true) return false;
            for (BodyPartRecord ancestor = part.parent; ancestor != null; ancestor = ancestor.parent)
            {
                if (ancestor.def.tags?.Contains(BodyPartTagDefOf.ManipulationLimbCore) == true) return true;
            }
            return false;
        }

        private static bool ContainsHand(BodyPartRecord part)
        {
            if (part == null) return false;
            if (IsHand(part)) return true;
            for (int i = 0; i < part.parts.Count; i++)
            {
                if (ContainsHand(part.parts[i])) return true;
            }
            return false;
        }

        internal static void NotifyMissingPartAdded(Pawn pawn, Hediff hediff)
        {
            if (!(hediff is Hediff_MissingPart) || !ContainsHand(hediff.Part)
                || !HasMissingHand(pawn)) return;

            RimKataSecondaryWeaponRegistry registry = RimKataSecondaryWeaponRegistry.CurrentRegistry;
            // AddHediff has already processed downing, which may have recorded automatic recovery.
            registry?.RemoveRecovery(pawn);
            ThingWithComps secondary = registry?.GetRegistered(pawn);
            if (pawn?.Spawned == true && secondary != null)
                RimKataWeaponSlotUtility.RemoveInvalidSecondary(pawn, secondary, forbidDropped: true);
        }
    }
}
