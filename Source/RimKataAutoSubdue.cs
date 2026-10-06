using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Only a selected close target or a successful stun event can add a pending pair.
    // The registry consumes it outside the attack/damage stack; no pawn search is needed.
    internal static class RimKataAutoSubdue
    {
        internal struct Pending
        {
            internal Pawn pawn, target;
            internal Job job;
            internal RimKataPawnCombatState combat;
            internal int requestedTick;
        }

        internal static bool Request(Pawn pawn, Pawn target, RimKataPawnCombatState combat)
        {
            var registry = RimKataSubdueUtility.Registry;
            if (registry == null || !CanTake(pawn, target, out _)) return false;
            var pending = new Pending { pawn = pawn, target = target, job = pawn.CurJob,
                combat = combat, requestedTick = Find.TickManager.TicksGame };
            for (int i = 0; i < registry.pending.Count; i++)
            {
                if (registry.pending[i].pawn != pawn) continue;
                // Repeated native attack attempts must not postpone the queued handoff.
                if (registry.pending[i].target == target && registry.pending[i].job == pending.job) return true;
                ClearFlag(registry.pending[i]);
                registry.pending[i] = pending;
                if (combat != null) combat.autoSubduePending = true;
                RimKataGroundPoseUtility.RiseForAutoSubdue(combat);
                return true;
            }
            registry.pending.Add(pending);
            if (combat != null) combat.autoSubduePending = true;
            RimKataGroundPoseUtility.RiseForAutoSubdue(combat);
            return true;
        }

        internal static void RequestFromStun(Pawn victim, Thing instigator)
        {
            if (!(instigator is Pawn pawn) || pawn == victim) return;
            var entry = RimKataResponseVisualParticipantCache.BodyVisualFor(pawn);
            if (entry?.registeredQualified != true) return;
            var combat = entry.equipmentState;
            Job job = pawn.CurJob;
            bool attacking = RimKataFireContext.Shooter == pawn && RimKataFireContext.CloseTarget == victim
                || combat?.dualCloseTarget == victim || combat?.closeCombatTrigger == victim
                || combat?.flyingKick?.target == victim || combat?.kick?.target == victim
                || job?.targetA.Pawn == victim && RimKataSubdueJobs.IsAttackJob(job)
                || pawn.mindState?.meleeThreat == victim;
            if (attacking) Request(pawn, victim, combat);
        }

        internal static bool CanTake(Pawn pawn, Pawn target, out RimKataSettings settings)
        {
            settings = null;
            if (pawn == null || target?.stances?.stunner.Stunned != true) return false;
            settings = RimKataTargetAccess.SettingsFor(pawn);
            if (settings?.autoSubdueEnabled != true || !settings.subdueEnabled
                || pawn?.Spawned != true || pawn.Dead || pawn.Downed || !pawn.Awake()
                || pawn.InMentalState || pawn.stances?.stunner.Stunned == true
                || pawn.carryTracker == null || pawn.carryTracker.CarriedThing != null
                || target?.Spawned != true || target == pawn || target.Dead
                || target.Map != pawn.Map
                || settings.autoSubdueReleaseDowned && target.Downed
                || !target.HostileTo(pawn) || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return false;
            double maximum = RimKataStrengthUtility.BodyMass(pawn)
                * (settings.GetSubdueMassMultiplier(pawn) + (double)RimKataStrengthUtility.Bonus(pawn));
            return RimKataStrengthUtility.BodyMass(target) <= maximum;
        }

        internal static void ProcessPending(RimKataSubdueRegistry registry)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = registry.pending.Count - 1; i >= 0; i--)
            {
                var pending = registry.pending[i];
                if (pending.requestedTick >= now) continue;
                Pawn pawn = pending.pawn;
                var entry = RimKataResponseVisualParticipantCache.BodyVisualFor(pawn);
                bool valid = entry?.registeredQualified == true && pawn.CurJob == pending.job
                    && pawn.Spawned && pending.target.Spawned && pending.target.Map == pawn.Map
                    && !pawn.Dead && !pawn.Downed && !pending.target.Dead
                    && pending.target.stances?.stunner.Stunned == true;
                var combat = entry?.equipmentState;
                // Finish existing special motion before taking ownership of the held pawn.
                if (valid && (combat?.flyingKick != null || combat?.kick?.motionActive == true
                    || combat?.reactiveMotion != null || combat?.groundPose != null
                    || combat?.DodgeMovementActive == true || combat?.DeflectionSpinActive == true
                    || entry.breach.HasValue)) continue;
                registry.pending.RemoveAt(i);
                ClearFlag(pending);
                if (!valid || !CanTake(pawn, pending.target, out _)
                    || !RimKataEligibility.HasActiveRimKataAccess(pawn)) continue;
                Job hold = JobMaker.MakeJob(RimKataDefOf.RimKata_Subdue, pending.target);
                hold.count = 1; // Automatic: already in Touch range, then remain in the hold toil.
                hold.canUseRangedWeapon = false;
                pawn.jobs.StartJob(hold, JobCondition.InterruptForced, keepCarryingThingOverride: true);
            }
        }

        private static void ClearFlag(Pending pending)
        {
            if (pending.combat != null) pending.combat.autoSubduePending = false;
        }

        internal static void NotifyManualControl(RimKataSubdueState state)
        {
            if (state == null) return;
            state.automatic = false;
            state.automaticReleaseTick = -1;
        }

        internal static void ClearHeldStun(RimKataSubdueState state)
        {
            if (!state.automatic) return;
            state.target.stances?.stunner.StopStun();
            RimKataTemporaryInactivity.RefreshExisting(state.target);
        }

        internal static bool ShouldRelease(RimKataSubdueState state, int now)
            => state.automatic && (now >= state.automaticReleaseTick
                || state.target.Downed && RimKataTargetAccess.SettingsFor(state.pawn)?.autoSubdueReleaseDowned != false);

        internal static int ReleaseTick(int now, int duration)
            => (int)Math.Min(int.MaxValue, (long)now + Math.Max(0, duration));
    }
}
