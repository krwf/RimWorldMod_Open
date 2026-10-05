using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Reflection.Emit;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataBreachMovement
    {
        internal struct CellEntryScope
        {
            internal RimKataBreachState state;
            internal int version;
        }

        private static readonly AccessTools.FieldRef<PawnPath, int> Index =
            AccessTools.FieldRefAccess<PawnPath, int>("curNodeIndex");
        private static readonly AccessTools.FieldRef<PawnPath, bool> InUse =
            AccessTools.FieldRefAccess<PawnPath, bool>("inUse");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, float> MovePercent =
            AccessTools.FieldRefAccess<Pawn_PathFollower, float>("cachedMovePercentage");
        private static readonly System.Action<Pawn_PathFollower> Setup =
            AccessTools.MethodDelegate<System.Action<Pawn_PathFollower>>(
                AccessTools.Method(typeof(Pawn_PathFollower), "SetupMoveIntoNextCell"));
        private static readonly System.Action<Pawn_PathFollower, Building> BashBlocker =
            AccessTools.MethodDelegate<System.Action<Pawn_PathFollower, Building>>(
                AccessTools.Method(typeof(Pawn_PathFollower), "MakeBashBlockerJob"));
        [System.ThreadStatic] internal static bool installing;

        internal static RimKataBreachState StraightState(Pawn pawn)
        {
            if (!(pawn?.jobs?.curDriver is JobDriver_RimKataBreach)) return null;
            var state = RimKataBreachUtility.Get(pawn);
            return state != null && (state.phase == BreachPhase.Run || state.phase == BreachPhase.Slide)
                ? state : null;
        }

        internal static RimKataBreachState DoorState(Pawn pawn, Building_Door door)
        {
            if (!(pawn?.jobs?.curDriver is JobDriver_RimKataBreach)) return null;
            RimKataBreachState state = RimKataBreachUtility.Get(pawn);
            return state != null && !state.broken && state.target == door
                && pawn.CurJob?.loadID == state.ownerJobId ? state : null;
        }

        internal static void StartStraight(Pawn pawn, RimKataBreachState state, bool resume = false)
        {
            IntVec3 savedNext = pawn.pather.nextCell;
            float savedLeft = pawn.pather.nextCellCostLeft, savedTotal = pawn.pather.nextCellCostTotal;
            IntVec3 end = pawn.Position;
            for (IntVec3 cell = end + state.direction; cell.InBounds(pawn.Map); cell += state.direction)
            {
                if (!RimKataBreachUtility.CanEnter(pawn, cell, state.target)) break;
                end = cell;
            }
            state.endpoint = end;
            if (end == pawn.Position) { Stop(pawn, state); return; }
            var path = new PawnPath();
            for (IntVec3 cell = end; ; cell -= state.direction)
            {
                path.AddNode(cell);
                if (cell == pawn.Position) break;
            }
            Index(path) = path.NodesReversed.Count - 1;
            InUse(path) = true;
            pawn.pather.StopDead();
            installing = true;
            try { pawn.pather.StartPath(end, PathEndMode.OnCell); }
            finally { installing = false; }
            if (RimKataBreachUtility.Get(pawn) != state || !pawn.pather.Moving)
            { path.Dispose(); return; }
            pawn.pather.DisposeAndClearCurPathRequest();
            pawn.pather.DisposeAndClearCurPath();
            pawn.pather.curPath = path;
            pawn.pather.lastPathedTargetPosition = end;
            pawn.pather.curPathJobIsStale = false;
            Setup(pawn.pather);
            if (resume && RimKataBreachUtility.Get(pawn) == state
                && pawn.pather.nextCell == savedNext && savedTotal > 0f)
            {
                pawn.pather.nextCellCostLeft = savedLeft;
                pawn.pather.nextCellCostTotal = savedTotal;
                MovePercent(pawn.pather) = Mathf.Clamp01(1f - savedLeft / savedTotal);
            }
        }

        internal static void Stop(Pawn pawn, RimKataBreachState state)
        {
            if (!(pawn.jobs?.curDriver is JobDriver_RimKataBreach driver)) return;
            if (state.broken) driver.BeginRise(movementStopped: true); else driver.Cancel();
        }

        internal static void RememberBlocker(RimKataBreachState state)
        {
            Pawn pawn = state.pawn;
            IntVec3 cell = pawn.pather.nextCell;
            if (cell == pawn.Position || !cell.AdjacentTo8WayOrInside(pawn.Position))
                cell = pawn.Position + state.direction;
            state.blockingTarget = BlockerAt(pawn, cell);
            state.blockingCell = state.blockingTarget != null ? cell : IntVec3.Invalid;
        }

        private static Thing BlockerAt(Pawn pawn, IntVec3 cell)
        {
            if (!cell.IsValid || !cell.InBounds(pawn.Map) || cell == pawn.Position
                || !cell.AdjacentTo8WayOrInside(pawn.Position)) return null;
            Building building = cell.GetEdifice(pawn.Map);
            if (building?.BlocksPawn(pawn) == true) return building;
            return PawnUtility.PawnBlockingPathAt(cell, pawn, true, false, false, false);
        }

        internal static void TryAttackBlocker(RimKataBreachState state)
        {
            Pawn pawn = state.pawn;
            if (state.phase != BreachPhase.Released || RimKataBreachUtility.Get(pawn) != state) return;
            Thing target = state.blockingTarget;
            IntVec3 cell = state.blockingCell;
            state.blockingTarget = null;
            state.blockingCell = IntVec3.Invalid;
            Job current = pawn.CurJob;
            if (!pawn.Spawned || pawn.Dead || pawn.Downed
                || state.player && !pawn.Drafted || pawn.InMentalState
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || RimKataTemporaryInactivity.IsInactive(pawn)
                || current == null || current.playerForced
                || current.def != JobDefOf.Wait_Combat && current.def != JobDefOf.Wait_MaintainPosture
                    && current.def != RimKataDefOf.RimKata_Attack) return;
            if (target == null)
            {
                RimKataReactiveMovement.RetargetUnreachableAttack(pawn);
                return;
            }
            if (target.Spawned != true || target.Map != pawn.Map
                || BlockerAt(pawn, cell) != target
                || !pawn.CanReachImmediate(target, PathEndMode.Touch)
                || pawn.TryGetAttackVerb(target, false, false) == null) return;
            if (target is Building building)
            {
                if (current.canBashDoors || pawn.HostileTo(building)) BashBlocker(pawn.pather, building);
            }
            else if (target is Pawn blocker && pawn.HostileTo(blocker) && pawn.CanAttackWhenPathingBlocked)
            {
                Job attack = JobMaker.MakeJob(JobDefOf.AttackMelee, blocker);
                attack.maxNumMeleeAttacks = 1;
                attack.expiryInterval = 300;
                pawn.jobs.StartJob(attack, JobCondition.InterruptForced, null, resumeCurJobAfterwards: false);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "CostToPayThisTick")]
    internal static class Patch_PawnPathFollower_RimKataBreachSpeed
    {
        private static void Postfix(Pawn ___pawn, IntVec3 ___nextCell, float ___nextCellCostTotal, ref float __result)
        {
            var state = RimKataBreachMovement.StraightState(___pawn);
            if (state == null) return;
            if (state.phase == BreachPhase.Slide || ___nextCell == state.doorCell && state.rate > 0f)
                __result = state.rate * ___nextCellCostTotal;
            else state.rate = Mathf.Clamp(__result / Mathf.Max(1f, ___nextCellCostTotal), 0.000001f, 1f);
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.TryResumePathingAfterLoading))]
    internal static class Patch_PawnPathFollower_RimKataBreachResume
    {
        // FinalizeInit restores the straight path after maps and jobs load; normal pathfinding would appear to be a detour.
        private static bool Prefix(Pawn ___pawn)
            => RimKataBreachMovement.StraightState(___pawn) == null;
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "SetNewPathRequest")]
    internal static class Patch_PawnPathFollower_RimKataBreachNoDetour
    {
        private static bool Prefix(Pawn ___pawn)
        {
            if (RimKataBreachMovement.installing) return true;
            var state = RimKataBreachMovement.StraightState(___pawn);
            if (state == null) return true;
            RimKataBreachMovement.Stop(___pawn, state);
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "NeedNewPath")]
    internal static class Patch_PawnPathFollower_RimKataBreachKeepPath
    {
        private static void Postfix(Pawn ___pawn, ref bool __result)
        {
            if (RimKataBreachMovement.StraightState(___pawn) != null) __result = false;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "BuildingBlockingNextPathCell")]
    internal static class Patch_PawnPathFollower_RimKataBreachDoorBlock
    {
        private static Building ResolveBreachDoor(Pawn pawn, Building blocker)
        {
            RimKataBreachState state = RimKataBreachMovement.DoorState(pawn, (Building_Door)blocker);
            return state != null && (state.phase == BreachPhase.Run || state.phase == BreachPhase.Slide)
                ? null : blocker;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            LocalBuilder result = generator.DeclareLocal(typeof(Building));
            LocalBuilder pawn = generator.DeclareLocal(typeof(Pawn));
            LocalBuilder jobs = generator.DeclareLocal(typeof(Pawn_JobTracker));
            Label completed = generator.DefineLabel(), done = generator.DefineLabel();
            foreach (CodeInstruction code in instructions)
            {
                if (code.opcode != OpCodes.Ret) { yield return code; continue; }
                code.opcode = OpCodes.Stloc;
                code.operand = result;
                yield return code;
                yield return new CodeInstruction(OpCodes.Br, completed);
            }
            yield return new CodeInstruction(OpCodes.Ldloc, result).WithLabels(completed);
            yield return new CodeInstruction(OpCodes.Isinst, typeof(Building_Door));
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_PathFollower), "pawn"));
            yield return new CodeInstruction(OpCodes.Stloc, pawn);
            yield return new CodeInstruction(OpCodes.Ldloc, pawn);
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldloc, pawn);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn), nameof(Pawn.jobs)));
            yield return new CodeInstruction(OpCodes.Stloc, jobs);
            yield return new CodeInstruction(OpCodes.Ldloc, jobs);
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldloc, jobs);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.curDriver)));
            yield return new CodeInstruction(OpCodes.Isinst, typeof(JobDriver_RimKataBreach));
            yield return new CodeInstruction(OpCodes.Brfalse, done);
            yield return new CodeInstruction(OpCodes.Ldloc, pawn);
            yield return new CodeInstruction(OpCodes.Ldloc, result);
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(
                typeof(Patch_PawnPathFollower_RimKataBreachDoorBlock), nameof(ResolveBreachDoor)));
            yield return new CodeInstruction(OpCodes.Stloc, result);
            yield return new CodeInstruction(OpCodes.Ldloc, result).WithLabels(done);
            yield return new CodeInstruction(OpCodes.Ret);
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "TryEnterNextPathCell")]
    internal static class Patch_PawnPathFollower_RimKataBreachEnter
    {
        private static bool Prefix(Pawn ___pawn, IntVec3 ___nextCell,
            out RimKataBreachMovement.CellEntryScope __state)
        {
            var state = RimKataBreachMovement.StraightState(___pawn);
            __state = default;
            if (state == null) return true;
            __state = new RimKataBreachMovement.CellEntryScope
            {
                state = state, version = RimKataBreachUtility.AttackStateVersion
            };
            if (!RimKataBreachUtility.CanEnter(___pawn, ___nextCell, state.target))
            { RimKataBreachMovement.Stop(___pawn, state); return false; }
            if (!state.broken && ___nextCell == state.doorCell)
            {
                ((JobDriver_RimKataBreach)___pawn.jobs.curDriver).BreakDoor();
                return RimKataBreachUtility.Get(___pawn) == state && state.broken;
            }
            return true;
        }

        private static void Postfix(Pawn ___pawn, RimKataBreachMovement.CellEntryScope __state)
        {
            RimKataBreachState state = __state.state;
            if (state == null || !(___pawn.jobs?.curDriver is JobDriver_RimKataBreach)
                || ___pawn.CurJob?.loadID != state.ownerJobId
                || state.phase != BreachPhase.Run && state.phase != BreachPhase.Slide
                || __state.version != RimKataBreachUtility.AttackStateVersion
                    && RimKataBreachUtility.Get(___pawn) != state) return;
            if (state.phase == BreachPhase.Run && state.leap == null
                && ___pawn.Position == state.doorCell - state.direction * 2)
            {
                state.leap = RimKataBreachLeap.Start(state);
                RimKataBreachUtility.Publish(state);
            }
            if (state.broken && ___pawn.Position == state.doorCell)
            {
                Vector3 offset = ___pawn.Position.ToVector3Shifted() - state.doorOrigin;
                state.carried = offset.x * state.direction.x + offset.z * state.direction.z >= 0f;
                RimKataBreachUtility.Publish(state);
                if (state.slideTicks == 0) ((JobDriver_RimKataBreach)___pawn.jobs.curDriver).BeginRise();
            }
        }
    }

    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.Notify_PawnApproaching))]
    internal static class Patch_BuildingDoor_RimKataBreachApproach
    {
        private static bool Prefix(Building_Door __instance, Pawn p)
            => RimKataBreachMovement.DoorState(p, __instance) == null;
    }

    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.StartManualOpenBy))]
    internal static class Patch_BuildingDoor_RimKataBreachOpen
    {
        private static bool Prefix(Building_Door __instance, Pawn opener)
            => RimKataBreachMovement.DoorState(opener, __instance) == null;
    }

    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.PawnCanOpen))]
    internal static class Patch_BuildingDoor_RimKataBreachPermission
    {
        private static void Postfix(Building_Door __instance, Pawn p, ref bool __result)
        {
            if (__result && RimKataBreachMovement.DoorState(p, __instance) != null) __result = false;
        }
    }
}
