using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public static class RimKataDodgeMovementUtility
    {
        private static readonly IntVec3[] EightDirections =
        {
            IntVec3.North,
            IntVec3.NorthEast,
            IntVec3.East,
            IntVec3.SouthEast,
            IntVec3.South,
            IntVec3.SouthWest,
            IntVec3.West,
            IntVec3.NorthWest
        };

        private static readonly FieldInfo PathEndModeField = AccessTools.Field(typeof(Pawn_PathFollower), "peMode");

        public static bool IsActive(Pawn pawn)
        {
            return pawn?.Map?.GetComponent<RimKataMapComponent>()?.IsDodgeMovementActive(pawn) == true;
        }

        internal static bool CalculateIsActive(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (pawn == null
                || state?.DodgeMovementActive != true
                || state.dodgeMovementJob != pawn.CurJob)
            {
                return false;
            }

            return pawn.Position == state.dodgeMovementDestination
                || (pawn.pather?.Destination.IsValid == true
                    && pawn.pather.Destination.Cell
                        == state.dodgeMovementDestination);
        }

        public static bool BlocksJob(Pawn pawn)
        {
            return pawn?.Map?.GetComponent<RimKataMapComponent>()?.IsDodgeMotionBlocking(pawn) == true;
        }

        internal static void GetStatus(
            Pawn pawn,
            out bool blocksJob,
            out bool isActive)
        {
            RimKataMapComponent component =
                pawn?.Map?.GetComponent<RimKataMapComponent>();
            if (component == null)
            {
                blocksJob = false;
                isActive = false;
                return;
            }

            component.GetDodgeMovementStatus(
                pawn,
                out blocksJob,
                out isActive);
        }

        public static bool IsVisualLocked(Pawn pawn)
        {
            return pawn?.Map?.GetComponent<RimKataMapComponent>()?.IsDodgeVisualLocked(pawn) == true;
        }

        public static bool ShouldBlockPhysicalMeleeVerb(Verb_MeleeAttack meleeVerb)
        {
            Pawn pawn = meleeVerb?.CasterPawn;
            if (pawn == null
                || RimKataTargetAccess.SettingsFor(pawn)?.closeFireEnabled == false
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return false;
            }

            ThingWithComps sourceWeapon = meleeVerb.EquipmentSource as ThingWithComps;
            if (sourceWeapon != null)
            {
                if (!RimKataEquipmentUtility.IsWeaponEnabled(sourceWeapon.def))
                {
                    return false;
                }

                Verb combatVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, sourceWeapon);
                return combatVerb != null && !combatVerb.IsMeleeAttack;
            }

            ThingWithComps primary =
                RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            Verb primaryCombatVerb =
                RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            if (RimKataEquipmentUtility.IsWeaponEnabled(primary?.def)
                && primaryCombatVerb != null
                && !primaryCombatVerb.IsMeleeAttack)
            {
                return true;
            }

            ThingWithComps secondary =
                RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                    ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                    : null;
            Verb secondaryCombatVerb =
                RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            return RimKataEquipmentUtility.IsWeaponEnabled(secondary?.def)
                && secondaryCombatVerb != null
                && !secondaryCombatVerb.IsMeleeAttack;
        }

        private static bool HasPhysicalMovementIntent(Pawn pawn)
        {
            Job job = pawn?.CurJob;
            JobDef jobDef = job?.def;
            return jobDef == JobDefOf.Goto
                || jobDef == RimKataDefOf.RimKata_Attack
                || IsUnarmedAutomaticMeleeApproach(pawn, job);
        }

        private static bool IsUnarmedAutomaticMeleeApproach(Pawn pawn, Job job)
        {
            // Natural melee attackers have no equipment-backed weapon cycle,
            // but their legitimate attack path can still own a physical dodge.
            // Keep nonhostile and player-forced AttackMelee Jobs outside this
            // exception so it cannot mask a stale automatic friendly response.
            return pawn != null
                && job?.def == JobDefOf.AttackMelee
                && !job.playerForced
                && RimKataWeaponSlotUtility.PrimaryWeapon(pawn) == null
                && job.targetA.HasThing
                && RimKataTargeting.IsAutomaticEnemy(
                    pawn,
                    job.targetA.Thing);
        }

        internal static void ApplySuccessfulRangedDodge(
            Pawn pawn,
            Thing attacker,
            Projectile projectile,
            RimKataMapComponent component,
            int dodgeWindowDurationTicks)
        {
            component?.BeginRangedDodgeWindow(
                pawn,
                dodgeWindowDurationTicks);

            ApplyRangedDodgeMotion(
                pawn,
                attacker,
                projectile,
                component,
                dodgeWindowDurationTicks);
        }

        private static void ApplyRangedDodgeMotion(
            Pawn pawn,
            Thing attacker,
            Projectile projectile,
            RimKataMapComponent component,
            int dodgeWindowDurationTicks)
        {
            if (component?.IsDodgeMovementActive(pawn) == true)
            {
                TryGetCurrentMovementDirection(
                    pawn,
                    out IntVec3 existingDodgeDirection);
                BeginStandardDodge(
                    pawn,
                    attacker,
                    component,
                    existingDodgeDirection,
                    dodgeWindowDurationTicks);
                return;
            }

            if (RimKataEligibility.IsWorkMovementDefenseException(pawn))
            {
                IntVec3 workDirection = MovementDirection(pawn);
                BeginStandardDodge(
                    pawn,
                    attacker,
                    component,
                    workDirection,
                    dodgeWindowDurationTicks);
                return;
            }

            bool hasActiveMovementPath = TryGetCurrentMovementDirection(
                pawn,
                out IntVec3 movementDirection);

            if (!hasActiveMovementPath
                && component?.IsCloseCombatActive(pawn) == true)
            {
                component.BeginCloseCombatDodge(
                    pawn,
                    dodgeWindowDurationTicks);
                return;
            }

            if (hasActiveMovementPath
                && !RimKataDualWeaponController
                    .PendingFollowupCanReplaceCurrentNonForcedGoto(pawn)
                && TryChooseDestination(
                    pawn,
                    movementDirection,
                    out IntVec3 destination)
                && TryStartInternal(
                    pawn,
                    projectile,
                    component,
                    destination,
                    movementDirection,
                    dodgeWindowDurationTicks))
            {
                return;
            }

            BeginStandardDodge(
                pawn,
                attacker,
                component,
                movementDirection,
                dodgeWindowDurationTicks);
        }

        private static void BeginStandardDodge(
            Pawn pawn,
            Thing attacker,
            RimKataMapComponent component,
            IntVec3 movementDirection,
            int durationTicks)
        {
            component?.BeginOrRestartStandardDodgeVisual(
                pawn,
                durationTicks,
                DodgeDirection(pawn, attacker, movementDirection));
        }

        private static IntVec3 DodgeDirection(
            Pawn pawn,
            Thing attacker,
            IntVec3 movementDirection)
        {
            Vector3 line;
            if (pawn?.pather?.MovingNow == true)
            {
                IntVec3 forward = movementDirection.IsValid
                    ? movementDirection
                    : MovementDirection(pawn);
                line = forward.ToVector3();
            }
            else if (attacker != null)
            {
                line = (pawn.Position - attacker.Position).ToVector3();
            }
            else
            {
                line = Vector3.forward;
            }

            if (line.sqrMagnitude < 0.01f)
            {
                line = Vector3.forward;
            }

            Vector3 side = new Vector3(-line.z, 0f, line.x).normalized
                * (Rand.Bool ? 1f : -1f);
            IntVec3 result = new IntVec3(
                Mathf.RoundToInt(side.x),
                0,
                Mathf.RoundToInt(side.z));
            return result == IntVec3.Zero ? IntVec3.East : result;
        }

        private static IntVec3 FacingCell(Rot4 rotation)
        {
            switch (rotation.AsInt)
            {
                case 0: return IntVec3.North;
                case 1: return IntVec3.East;
                case 2: return IntVec3.South;
                case 3: return IntVec3.West;
                default: return IntVec3.North;
            }
        }

        private static IntVec3 MovementDirection(Pawn pawn)
        {
            if (pawn?.pather?.MovingNow == true
                && pawn.pather.nextCell.IsValid
                && pawn.pather.nextCell != pawn.Position)
            {
                IntVec3 direction = pawn.pather.nextCell - pawn.Position;
                return new IntVec3(
                    Math.Sign(direction.x),
                    0,
                    Math.Sign(direction.z));
            }

            return FacingCell(pawn?.Rotation ?? Rot4.North);
        }

        public static bool TryGetCurrentMovementDirection(Pawn pawn, out IntVec3 direction)
        {
            direction = IntVec3.Invalid;
            if (!HasPhysicalMovementIntent(pawn))
            {
                return false;
            }

            Pawn_PathFollower pather = pawn?.pather;
            if (pather?.Moving != true)
            {
                return false;
            }

            if (!pather.nextCell.IsValid || pather.nextCell == pawn.Position)
            {
                return false;
            }

            IntVec3 delta = pather.nextCell - pawn.Position;
            direction = QuantizeDirection(delta.ToVector3());
            return true;
        }

        public static void AdjacentDirections(IntVec3 combatDirection, out IntVec3 first, out IntVec3 second)
        {
            int sector = DirectionIndex(combatDirection);
            first = EightDirections[(sector + 7) & 7];
            second = EightDirections[(sector + 1) & 7];
        }

        public static bool TryChooseDestination(Pawn pawn, IntVec3 combatDirection, out IntVec3 destination)
        {
            destination = IntVec3.Invalid;
            AdjacentDirections(combatDirection, out IntVec3 left, out IntVec3 right);
            IntVec3 first = Rand.Bool ? left : right;
            IntVec3 second = first == left ? right : left;
            IntVec3 firstCell = pawn.Position + first;
            IntVec3 secondCell = pawn.Position + second;
            if (CanEnterDodgeCell(pawn, firstCell))
            {
                destination = firstCell;
                return true;
            }

            if (CanEnterDodgeCell(pawn, secondCell))
            {
                destination = secondCell;
                return true;
            }

            return false;
        }

        private static bool TryStartInternal(
            Pawn pawn,
            Projectile projectile,
            RimKataMapComponent component,
            IntVec3 destination,
            IntVec3 combatDirection,
            int dodgeWindowDurationTicks)
        {
            if (pawn?.Map == null || component == null)
            {
                return false;
            }

            Pawn_PathFollower pather = pawn.pather;
            bool resumeWasMoving = pather?.Moving == true && pather.Destination.IsValid;
            LocalTargetInfo resumeDestination = resumeWasMoving
                ? pather.Destination
                : LocalTargetInfo.Invalid;
            PathEndMode resumeMode = resumeWasMoving && PathEndModeField != null
                ? (PathEndMode)PathEndModeField.GetValue(pather)
                : (resumeDestination.HasThing ? PathEndMode.Touch : PathEndMode.OnCell);
            if (component.IsDodgeMovementStartBlocked(pawn))
            {
                return false;
            }

            if (pather.Moving)
            {
                pather.StopDead();
            }

            Job movementJob = pawn.CurJob;
            GetFailureStagger(
                pawn,
                projectile,
                out int failureStaggerTicks,
                out float failureStaggerSpeedFactor);
            bool started = component.BeginDodgeMovement(
                pawn,
                destination,
                combatDirection,
                resumeWasMoving,
                resumeDestination,
                resumeMode,
                movementJob,
                failureStaggerTicks,
                failureStaggerSpeedFactor,
                dodgeWindowDurationTicks);
            if (started)
            {
                return true;
            }

            component.CancelFailedDodgeMovementStart(pawn);
            if (resumeWasMoving
                && pawn.CurJob == movementJob
                && resumeDestination.IsValid
                && (!resumeDestination.HasThing || !resumeDestination.ThingDestroyed)
                && pawn.Spawned)
            {
                pather.StartPath(resumeDestination, resumeMode);
            }

            return false;
        }

        public static bool TryFinish(Pawn pawn, bool failed)
        {
            return pawn?.Map?.GetComponent<RimKataMapComponent>()?.TryFinishDodgeMovement(pawn, failed) == true;
        }

        private static IntVec3 QuantizeDirection(Vector3 vector)
        {
            if (vector.sqrMagnitude < 0.001f)
            {
                return IntVec3.North;
            }

            float clockwiseFromNorth = Mathf.Repeat(Mathf.Atan2(vector.x, vector.z) * Mathf.Rad2Deg, 360f);
            int sector = Mathf.RoundToInt(clockwiseFromNorth / 45f) & 7;
            return EightDirections[sector];
        }

        private static int DirectionIndex(IntVec3 direction)
        {
            for (int i = 0; i < EightDirections.Length; i++)
            {
                if (EightDirections[i] == direction)
                {
                    return i;
                }
            }

            return DirectionIndex(QuantizeDirection(direction.ToVector3()));
        }

        private static void GetFailureStagger(
            Pawn pawn,
            Projectile projectile,
            out int ticks,
            out float moveSpeedFactor)
        {
            ticks = 0;
            moveSpeedFactor = StaggerHandler.DefaultStaggerMoveSpeedFactor;
            if (!(projectile is Bullet bullet)
                || (!pawn.RaceProps.bulletStaggerIgnoreBodySize
                    && pawn.BodySize > bullet.stoppingPower + 0.001f))
            {
                return;
            }

            ticks = pawn.RaceProps.bulletStaggerDelayTicks ?? 95;
            moveSpeedFactor = pawn.RaceProps.bulletStaggerSpeedFactor
                ?? StaggerHandler.DefaultStaggerMoveSpeedFactor;
        }

        private static bool CanEnterDodgeCell(Pawn pawn, IntVec3 cell)
        {
            if (pawn?.Map == null
                || !cell.InBounds(pawn.Map)
                || !cell.WalkableBy(pawn.Map, pawn)
                || !pawn.CanReachImmediate(cell, PathEndMode.Touch))
            {
                return false;
            }

            Building_Door door = cell.GetDoor(pawn.Map);
            if (door != null && (!door.Open || door.TicksTillFullyOpened > 0))
            {
                return false;
            }

            Pawn occupyingPawn = cell.GetFirstPawn(pawn.Map);
            return occupyingPawn == null || occupyingPawn == pawn;
        }

    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "PatherArrived")]
    public static class Patch_PawnPathFollower_RimKataDodgeArrived
    {
        public static bool Prefix(Pawn ___pawn)
        {
            return !RimKataDodgeMovementUtility.TryFinish(___pawn, false);
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "PatherFailed")]
    public static class Patch_PawnPathFollower_RimKataDodgeFailed
    {
        public static bool Prefix(Pawn ___pawn)
        {
            return !RimKataDodgeMovementUtility.TryFinish(___pawn, true);
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.DriverTick))]
    public static class Patch_JobDriver_RimKataDodgeMovement
    {
        public static bool Prefix(JobDriver __instance, Pawn ___pawn)
        {
            // Negative-only fast gate. Active dodge movement is published to
            // the body-visual participant cache before its path starts; a hit
            // still requires the exact movement-state check below.
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(___pawn)
                || !RimKataResponseVisualParticipantCache
                    .IsBodyVisualParticipant(___pawn))
            {
                return true;
            }

            RimKataDodgeMovementUtility.GetStatus(
                ___pawn,
                out bool blocksJob,
                out bool isActive);
            if (!blocksJob)
            {
                return true;
            }

            return isActive
                && __instance is JobDriver_RimKataAttack;
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick))]
    public static class Patch_PawnPathFollower_RimKataDodgeMovement
    {
        private static readonly MethodInfo FullBodyBusyGetter = AccessTools.PropertyGetter(
            typeof(Pawn_StanceTracker),
            nameof(Pawn_StanceTracker.FullBodyBusy));
        private static readonly FieldInfo PawnField = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");
        private static readonly MethodInfo GateMethod = AccessTools.Method(
            typeof(Patch_PawnPathFollower_RimKataDodgeMovement),
            nameof(GateFullBodyBusy));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            bool patched = false;
            for (int i = 0; i < codes.Count; i++)
            {
                yield return codes[i];
                if (!patched && codes[i].Calls(FullBodyBusyGetter))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldfld, PawnField);
                    yield return new CodeInstruction(OpCodes.Call, GateMethod);
                    patched = true;
                }
            }

            if (!patched)
            {
                Log.Error("[RimKata] Could not place the dodge movement stance gate.");
            }
        }

        public static bool GateFullBodyBusy(bool fullBodyBusy, Pawn pawn)
        {
            // Body-visual participation is only a negative fast gate. The
            // exact dodge owner, Job and path are still verified by IsActive.
            return fullBodyBusy
                && (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    || !RimKataResponseVisualParticipantCache
                        .IsBodyVisualParticipant(pawn)
                    || !RimKataDodgeMovementUtility.IsActive(pawn));
        }
    }
}
