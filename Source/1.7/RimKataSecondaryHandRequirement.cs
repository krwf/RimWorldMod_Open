using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataSecondaryHandRequirement
    {
        private sealed class LimbState
        {
            internal HediffSet hediffs;
            internal BodyDef body;
            internal bool enough;
        }

        private static ConditionalWeakTable<Pawn, LimbState> states = new ConditionalWeakTable<Pawn, LimbState>();

        internal static bool HasEnoughManipulationLimbs(Pawn pawn)
        {
            HediffSet hediffs = pawn?.health?.hediffSet;
            BodyDef body = pawn?.RaceProps?.body;
            // Early initialization is not a confirmed shortage and must not be cached.
            if (hediffs == null || body?.AllParts == null) return false;
            LimbState state = states.GetValue(pawn, _ => new LimbState());
            if (state.hediffs != hediffs || state.body != body)
            {
                state.enough = CountUsableLimbs(hediffs, body) >= 2;
                state.hediffs = hediffs;
                state.body = body;
            }
            return state.enough;
        }

        internal static bool HasInsufficientManipulationLimbs(Pawn pawn)
            => pawn?.health?.hediffSet != null && pawn.RaceProps?.body?.AllParts != null
                && !HasEnoughManipulationLimbs(pawn);

        internal static void Reset() => states = new ConditionalWeakTable<Pawn, LimbState>();

        internal static void Forget(Pawn pawn)
        {
            if (pawn != null) states.Remove(pawn);
        }

        internal static void NotifyHealthChanged(Pawn pawn, Hediff hediff)
        {
            if (hediff is Hediff_MissingPart || hediff is Hediff_AddedPart)
                NotifyPartRestored(pawn);
        }

        internal static void NotifyPartRestored(Pawn pawn)
        {
            if (pawn == null || !states.TryGetValue(pawn, out LimbState state)) return;
            state.hediffs = null;
            RimKataDualWeaponController.InvalidateWeaponBindings(pawn);
        }

        private static int CountUsableLimbs(HediffSet hediffs, BodyDef body)
        {
            // Vanilla caches the structural list per BodyDef. Each core is one limb,
            // regardless of how many hands or segments are defined below it.
            List<BodyPartRecord> cores = body.GetPartsWithTag(BodyPartTagDefOf.ManipulationLimbCore);
            int count = 0;
            for (int i = 0; i < cores.Count; i++)
            {
                BodyPartRecord core = cores[i];
                if (!IsMissing(hediffs, core) && !HasMissingSegment(hediffs, core) && ++count >= 2)
                    break;
            }
            return count;
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

        private static bool HasMissingSegment(HediffSet hediffs, BodyPartRecord core)
        {
            for (int i = 0; i < core.parts.Count; i++)
            {
                BodyPartRecord part = core.parts[i];
                if (part?.def == null) continue;
                List<BodyPartTagDef> tags = part.def.tags;
                // Nested cores are counted separately; fingers are not extra limbs or
                // a new disqualifier. Preserve the former external-segment policy.
                if (tags?.Contains(BodyPartTagDefOf.ManipulationLimbCore) == true
                    || tags?.Contains(BodyPartTagDefOf.ManipulationLimbDigit) == true) continue;
                if ((part.def == BodyPartDefOf.Hand
                    || (part.depth == BodyPartDepth.Outside
                        && tags?.Contains(BodyPartTagDefOf.ManipulationLimbSegment) == true))
                    && IsMissing(hediffs, part)) return true;
                if (HasMissingSegment(hediffs, part)) return true;
            }
            return false;
        }

        internal static void NotifyMissingPartAdded(Pawn pawn, Hediff hediff)
        {
            if (!(hediff is Hediff_MissingPart) || pawn == null
                || (!states.TryGetValue(pawn, out _) && !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
                || !HasInsufficientManipulationLimbs(pawn)) return;

            RimKataSecondaryWeaponRegistry registry = RimKataSecondaryWeaponRegistry.CurrentRegistry;
            // AddHediff has already processed downing, which may have recorded automatic recovery.
            registry?.RemoveRecovery(pawn);
            ThingWithComps secondary = registry?.GetRegistered(pawn);
            if (pawn?.Spawned == true && secondary != null)
                RimKataWeaponSlotUtility.RemoveInvalidSecondary(pawn, secondary, forbidDropped: true);
        }
    }
}
