using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataReactiveMovement
    {
        [ThreadStatic] internal static Pawn entering;
        private static readonly AccessTools.FieldRef<PawnPath, int> Index = AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");
        private static readonly AccessTools.FieldRef<PawnPath, bool> InUse = AccessTools.FieldRefAccess<PawnPath, bool>("inUse");
        private static readonly Action<Pawn_PathFollower> Setup = AccessTools.MethodDelegate<Action<Pawn_PathFollower>>(
            AccessTools.Method(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell"));
        private static readonly Action<Pawn_PathFollower> EnterCell = AccessTools.MethodDelegate<Action<Pawn_PathFollower>>(
            AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell"));

        internal static void RefreshNearbyPursuers(Pawn pawn, IntVec3 origin)
        {
            Map map = pawn.Map;
            if (map == null || origin == pawn.Position) return;
            foreach (IntVec3 offset in GenAdj.AdjacentCellsAndInside)
            {
                IntVec3 cell = origin + offset;
                if (!cell.InBounds(map)) continue;
                var things = cell.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (!(things[i] is Pawn pursuer) || pursuer == pawn || pursuer.Dead) continue;
                    Pawn_PathFollower pather = pursuer.pather;
                    if (pather?.Moving != true || pather.Destination.Thing != pawn
                        || pather.lastPathedTargetPosition == pawn.Position
                        || !RimKataTargeting.IsAutomaticEnemy(pawn, pursuer)
                        || !ReachabilityImmediate.CanReachImmediate(
                            pursuer.Position, origin, map, PathEndMode.Touch, pursuer)) continue;
                    pather.ResetToCurrentPosition();
                }
            }
        }

        internal static void RetargetUnreachableAttack(Pawn pawn,
            RimKataPawnCombatState combat = null, Thing primaryTarget = null, Thing secondaryTarget = null)
        {
            Job job = pawn?.CurJob;
            if (pawn?.Map == null || job?.def != RimKataDefOf.RimKata_Attack
                || job.playerForced || pawn.InMentalState
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || RimKataTemporaryInactivity.IsInactive(pawn)
                || !(pawn.jobs.curDriver is JobDriver_RimKataAttack driver)
                || driver.IsStructureMelee
                || !RimKataEligibility.RandomAttackEnabledForPawn(pawn)) return;
            Thing target = job.targetA.Thing;
            if (target?.Spawned == true && target.Map == pawn.Map
                && pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return;
            pawn.pather?.StopDead();
            if (combat?.ownerComponent?.map != pawn.Map)
            {
                if (!RimKataCombatStatePresenceCache.TryGetOwner(pawn, out var owner)) return;
                combat = owner.GetState(pawn, false);
            }
            if (combat == null) return;
            if (!TryRetargetKnownCandidate(pawn, combat, combat.primaryWeaponCycle, driver, primaryTarget))
                TryRetargetKnownCandidate(pawn, combat, combat.secondaryWeaponCycle, driver, secondaryTarget);
        }

        private static bool TryRetargetKnownCandidate(Pawn pawn, RimKataPawnCombatState combat,
            RimKataWeaponCycleState cycle, JobDriver_RimKataAttack driver, Thing target)
        {
            if (cycle?.weapon == null) return false;
            if (!RimKataSharedTargetSearch.IsLiveRegisteredCandidate(pawn, target))
                target = cycle.plannedTarget ?? cycle.cachedCandidateTarget;
            if (target == null || target == pawn.CurJob.targetA.Thing) return false;
            Verb verb = cycle.boundVerb ?? RimKataWeaponSlotUtility.CombatVerb(pawn, cycle.weapon);
            if (verb == null) return false;
            return RimKataSharedTargetSearch.CanShootRegisteredCandidate(pawn, combat, cycle, verb, target)
                && driver.TryPromoteAutomaticJobTarget(target);
        }

        internal static bool Enter(Pawn pawn, IntVec3 destination)
        {
            // Native cell entry runs collision, grid, terrain and door callbacks.
            if (pawn.pather == null) return false;
            Job entryJob = pawn.CurJob;
            Pawn previous = entering;
            var path = new PawnPath();
            path.AddNode(destination);
            path.AddNode(pawn.Position);
            Index(path) = 1;
            InUse(path) = true;
            bool assigned = false;
            pawn.pather.StopDead();
            entering = pawn;
            try
            {
                pawn.pather.StartPath(destination, PathEndMode.OnCell);
                if (pawn.CurJob != entryJob || !pawn.pather.Moving) return false;
                pawn.pather.DisposeAndClearCurPathRequest();
                pawn.pather.DisposeAndClearCurPath();
                pawn.pather.curPath = path;
                assigned = true;
                pawn.pather.lastPathedTargetPosition = destination;
                pawn.pather.curPathJobIsStale = false;
                Setup(pawn.pather);
                if (pawn.CurJob != entryJob || pawn.pather.nextCell != destination) return false;
                pawn.pather.nextCellCostLeft = 0f;
                EnterCell(pawn.pather);
                return pawn.CurJob == entryJob && pawn.Position == destination;
            }
            finally
            {
                if (pawn.CurJob == entryJob) pawn.pather.StopDead();
                if (!assigned) path.Dispose();
                entering = previous;
                pawn.Drawer?.tweener?.ResetTweenedPosToRoot();
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "PatherArrived")]
    internal static class Patch_PathArrived_RimKataReactiveEntry
    {
        // Native arrival callbacks would otherwise complete the original Job.
        [HarmonyPriority(Priority.First + 100)]
        private static bool Prefix(Pawn ___pawn) => RimKataReactiveMovement.entering != ___pawn;
    }
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_JobStart_RimKataReactiveMotion
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            Label original = generator.DefineLabel();
            var gate = RimKataMotionJobGate.ParticipantBranch(generator, new[] {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_JobTracker), "pawn"))
            }, original, out LocalBuilder participant);
            foreach (var code in gate) yield return code;
            yield return new CodeInstruction(OpCodes.Ldloc, participant);
            yield return new CodeInstruction(OpCodes.Ldarg_1);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataMotionJobGate), nameof(RimKataMotionJobGate.JobChanging)));
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(original);
            foreach (var code in instructions) yield return code;
        }
    }
}
