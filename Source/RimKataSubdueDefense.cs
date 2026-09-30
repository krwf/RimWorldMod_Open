using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed partial class RimKataSubdueState
    {
        // Damage callbacks can reflect damage back to the carrier. Keep this
        // guard on the exact relation, so unrelated damage can still transfer.
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
            var state = RimKataSubdueUtility.Get(pawn);
            if (state == null || IsDamageTransferEnabled(state)) return false;
            if (closeTarget != null && (closeTarget == state.target || !closeTarget.Spawned
                || closeTarget.Map != pawn.Map || !pawn.CanReachImmediate(closeTarget, PathEndMode.Touch)))
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

            // A close shot can reach TakeDamage even when its melee result is
            // a miss. Let the existing defense hook consume that result.
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
                // The original body part belongs to the carrier's body. All
                // other damage properties stay intact; the held pawn applies
                // its own body-part selection, damage factors and armor once.
                damage.SetHitPart(null);
                state.target.TakeDamage(damage);
            }
            finally
            {
                state.redirectingDamage = false;
                if (!RimKataSubdueUtility.IsRelationValid(state) || state.target.Dead)
                    RimKataSubdueUtility.Remove(state);
            }
            // The recipient may have died and ended the relation above. This
            // hit is still consumed and must never fall back onto the carrier.
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
            // Redirect at entry, before either pawn's mitigation is applied.
            if (!RimKataSubdueUtility.Any || !(__instance is Pawn carrier)
                || !RimKataSubdueDefense.TryTransfer(carrier, dinfo)) return true;
            __result = new DamageWorker.DamageResult();
            return false;
        }
    }
}
