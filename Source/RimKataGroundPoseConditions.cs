using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataGroundPoseConditions
    {
        internal static bool HasMovementJob(Pawn pawn)
            => pawn.pather?.Moving == true || pawn.CurJobDef == JobDefOf.Goto;

        internal static bool HasAttackableOpponent(Pawn pawn, RimKataPawnCombatState state)
            => TryGetAttackableOpponent(pawn, state, out _);

        internal static bool TryGetAttackableOpponent(
            Pawn pawn, RimKataPawnCombatState state, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            if (pawn?.Spawned != true || state?.pawn != pawn) return false;
            // Reuse known combat targets only. A visual pose must not start a
            // second map search or alter the combat controller's candidates.
            if (TryGetAttackableCycleTarget(pawn, state.primaryWeaponCycle, out target)
                || TryGetAttackableCycleTarget(pawn, state.secondaryWeaponCycle, out target)) return true;
            if (pawn.stances?.curStance is Stance_Busy busy
                && CanAttackKnownTarget(pawn, busy.focusTarg.Thing))
            {
                target = busy.focusTarg;
                return true;
            }
            Job job = pawn.CurJob;
            if ((job?.def == JobDefOf.AttackStatic || job?.def == JobDefOf.AttackMelee
                    || job?.def == RimKataDefOf.RimKata_Attack
                    || pawn.jobs?.curDriver is JobDriver_Hunt)
                && CanAttackKnownTarget(pawn, job.targetA.Thing))
            {
                target = job.targetA;
                return true;
            }
            return false;
        }

        private static bool TryGetAttackableCycleTarget(
            Pawn pawn, RimKataWeaponCycleState cycle, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            if (!HasEquippedWeapon(pawn, cycle)) return false;
            Thing known = CanAttackKnownTarget(pawn, cycle.focusedTarget, cycle.focusedTargetFromAttackGizmo)
                ? cycle.focusedTarget
                : CanAttackKnownTarget(pawn, cycle.plannedTarget) ? cycle.plannedTarget
                : CanAttackKnownTarget(pawn, cycle.cachedCandidateTarget) ? cycle.cachedCandidateTarget : null;
            if (known != null)
            {
                target = known;
                return true;
            }
            if (cycle.automaticCandidates != null)
                for (int i = 0; i < cycle.automaticCandidates.Count; i++)
                    if (CanAttackKnownTarget(pawn, cycle.automaticCandidates[i]))
                    {
                        target = cycle.automaticCandidates[i];
                        return true;
                    }
            return false;
        }

        private static bool CanAttackKnownTarget(Pawn pawn, Thing target, bool explicitFocus = false)
        {
            Job job = pawn.CurJob;
            bool forced = explicitFocus || (job != null && job.targetA.Thing == target
                && (job.playerForced || pawn.jobs?.curDriver is JobDriver_Hunt));
            return target != null && IsLiveTarget(pawn, target)
                && (forced || RimKataTargeting.IsAutomaticEnemy(pawn, target))
                && RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(pawn, target);
        }

        internal static float HeadAimAngle(Pawn pawn)
        {
            LocalTargetInfo target = LocalTargetInfo.Invalid;
            if (pawn.stances?.curStance is Stance_Busy busy && !busy.neverAimWeapon)
                target = busy.focusTarg;
            if (!target.IsValid && RimKataDualWeaponController.TryGetNextAim(pawn, out _, out var next))
                target = next;
            if (!target.IsValid && (pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                || pawn.CurJobDef == JobDefOf.AttackStatic || pawn.jobs?.curDriver is JobDriver_Hunt))
                target = pawn.CurJob.targetA;
            if (!target.IsValid) return pawn.Rotation.AsAngle;
            Vector3 direction = target.CenterVector3 - pawn.DrawPos;
            return direction.x * direction.x + direction.z * direction.z > 0.0001f
                ? Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg : pawn.Rotation.AsAngle;
        }

        internal static bool CanEnterProne(Pawn pawn, Verb verb, LocalTargetInfo target)
        {
            // The combat event already chose the verb and target. Do not poll
            // stances or weapon cycles to rediscover an aiming intent here.
            if (pawn?.Spawned != true
                || pawn.Dead
                || pawn.Downed
                || pawn.GetPosture() != PawnPosture.Standing
                || HasMovementJob(pawn)
                || pawn.stances?.stunner?.Stunned == true
                || RimKataTargetAccess.SettingsFor(pawn)?.proneFireEnabled != true
                || !RimKataEligibility.CanOperateCombatWeapon(pawn)
                || !IsHeldRangedVerb(pawn, verb)
                || !IsLiveTarget(pawn, target))
            {
                return false;
            }

            float distanceSquared = pawn.Position.DistanceToSquared(target.Cell);
            float candidateRadius = RimKataTargeting.MaximumAutomaticCandidateCellRadius(pawn);
            float weaponRange = RimKataRangeUtility.ResolveEffectiveRange(
                pawn, verb.EquipmentSource as ThingWithComps, verb);
            if (distanceSquared <= candidateRadius * candidateRadius
                && distanceSquared <= weaponRange * weaponRange)
            {
                return false;
            }

            // Sample only the adjacent cell toward the existing aim. The angle
            // selects the nearest of eight directions, not every nearby cover.
            Vector3 direction = (target.Cell - pawn.Position).ToVector3();
            int octant = Mathf.RoundToInt(Mathf.Atan2(direction.x, direction.z)
                * Mathf.Rad2Deg / 45f);
            float angle = octant * 45f * Mathf.Deg2Rad;
            IntVec3 frontCell = pawn.Position + new IntVec3(
                Mathf.RoundToInt(Mathf.Sin(angle)),
                0,
                Mathf.RoundToInt(Mathf.Cos(angle)));
            Thing cover = frontCell.InBounds(pawn.Map)
                ? frontCell.GetCover(pawn.Map)
                : null;
            if (cover != null && cover.BaseBlockChance() > 0.2f)
            {
                return false;
            }

            return true;
        }

        internal static bool HasSingleCloseOpponent(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing attacker)
        {
            if (pawn?.Spawned != true
                || state?.pawn != pawn
                || (!state.dualCloseCombatActive && !state.CloseCombatActive))
            {
                return false;
            }

            bool hasPrimary = HasEquippedWeapon(pawn, state.primaryWeaponCycle);
            bool hasSecondary = HasEquippedWeapon(pawn, state.secondaryWeaponCycle);
            if (!hasPrimary && !hasSecondary) return false;

            bool allowFixedTarget = RimKataTargetAccess.SettingsFor(pawn)?.randomAttackEnabled == false;
            Thing target = null;
            if (hasPrimary
                && !TryGetSingleCandidate(state.primaryWeaponCycle, allowFixedTarget, out target))
                return false;

            if (hasSecondary)
            {
                if (!TryGetSingleCandidate(state.secondaryWeaponCycle, allowFixedTarget, out Thing secondaryTarget)
                    || (target != null && target != secondaryTarget))
                    return false;
                target = secondaryTarget;
            }

            return IsLiveTarget(pawn, target)
                && pawn.CanReachImmediate(target, PathEndMode.Touch);
        }

        private static bool TryGetSingleCandidate(
            RimKataWeaponCycleState cycle,
            bool allowFixedTarget,
            out Thing target)
        {
            target = null;
            int count = cycle.automaticCandidates?.Count ?? 0;
            if (count != 0)
            {
                if (count != 1)
                {
                    return false;
                }

                target = cycle.automaticCandidates[0];
                return target != null;
            }

            // Fixed-target cycles can operate without a random-candidate list.
            // Conflicting retained targets are not treated as a single opponent.
            if (!allowFixedTarget
                || (cycle.plannedTarget != null && cycle.focusedTarget != null
                    && cycle.plannedTarget != cycle.focusedTarget))
            {
                return false;
            }

            target = cycle.plannedTarget ?? cycle.focusedTarget;
            return target != null;
        }

        private static bool HasEquippedWeapon(Pawn pawn, RimKataWeaponCycleState cycle)
        {
            return cycle?.weapon != null
                && !cycle.weapon.Destroyed
                && pawn.equipment?.AllEquipmentListForReading.Contains(cycle.weapon) == true;
        }

        private static bool IsHeldRangedVerb(Pawn pawn, Verb verb)
        {
            ThingWithComps weapon = verb?.EquipmentSource as ThingWithComps;
            return verb?.IsMeleeAttack == false
                && weapon != null
                && !weapon.Destroyed
                && pawn.equipment?.AllEquipmentListForReading.Contains(weapon) == true
                && (weapon == pawn.equipment.Primary
                    || weapon == RimKataWeaponSlotUtility.SecondaryWeapon(pawn));
        }

        private static bool IsLiveTarget(Pawn pawn, LocalTargetInfo target)
        {
            if (!target.IsValid || !target.Cell.InBounds(pawn.Map))
            {
                return false;
            }

            if (!target.HasThing)
            {
                return true;
            }

            Thing thing = target.Thing;
            return thing != pawn
                && !(thing is Projectile)
                && !thing.Destroyed
                && thing.Spawned
                && thing.Map == pawn.Map
                && (!(thing is Pawn targetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(
                        targetPawn,
                        pawn.CurJob?.killIncappedTarget == true
                            && pawn.CurJob.targetA.Thing == thing));
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    internal static class Patch_PawnJobTracker_StartJob_RimKataGroundPose
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pawn ___pawn)
        {
            RimKataGroundPoseUtility.NotifyJobChanged(___pawn);
            if (___pawn?.CurJobDef == JobDefOf.AttackStatic)
            {
                RimKataGroundPoseUtility.NotifyAttackJob(___pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    internal static class Patch_PawnPathFollower_StartPath_RimKataGroundPose
    {
        private static void Postfix(Pawn ___pawn)
            => RimKataGroundPoseUtility.NotifyMovement(___pawn);
    }
}
