using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Reuse Wait's native target acquisition. The scope exists only while that
    // call runs; neither the hold registry nor a new scanner searches for foes.
    internal static class RimKataSubdueAutomaticFire
    {
        [ThreadStatic] private static RimKataSubdueState acquiring;

        internal static RimKataSubdueState Begin()
        {
            var previous = acquiring;
            acquiring = null;
            return previous;
        }

        internal static void End(RimKataSubdueState previous) => acquiring = previous;

        internal static bool CanAcquire(RimKataSubdueState state)
            => state != null && state.attackAllowed && !state.attackEnabled
                && !state.HasExternalTarget && state.weapon != null
                && state.attackVerb != null && RimKataSubdueUtility.IsRelationValid(state);

        internal static bool IsCarryingPawn(Pawn pawn, Pawn carriedPawn)
        {
            if (!PawnUtility.IsCarryingPawn(pawn, carriedPawn)) return false;
            var state = RimKataSubdueUtility.Get(pawn);
            if (!CanAcquire(state)) return true;
            acquiring = state;
            return false;
        }

        internal static bool FireAtWill(Pawn_DraftController drafter)
            => acquiring?.pawn == drafter.pawn ? acquiring.attackAllowed : drafter.FireAtWill;

        internal static bool AllowsRangedSearch(bool allowed, Pawn pawn)
            => allowed && (acquiring?.pawn == pawn ? CanAcquire(acquiring)
                : !RimKataDualWeaponController.ShouldSuppressVanillaTargetSearch(pawn));

        internal static bool IsAcquiring(Verb verb, Pawn pawn)
            => acquiring != null && acquiring.pawn == pawn && acquiring.attackVerb == verb
                && CanAcquire(acquiring);

        internal static bool TryStartAttack(Pawn pawn, LocalTargetInfo target)
        {
            if (acquiring?.pawn != pawn) return pawn.TryStartAttack(target);
            return CanAcquire(acquiring) && acquiring.attackVerb.TryStartCastOn(
                target, LocalTargetInfo.Invalid, false, true, false, false);
        }

        internal static bool TryTakeOpening(Verb verb, LocalTargetInfo target, out bool result)
        {
            result = false;
            if (!IsAcquiring(verb, verb?.CasterPawn)) return false;
            var state = acquiring;
            result = RimKataSubdueCombat.TrySetExternalTarget(state, state.weapon, target, true);
            // Take ownership at the same TryStartCastOn boundary as the main
            // opening hook. Do not run a native zero-warmup shot as well.
            return true;
        }

        internal static bool TryGetVerb(Pawn pawn, out Verb verb)
        {
            verb = null;
            if (acquiring == null || acquiring.pawn != pawn) return false;
            verb = acquiring.attackVerb;
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.CurrentEffectiveVerb), MethodType.Getter)]
    internal static class Patch_PawnEffectiveVerb_RimKataSubdueAcquisition
    {
        private static bool Prefix(Pawn __instance, ref Verb __result)
        {
            if (!RimKataSubdueAutomaticFire.TryGetVerb(__instance, out Verb verb)) return true;
            __result = verb;
            return false;
        }
    }
}
