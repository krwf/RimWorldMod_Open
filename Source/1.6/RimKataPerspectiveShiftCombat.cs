using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static partial class RimKataPerspectiveShiftCompat
    {
        private static bool replaceSecondaryNext;
        private static bool manualFireActive, manualInputDrafted;
        private static bool manualFireBlocked = true;
        private static ThingWithComps manualPrimaryWeapon, manualSecondaryWeapon;
        private static Thing manualFireTarget;

        internal static bool IsDraftedControlled(Pawn pawn)
            => IsControlled(pawn) && pawn.Drafted;

        internal static bool IsManualTarget(Pawn pawn, ThingWithComps weapon, Thing target)
            => HasManualFireInput(pawn) && target != null && target == manualFireTarget
                && weapon != null && (weapon == manualPrimaryWeapon || weapon == manualSecondaryWeapon);

        internal static bool AllowsCycleTarget(Pawn pawn, RimKataWeaponCycleState cycle, Thing target)
        {
            if (!IsDraftedControlled(pawn)) return true;
            if (target == null) return cycle?.HasDirectionalFire == true;
            if (RimKataTargeting.IsProjectile(target) || cycle?.focusedTarget == target) return true;
            return HasExplicitCombatOrder(pawn) && pawn.CurJob.targetA.Thing == target;
        }

        internal static bool MayAutoFire(Pawn pawn)
            => IsControlled(pawn) && !OffenseStopped
                && (pawn.Drafted || !ManualMovement || HasFocus(pawn));
        internal static bool BlocksAutomaticAttack(Pawn pawn)
            => IsControlled(pawn) && (OffenseStopped || pawn.Drafted && manualFireBlocked
                || !pawn.Drafted && ManualMovement && !HasFocus(pawn)
                || MotionTarget(pawn) != null);
        internal static bool IsMovingNow(Pawn pawn)
            => pawn?.pather?.MovingNow == true || IsControlled(pawn) && ManualMovement;

        internal static bool IsCombatTarget(Pawn pawn, Thing target)
            => target is Pawn other && other != pawn && other.Spawned && !other.Dead
                && other.Map == pawn?.Map && RimKataTargeting.IsPawnTargetStateValid(other)
                && (RimKataTargeting.IsAutomaticEnemy(pawn, other) || IsHuntTarget(pawn, other));

        private static bool IsHuntTarget(Pawn pawn, Thing target)
            => pawn?.Drafted != true && target is Pawn prey && prey.RaceProps.Animal
                && prey.Map?.designationManager.DesignationOn(prey, DesignationDefOf.Hunt) != null;

        internal static bool HasFocus(Pawn pawn)
        {
            if (!IsControlled(pawn)
                || !RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner)) return false;
            RimKataPawnCombatState state = owner.GetState(pawn, false);
            return LiveFocus(pawn, state?.primaryWeaponCycle) || LiveFocus(pawn, state?.secondaryWeaponCycle);
        }

        internal static bool HasDirectionalOrder(Pawn pawn, RimKataPawnCombatState state)
            => IsControlled(pawn) && RimKataDirectionalFire.HasRegisteredTarget(state);

        internal static bool KeepsAim(Pawn pawn, RimKataPawnCombatState state)
            => MayAutoFire(pawn) && (LiveFocus(pawn, state?.primaryWeaponCycle)
                || LiveFocus(pawn, state?.secondaryWeaponCycle));

        internal static bool HasManualFireInput(Pawn pawn)
            => IsControlled(pawn) && pawn.Drafted && manualFireActive && !manualFireBlocked;

        internal static bool ExtendsManualFireToRange(Pawn pawn)
            => HasManualFireInput(pawn)
                && RimKataTargetAccess.SettingsFor(pawn)?.directionalFireEnabled == true;

        private static bool OwnsManualCycle(RimKataWeaponCycleState cycle)
            => cycle?.weapon != null
                && (cycle.weapon == manualPrimaryWeapon || cycle.weapon == manualSecondaryWeapon);

        internal static bool IsManualCloseTarget(Pawn pawn, RimKataWeaponCycleState cycle)
            => IsDraftedControlled(pawn) && !manualFireBlocked && !OffenseStopped
                && cycle?.focusedTargetFromAttackGizmo == true
                && cycle.focusedTarget?.Spawned == true && cycle.focusedTarget.Map == pawn.Map
                && pawn.CanReachImmediate(cycle.focusedTarget, PathEndMode.Touch);

        internal static void ResetCombatInput()
        {
            replaceSecondaryNext = false;
            ResetManualFireInput();
        }

        private static void ResetManualFireInput()
        {
            manualFireActive = manualInputDrafted = false;
            manualFireBlocked = true;
            manualPrimaryWeapon = manualSecondaryWeapon = null;
            manualFireTarget = null;
        }

        private static bool HasExplicitCombatOrder(Pawn pawn)
            => pawn.CurJob?.playerForced == true
                && (pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                    || pawn.CurJobDef == RimKataDefOf.RimKata_Breach
                    || pawn.CurJobDef == RimKataDefOf.RimKata_SubdueCombat
                    || pawn.CurJobDef == JobDefOf.AttackMelee
                    || pawn.CurJobDef == JobDefOf.AttackStatic);

        private static void UpdateManualFireInput(Pawn pawn, bool held)
        {
            if (manualInputDrafted != pawn.Drafted)
            {
                EndManualFire(pawn);
                manualInputDrafted = pawn.Drafted;
                manualFireBlocked = pawn.Drafted && !HasExplicitCombatOrder(pawn);
                if (manualFireBlocked) CancelCombatActions(pawn);
            }
            if (manualFireActive && (!held || !pawn.Drafted)) EndManualFire(pawn);
        }

        private static void EndManualFire(Pawn pawn)
        {
            if (!manualFireActive) return;
            manualFireActive = false;
            manualFireBlocked = true;
            CancelCombatActions(pawn, manualOnly: true);
            manualPrimaryWeapon = manualSecondaryWeapon = null;
            manualFireTarget = null;
        }

        internal static void ExposeCombatInput()
            => Scribe_Values.Look(ref replaceSecondaryNext, "rimKataReplaceSecondaryNext");

        internal static void StopCombat(Pawn pawn)
        {
            if (!IsControlled(pawn)) return;
            OffenseStopped = true;
            replaceSecondaryNext = false;
            CancelCombatActions(pawn);
        }

        internal static void CancelForMovement(Pawn pawn)
        {
            if (IsControlled(pawn)) CancelCombatActions(pawn);
        }

        private static void CancelCombatActions(Pawn pawn, bool manualOnly = false)
        {
            if (!RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner)) return;
            RimKataPawnCombatState state = owner.GetState(pawn, false);
            if (state == null) return;
            state.CancelDraftedFire(false);
            state.ClearDraftedMovementSearchTracking();
            state.CancelCloseCombat();
            state.ClearCloseAttackRequest();
            if (!manualOnly || OwnsManualCycle(state.primaryWeaponCycle))
                CancelCycleActions(pawn, state.primaryWeaponCycle);
            if (!manualOnly || OwnsManualCycle(state.secondaryWeaponCycle))
                CancelCycleActions(pawn, state.secondaryWeaponCycle);
            state.sharedTargetSearch?.Reset();
            state.ClearDedicatedFollowupJobRequest();
            state.dedicatedContinuityTarget = null;
            state.dedicatedContinuityUntilTick = -1;
            state.dualCloseCombatActive = false;
            state.dualCloseTarget = null;
            RimKataDualWeaponController.RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;
            RimKataGroundPoseUtility.NotifyAimCancelled(pawn);
            if (pawn.stances?.curStance is Stance_RimKataAim)
                pawn.stances.SetStance(new Stance_Mobile());
        }

        private static void CancelCycleActions(Pawn pawn, RimKataWeaponCycleState cycle)
        {
            if (cycle == null) return;
            if (cycle.nativeAttack?.reactiveMotion != null || cycle.plannedInterception
                || cycle.nativeAttack?.interceptionShot == true)
            {
                // Clear the input's orders, preserving an interception or reaction's own execution.
                cycle.ClearAutomaticCandidates();
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
                cycle.focusedTarget = null;
                cycle.focusedTargetFromAttackGizmo = false;
            }
            else RimKataDualWeaponController.ClearManualWeaponTarget(pawn, cycle);
            RimKataDirectionalFire.Clear(cycle);
        }

        internal static bool CombatTarget(Pawn pawn, Thing target, bool allowProne = true)
        {
            if (!IsControlled(pawn) || !IsCombatTarget(pawn, target)) return false;
            RimKataPawnCombatState state = Prepare(pawn);
            if (state == null) return false;
            RimKataWeaponCycleState primary = Usable(state.primaryWeaponCycle) ? state.primaryWeaponCycle : null;
            RimKataWeaponCycleState secondary = Usable(state.secondaryWeaponCycle) ? state.secondaryWeaponCycle : null;
            bool primaryAssigned = LiveFocus(pawn, primary);
            bool secondaryAssigned = LiveFocus(pawn, secondary);
            bool bothAssigned = primaryAssigned && secondaryAssigned;
            RimKataWeaponCycleState selected = primary != null && !primaryAssigned ? primary
                : secondary != null && !secondaryAssigned ? secondary
                : bothAssigned && replaceSecondaryNext ? secondary : primary ?? secondary;
            if (selected == null) return false;
            if (selected.focusedTarget == target)
            {
                RimKataWeaponCycleState other = selected == primary ? secondary : primary;
                if (other == null || other.focusedTarget == target)
                {
                    OffenseStopped = false;
                    return true;
                }
                selected = other;
            }
            if (!RimKataDualWeaponController.NotifyPlayerWeaponTarget(
                    pawn, selected.boundVerb, target, true)) return false;
            OffenseStopped = false;
            if (bothAssigned) replaceSecondaryNext = selected == primary;
            if (allowProne) NotifyAim(pawn, state, selected, target);
            return true;
        }

        internal static bool CombatFire(Pawn pawn, LocalTargetInfo target)
        {
            if (!IsControlled(pawn) || !pawn.Drafted || !target.IsValid) return false;
            if (!manualFireActive)
            {
                // A new left-click replaces persistent focus; held updates keep both weapon cycles.
                CancelCombatActions(pawn);
                replaceSecondaryNext = false;
            }
            OffenseStopped = false;
            manualFireActive = manualInputDrafted = true;
            manualFireBlocked = false;
            manualFireTarget = target.Thing;
            RimKataPawnCombatState state = Prepare(pawn);
            if (state == null) return false;
            if (target.HasThing)
            {
                bool primary = SetManualTarget(pawn, state, state.primaryWeaponCycle, target.Thing);
                bool secondary = SetManualTarget(pawn, state, state.secondaryWeaponCycle, target.Thing);
                return primary || secondary;
            }
            bool first = SetDirectionalTarget(pawn, state, state.primaryWeaponCycle, target.Cell);
            bool second = SetDirectionalTarget(pawn, state, state.secondaryWeaponCycle, target.Cell);
            if (first || second) RimKataGroundPoseUtility.NotifyJobChanged(pawn);
            return first || second;
        }

        private static RimKataPawnCombatState Prepare(Pawn pawn)
        {
            if (!RimKataEligibility.CanBeginGunKataAttack(pawn) || RimKataSubdueUtility.IsHolding(pawn))
                return null;
            RimKataPawnCombatState state = pawn.Map?.GetComponent<RimKataMapComponent>()?.GetState(pawn, true);
            if (state != null) RimKataDualWeaponController.BindCurrentWeapons(pawn, state, true);
            return state;
        }

        private static bool Usable(RimKataWeaponCycleState cycle)
            => cycle?.weapon != null && !cycle.weapon.Destroyed && cycle.ordinaryWeaponEnabled
                && cycle.boundVerb?.IsMeleeAttack == false;

        private static bool UsableManualCycle(RimKataWeaponCycleState cycle)
            => cycle?.weapon != null && !cycle.weapon.Destroyed
                && cycle.ordinaryWeaponEnabled && cycle.boundVerb != null;

        private static void RememberManualCycle(RimKataPawnCombatState state, RimKataWeaponCycleState cycle)
        {
            if (cycle == state.primaryWeaponCycle) manualPrimaryWeapon = cycle.weapon;
            else manualSecondaryWeapon = cycle.weapon;
        }

        private static bool LiveFocus(Pawn pawn, RimKataWeaponCycleState cycle)
        {
            Thing target = cycle?.focusedTarget;
            return target != null && !target.Destroyed && target.Spawned && target.Map == pawn.Map
                && (!(target is Pawn other) || RimKataTargeting.IsPawnTargetStateValid(
                    other, IsManualTarget(pawn, cycle.weapon, target)));
        }

        private static bool SetManualTarget(Pawn pawn, RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle, Thing target)
        {
            if (!UsableManualCycle(cycle)) return false;
            RememberManualCycle(state, cycle);
            if (cycle.focusedTarget == target) return true;
            if (!RimKataDualWeaponController.NotifyPlayerWeaponTarget(
                    pawn, cycle.boundVerb, target, true, allowMelee: true)) return false;
            NotifyAim(pawn, state, cycle, target);
            return true;
        }

        private static bool SetDirectionalTarget(Pawn pawn, RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle, IntVec3 cell)
        {
            if (!Usable(cycle))
            {
                if (OwnsManualCycle(cycle)) CancelCycleActions(pawn, cycle);
                return false;
            }
            RememberManualCycle(state, cycle);
            float range = RimKataRangeUtility.ResolveEffectiveRange(pawn, cycle.weapon, cycle.boundVerb);
            if (!cell.InBounds(pawn.Map) || cell == pawn.Position || range <= 0f
                || pawn.Position.DistanceToSquared(cell) > range * range && !ExtendsManualFireToRange(pawn))
            {
                CancelCycleActions(pawn, cycle);
                return false;
            }
            // Store the cursor's anchor; each shot projects from the pawn's current position.
            if (cycle.directionalFireCell == cell) return true;
            return cycle.HasDirectionalFire
                ? RimKataDualWeaponController.UpdateDirectionalFire(pawn, cycle.boundVerb, cell)
                : RimKataDualWeaponController.BeginDirectionalFire(pawn, cycle.boundVerb, cell);
        }

        private static void NotifyAim(Pawn pawn, RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle, Thing target)
            => RimKataGroundPoseUtility.NotifyAimStarted(pawn, cycle.boundVerb, target, knownState: state);
    }
}
