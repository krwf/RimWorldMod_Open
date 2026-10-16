using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed partial class RimKataSubdueState
    {
        // Reflected damage can reenter this exact relation during transfer.
        internal bool redirectingDamage;
    }

    internal static class RimKataSubdueDefense
    {
        internal static bool IsDamageTransferEnabled(RimKataSubdueState state)
            => RimKataTargetAccess.SettingsFor(state.pawn)?.subdueDamageTransferEnabled != false;

        internal static bool HoldsLivingTarget(Pawn pawn)
        {
            var state = RimKataSubdueUtility.Get(pawn);
            return RimKataSubdueUtility.IsRelationValid(state) && !state.target.Dead
                && IsDamageTransferEnabled(state);
        }

        internal static bool ReleaseForDefense(Pawn pawn, Thing closeTarget = null)
        {
            return ReleaseKnownForDefense(RimKataSubdueUtility.Get(pawn), closeTarget);
        }

        internal static bool ReleaseKnownForDefense(RimKataSubdueState state, Thing closeTarget = null,
            bool? knownAdjacent = null)
        {
            if (state == null || IsDamageTransferEnabled(state)) return false;
            Pawn pawn = state.pawn;
            if (closeTarget != null && (closeTarget == state.target || !closeTarget.Spawned
                || closeTarget.Map != pawn.Map
                || !(knownAdjacent ?? pawn.CanReachImmediate(closeTarget, PathEndMode.Touch))))
                return false;
            RimKataSubdueUtility.Release(state);
            return RimKataSubdueUtility.Get(pawn) != state;
        }

        internal static bool TryTransfer(Pawn carrier, DamageInfo damage)
        {
            var state = RimKataSubdueUtility.Get(carrier);
            if (state == null) return false;
            if (!RimKataSubdueUtility.IsRelationValid(state) || state.target.Dead)
            {
                RimKataSubdueUtility.Remove(state);
                return false;
            }
            if (!IsDamageTransferEnabled(state)) return false;

            // A close-shot miss can still reach TakeDamage before the defense hook consumes it.
            if (RimKataDefenseUtility.TryGetCloseAttackData(carrier,
                    out bool meleeResolution, out bool meleeHit)
                && ((meleeResolution && !meleeHit)
                    || (RimKataDefenseUtility.TryGetCloseAttackResolution(carrier,
                            out bool avoided) && avoided)))
                return false;

            if (state.redirectingDamage) return true;
            state.redirectingDamage = true;
            try
            {
                // The original body part belongs to the carrier, not the recipient.
                damage.SetHitPart(null);
                state.target.TakeDamage(damage);
            }
            finally
            {
                state.redirectingDamage = false;
                if (!RimKataSubdueUtility.IsRelationValid(state) || state.target.Dead)
                    RimKataSubdueUtility.Remove(state);
            }
            // Recipient death can end the relation during TakeDamage; this hit is still consumed.
            return true;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    internal static class Patch_Thing_RimKataSubdueDamage
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Thing __instance, DamageInfo dinfo,
            ref DamageWorker.DamageResult __result)
        {
            // TakeDamage applies ThingDef multipliers before PreApplyDamage.
            if (!RimKataSubdueUtility.Any || !(__instance is Pawn carrier)
                || !RimKataSubdueDefense.TryTransfer(carrier, dinfo)) return true;
            __result = new DamageWorker.DamageResult();
            return false;
        }
    }
}
