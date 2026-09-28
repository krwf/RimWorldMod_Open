using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataBreachEvents
    {
        internal static bool PreserveAIDoorAttack(Pawn pawn, Job job)
        {
            // Keep the ordinary approach and AI decision until a real swing at
            // this door. Neutral pawns and animals use the same event entry.
            return job?.def == JobDefOf.AttackMelee
                && job.targetA.Thing is Building_Door door && door.Spawned && !door.Open
                && pawn?.IsPlayerControlled == false
                && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                && RimKataTargetAccess.SettingsFor(pawn)?.breachEnabled == true;
        }

        internal static void DiscardUnstarted(Pawn pawn, Job created)
        {
            if (created?.def != RimKataDefOf.RimKata_Breach || pawn?.CurJob == created) return;
            RimKataBreachState state = RimKataBreachUtility.Get(pawn);
            if (state?.ownerJobId != created.loadID) return;
            state.Drop();
            RimKataBreachUtility.Remove(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_PawnJobStart_RimKataBreach
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Pawn ___pawn, ref Job newJob, bool __runOriginal, out Job __state)
        {
            __state = null;
            if (!__runOriginal || newJob == null) return;
            RimKataBreachUtility.NotifyJobStarting(___pawn, newJob);

            // Queued orders stay ordinary Jobs until they are actually started.
            // Merely shift-clicking a door must not stop the current combat cycle.
            if (newJob.def != JobDefOf.AttackMelee || ___pawn?.IsPlayerControlled != true) return;
            if (newJob.playerForced && ___pawn.Drafted
                && newJob.targetA.Thing is Building_Door door
                && RimKataBreachUtility.TryCreate(___pawn, door, true, out Job replacement))
            {
                Job previous = newJob;
                newJob = replacement;
                __state = replacement;
                ___pawn.ClearReservationsForJob(previous);
                JobMaker.ReturnToPool(previous);
                return;
            }
            RimKataDualWeaponController.TryConvertStructureMeleeJob(___pawn, newJob);
        }

        private static Exception Finalizer(Pawn ___pawn, Job __state, Exception __exception)
        {
            RimKataBreachEvents.DiscardUnstarted(___pawn, __state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), nameof(Pawn_MeleeVerbs.TryMeleeAttack))]
    internal static class Patch_PawnMeleeAttempt_RimKataBreach
    {
        private static readonly AccessTools.FieldRef<JobDriver_AttackMelee, int> AttackCount =
            AccessTools.FieldRefAccess<JobDriver_AttackMelee, int>("numMeleeAttacksMade");

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Pawn_MeleeVerbs __instance, Pawn ___pawn, Thing target,
            Verb verbToUse, ref bool __result)
        {
            Job current = ___pawn?.CurJob;
            if (current?.def != JobDefOf.AttackMelee || current.targetA.Thing != target
                || !(target is Building) || !RimKataEligibilityCache.IsCachedQualifiedPawn(___pawn)
                || ___pawn.stances.FullBodyBusy || !___pawn.kindDef.canMeleeAttack
                || !___pawn.CanReachImmediate(target, PathEndMode.Touch)) return true;
            Verb verb = verbToUse ?? __instance.TryGetMeleeVerb(target);
            if (verb?.IsMeleeAttack != true || !verb.IsStillUsableBy(___pawn)
                || !verb.CanHitTarget(target)) return true;

            // Also upgrades an already-running ordinary melee Job loaded from
            // a save. A failed door snapshot falls back to two melee cycles.
            bool player = ___pawn.IsPlayerControlled;
            Job replacement = null;
            int originalStartTick = current.startTick;
            bool preserveTiming = false;
            if (target is Building_Door door && (!player || current.playerForced && ___pawn.Drafted))
                RimKataBreachUtility.TryCreate(___pawn, door, player, out replacement);
            if (replacement == null)
            {
                replacement = current.Clone();
                // Clone preserves loadID; a different active Job needs its own ID.
                replacement.loadID = Find.UniqueIDsManager.GetNextJobID();
                if (___pawn.jobs.curDriver is JobDriver_AttackMelee meleeDriver)
                    replacement.maxNumMeleeAttacks = Math.Max(1,
                        current.maxNumMeleeAttacks - AttackCount(meleeDriver));
                if (!RimKataDualWeaponController.TryConvertStructureMeleeJob(___pawn, replacement))
                { JobMaker.ReturnToPool(replacement); return true; }
                preserveTiming = true;
            }

            try
            {
                // Only the active door attack is replaced. Never ClearQueuedJobs
                // or resume the obsolete door target after destroying that door.
                ___pawn.jobs.StartJob(replacement, JobCondition.InterruptForced,
                    current.jobGiver, resumeCurJobAfterwards: false,
                    thinkTree: current.jobGiverThinkTree);
                if (preserveTiming && ___pawn.CurJob == replacement)
                    replacement.startTick = originalStartTick;
            }
            finally { RimKataBreachEvents.DiscardUnstarted(___pawn, replacement); }
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver_Mine), "DoDamage")]
    internal static class Patch_MiningHit_RimKataBreach
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(JobDriver_Mine __instance, Thing target, Pawn actor,
            bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!(target is Building_Door door) || actor == null
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(actor)
                || actor.IsPlayerControlled) return true;

            Job current = actor.CurJob;
            if (current?.def != JobDefOf.Mine || current.targetA.Thing != door
                || actor.jobs.curDriver != __instance || __instance.job != current
                || !actor.CanReachImmediate(door, PathEndMode.Touch)
                || !RimKataBreachUtility.TryCreate(actor, door, false, out Job replacement))
                return true;

            try
            {
                // Animals and sappers may mine a door instead of using AttackMelee.
                // Keep their approach and hit timer; intercept only this actual hit.
                actor.jobs.StartJob(replacement, JobCondition.InterruptForced,
                    current.jobGiver, resumeCurJobAfterwards: false,
                    thinkTree: current.jobGiverThinkTree);
            }
            finally { RimKataBreachEvents.DiscardUnstarted(actor, replacement); }

            // The door stays intact during the run-up. The old mining callback
            // only resets its own hit timer after this skipped damage call.
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    internal static class Patch_PawnPathStarted_RimKataBreach
    {
        private static void Postfix(Pawn ___pawn) => RimKataBreachUtility.NotifyPathStarted(___pawn);
    }

    [HarmonyPatch(typeof(JobDriver_Wait), nameof(JobDriver_Wait.DecorateWaitToil))]
    internal static class Patch_WaitToil_RimKataBreach
    {
        private static void Postfix(JobDriver_Wait __instance, Toil wait)
            => RimKataBreachUtility.AttachWaitToil(__instance.pawn, wait);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    internal static class Patch_PawnDespawn_RimKataBreach
    {
        private static void Prefix(Pawn __instance)
        {
            RimKataBreachState state = RimKataBreachUtility.Get(__instance);
            if (state == null) return;
            state.Drop();
            RimKataBreachUtility.Remove(__instance);
        }
    }
}
