using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataSubdueJobs
    {
        internal static bool IsAttackJob(Job job)
            => job != null && (job.def == JobDefOf.AttackMelee
                || job.def == JobDefOf.AttackStatic
                || job.def == RimKataDefOf.RimKata_Attack
                || job.def == RimKataDefOf.RimKata_SubdueCombat);

        // On rejection, the caller still owns disposal of the incoming job.
        internal static bool PrepareJob(Pawn pawn, Job newJob,
            ref bool? keepCarryingThingOverride)
        {
            if (!IsAttackJob(newJob)) return true;
            RimKataSubdueState state = RimKataSubdueUtility.Get(pawn);
            if (state == null) return true;
            if (!state.attackAllowed || !RimKataSubdueUtility.IsRelationValid(state)) return false;

            bool approach = newJob.def == JobDefOf.AttackMelee
                || newJob.def == RimKataDefOf.RimKata_SubdueCombat && newJob.count == 1;
            ThingWithComps weapon = newJob.verbToUse?.EquipmentSource as ThingWithComps;
            if (weapon == null || weapon.Destroyed
                || pawn.equipment?.AllEquipmentListForReading.Contains(weapon) != true
                || !RimKataWeaponSlotUtility.CanUseOneHandWeapon(pawn, weapon, true))
                weapon = pawn.equipment?.Primary;
            if (!RimKataSubdueCombat.TrySetExternalTarget(state, weapon,
                newJob.targetA, requireReachNow: !approach)) return false;

            newJob.def = RimKataDefOf.RimKata_SubdueCombat;
            newJob.count = approach ? 1 : 0;
            newJob.targetB = weapon;
            newJob.verbToUse = state.attackVerb;
            state.externalOrderJobId = newJob.loadID;
            newJob.killIncappedTarget = false;
            newJob.canUseRangedWeapon = false;
            // Native cleanup also evaluates the previous job when deciding to drop the held pawn.
            keepCarryingThingOverride = true;
            return true;
        }

        internal static bool IsApproaching(RimKataSubdueState state)
            => state?.pawn?.jobs?.curDriver is JobDriver_RimKataSubdueCombat driver
                && state.externalOrderJobId == driver.job.loadID
                && driver.Approaching && driver.job.targetA == state.externalTarget;

        internal static bool IsMeleeOrder(RimKataSubdueState state)
            => state?.pawn?.jobs?.curDriver is JobDriver_RimKataSubdueCombat driver
                && state.externalOrderJobId == driver.job.loadID
                && driver.job.count == 1 && driver.job.targetA == state.externalTarget
                && driver.job.targetB.Thing == state.externalWeapon;

        internal static void StopAttackJob(Pawn pawn)
        {
            if (pawn?.CurJobDef == RimKataDefOf.RimKata_SubdueCombat)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
        }
    }

    public sealed class JobDriver_RimKataSubdueCombat : JobDriver
    {
        private bool approaching;
        internal bool Approaching => approaching;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref approaching, "subdueApproaching");
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        private bool HasCurrentOrder()
        {
            RimKataSubdueState state = RimKataSubdueUtility.Get(pawn);
            Thing target = job.targetA.Thing;
            return state != null && state.attackAllowed
                && RimKataSubdueUtility.IsRelationValid(state)
                && state.externalOrderJobId == job.loadID
                && state.HasExternalTarget && state.externalTarget == job.targetA
                && state.externalWeapon == job.targetB.Thing
                && target?.Spawned == true && !target.Destroyed && target.Map == pawn.Map
                && (!(target is Pawn victim) || !victim.Dead && !victim.Downed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !HasCurrentOrder());
            Toil approach = null;
            if (job.count == 1)
            {
                approach = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
                approach.AddPreInitAction(() => approaching = true);
                approach.AddFinishAction(() => approaching = false);
                yield return approach;
            }

            Toil combat = ToilMaker.MakeToil("RimKataSubdueCombatWait");
            combat.initAction = () => approaching = false;
            combat.defaultCompleteMode = ToilCompleteMode.Never;
            if (approach != null)
            {
                combat.tickAction = () =>
                {
                    if (!pawn.CanReachImmediate(job.targetA, PathEndMode.Touch))
                    {
                        approaching = true;
                        JumpToToil(approach);
                    }
                };
            }
            yield return combat;
        }
    }
}
