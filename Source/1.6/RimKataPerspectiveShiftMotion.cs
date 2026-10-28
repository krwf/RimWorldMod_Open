using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static partial class RimKataPerspectiveShiftCompat
    {
        // PS controls one avatar. Only its approved, explicitly ordered approach needs extra state.
        private sealed class MeleeApproach : IExposable
        {
            internal Pawn pawn, target;
            internal Job job;
            internal IntVec3 targetCell;
            internal bool started;
            private int jobId = -1;

            public MeleeApproach() { }

            public void ExposeData()
            {
                if (Scribe.mode == LoadSaveMode.Saving) jobId = job?.loadID ?? -1;
                Scribe_References.Look(ref pawn, "pawn");
                Scribe_References.Look(ref target, "target");
                Scribe_Values.Look(ref jobId, "jobId", -1);
                Scribe_Values.Look(ref targetCell, "targetCell");
                Scribe_Values.Look(ref started, "started");
                if (Scribe.mode == LoadSaveMode.PostLoadInit
                    && pawn?.CurJob?.loadID == jobId && pawn.CurJob.targetA.Pawn == target)
                    job = pawn.CurJob;
            }
        }
        private static MeleeApproach meleeApproach;

        internal static void ResetMotion() => meleeApproach = null;

        internal static void ExposeMotionInput()
        {
            Scribe_Deep.Look(ref meleeApproach, "RimKataPSMeleeApproach");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && meleeApproach?.job == null)
                meleeApproach = null;
        }

        internal static void ApplyMotion(Harmony harmony, Assembly psAssembly)
        {
            Type avatar = psAssembly.GetType("PerspectiveShift.Avatar");
            MethodInfo carrying = avatar == null ? null : AccessTools.DeclaredMethod(avatar, "HandleCarriedThing", Type.EmptyTypes);
            if (carrying?.ReturnType != typeof(void) || carrying.IsStatic
                || AccessTools.Field(avatar, "pawn")?.FieldType != typeof(Pawn))
                throw new MissingMethodException("Perspective Shift held-pawn API did not match.");
            RimKataStartupPatches.Patch(harmony, carrying,
                prefix: new HarmonyMethod(typeof(RimKataPerspectiveShiftCompat), nameof(KeepSubduedTarget)));
            RimKataStartupPatches.Patch(harmony,
                AccessTools.Method(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob)),
                postfix: new HarmonyMethod(typeof(RimKataPerspectiveShiftCompat), nameof(AfterMotionJob))
                    { priority = Priority.Last });
        }

        private static bool KeepSubduedTarget(Pawn ___pawn)
        {
            if (!IsControlled(___pawn) || !___pawn.Drafted) return true;
            var held = RimKataSubdueUtility.Get(___pawn);
            return held == null || held.target != ___pawn.carryTracker?.CarriedThing;
        }

        private static void AfterMotionJob(Pawn ___pawn, bool __runOriginal)
        {
            if (__runOriginal && IsControlled(___pawn)) NotifyMeleeOrder(___pawn, ___pawn.CurJob);
        }

        internal static void NotifyMeleeOrder(Pawn pawn, Job job)
        {
            if (!IsControlled(pawn)) return;
            if (meleeApproach?.pawn == pawn)
            {
                if (meleeApproach.job == job) return;
                meleeApproach = null;
                SyncAvatarPosition(pawn);
            }
            if (!pawn.Drafted || job?.playerForced != true || job != pawn.CurJob
                || !(job.targetA.Thing is Pawn target)
                || !RimKataFlyingKick.CanControlApproach(pawn, target)) return;
            int distance = RimKataFlyingKick.Distance(pawn.Position, target.Position);
            if (distance < 2 || !HasKickApproach(pawn, target)) return;
            meleeApproach = new MeleeApproach {
                pawn = pawn, target = target, job = job, targetCell = target.Position
            };
            OffenseStopped = false;
            CombatTarget(pawn, target, false);
            if (distance == 2) RimKataFlyingKick.WaitAfterPush(pawn, target, job);
        }

        internal static Thing MotionTarget(Pawn pawn)
            => meleeApproach?.pawn == pawn ? meleeApproach.target : null;

        internal static bool OwnsMovement(Pawn pawn)
        {
            if (!IsControlled(pawn)) return false;
            if (pawn.jobs?.curDriver is JobDriver_RimKataBreach
                && RimKataBreachUtility.Get(pawn) != null) return true;
            MeleeApproach approach = meleeApproach;
            if (approach?.pawn == pawn)
            {
                if (pawn.CurJob != approach.job || approach.target?.Spawned != true
                    || approach.target.Map != pawn.Map || pawn.Dead || pawn.Downed || !pawn.Drafted)
                {
                    EndMeleeApproach(pawn, cancelJob: false);
                    return false;
                }
                var flight = RimKataFlyingKick.Current(pawn);
                if (approach.started && flight?.attackResolved == true) return true;
                if (approach.targetCell != approach.target.Position)
                {
                    approach.targetCell = approach.target.Position;
                    if (!HasKickApproach(pawn, approach.target, flight != null))
                    {
                        EndMeleeApproach(pawn, cancelJob: true);
                        return false;
                    }
                }
                if (!approach.started && RimKataFlyingKick.Distance(pawn.Position, approach.target.Position) <= 2
                    && pawn.stances?.curStance is not Stance_RimKataFlyingKickWait)
                {
                    // A cancelled wait or a failed takeoff returns to the original melee order.
                    EndMeleeApproach(pawn, cancelJob: false);
                    return false;
                }
                return true;
            }
            if (RimKataFlyingKick.Current(pawn) is RimKataFlyingKickState active
                && active.phase != RimKataFlyingKickPhase.Abort) return true;
            // Undrafted automatic pursuit keeps the original job/path and its original takeoff gates.
            Job current = pawn.CurJob;
            return !pawn.Drafted && current != null && current.targetA.Pawn != null
                && (current.def == JobDefOf.AttackMelee || current.def == RimKataDefOf.RimKata_Attack);
        }

        private static bool HasKickApproach(Pawn pawn, Pawn target, bool airborne = false)
        {
            int distance = RimKataFlyingKick.Distance(pawn.Position, target.Position);
            if (distance <= 2)
                return (distance == 2 || airborne && distance == 1)
                    && RimKataFlyingKick.StraightPath(pawn, target.Position);
            Pawn_PathFollower pather = pawn.pather;
            if (pather?.curPath?.Found == true && pather.Destination.Thing == target
                && pather.lastPathedTargetPosition == target.Position)
                return HasStraightTail(pather.curPath, target.Position);
            // A new target cell is the only reason to re-evaluate a pending approach route.
            using (PawnPath path = pawn.Map.pathFinder.FindPathNow(pawn.Position, target, pawn, peMode: PathEndMode.Touch))
                return path.Found && HasStraightTail(path, target.Position);
        }

        private static bool HasStraightTail(PawnPath path, IntVec3 target)
        {
            List<IntVec3> nodes = path.NodesReversed;
            int first = nodes.Count != 0 && nodes[0] == target ? 1 : 0;
            if (nodes.Count < first + 3) return false;
            IntVec3 delta = target - nodes[first];
            var step = new IntVec3(Math.Sign(delta.x), 0, Math.Sign(delta.z));
            return step != IntVec3.Zero && delta == step
                && nodes[first + 1] == target - step * 2
                && nodes[first + 2] == target - step * 3;
        }

        internal static void FlyingKickStarted(Pawn pawn, Thing target)
        {
            if (!IsControlled(pawn)) return;
            if (meleeApproach?.pawn == pawn && meleeApproach.job == pawn.CurJob
                && meleeApproach.target == target) meleeApproach.started = true;
        }

        internal static void FlyingKickFinished(Pawn pawn)
        {
            if (!IsControlled(pawn)) return;
            if (meleeApproach?.pawn == pawn) meleeApproach = null;
            SyncAvatarPosition(pawn);
        }

        private static void EndMeleeApproach(Pawn pawn, bool cancelJob)
        {
            Job ownedJob = meleeApproach?.pawn == pawn ? meleeApproach.job : null;
            meleeApproach = null;
            RimKataFlyingKick.CancelControlled(pawn);
            if (cancelJob && ownedJob != null && pawn.CurJob == ownedJob)
            {
                pawn.pather?.StopDead();
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
            SyncAvatarPosition(pawn);
        }

        internal static void MotionInput(Pawn pawn)
        {
            if (!IsControlled(pawn)) return;
            var breach = RimKataBreachUtility.Get(pawn);
            if (breach != null)
            {
                if (pawn.jobs?.curDriver is JobDriver_RimKataBreach driver) driver.Cancel();
                else { breach.Drop(); RimKataBreachUtility.Remove(pawn); }
                SyncAvatarPosition(pawn);
            }
            if (meleeApproach?.pawn == pawn) EndMeleeApproach(pawn, cancelJob: true);
            else if (RimKataFlyingKick.Current(pawn) != null)
            {
                RimKataFlyingKick.CancelControlled(pawn);
                SyncAvatarPosition(pawn);
            }
        }
    }
}

