using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // Keep ordinary cell entry, reservations, terrain notifications and draw
    // interpolation. Only the accepted straight corridor replaces path finding.
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
            // Ordinary door/path checks already know the current driver. Do not
            // query the participant registry unless this pawn owns a breach Job.
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
            // The map bounds cap this once-per-start walk even with int.MaxValue
            // duration. Future changes are checked only on this participant.
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
            if (state.broken) driver.BeginRise(); else driver.Cancel();
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
        // GameComponent.FinalizeInit rebuilds the straight path after maps and
        // jobs have loaded. Ordinary pathfinding would be mistaken for a detour.
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
        private static void Postfix(Pawn ___pawn, ref Building __result)
        {
            if (!(__result is Building_Door door)) return;
            RimKataBreachState state = RimKataBreachMovement.DoorState(___pawn, door);
            if (state != null && (state.phase == BreachPhase.Run || state.phase == BreachPhase.Slide))
                __result = null;
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
            // The prefix already rejected ordinary pawns. Reuse the participant
            // unless a callback replaced its Job or changed registry membership.
            if (state == null || !(___pawn.jobs?.curDriver is JobDriver_RimKataBreach)
                || ___pawn.CurJob?.loadID != state.ownerJobId
                || state.phase != BreachPhase.Run && state.phase != BreachPhase.Slide
                || __state.version != RimKataBreachUtility.AttackStateVersion
                    && RimKataBreachUtility.Get(___pawn) != state) return;
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
