using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public static class RimKataAttackGizmoTargetContext
    {
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static int squadDepth;

        public static bool Active => depth > 0;
        public static bool SquadActive => squadDepth > 0;

        public static void Invoke(
            Action<LocalTargetInfo> action,
            LocalTargetInfo target)
        {
            if (action == null)
            {
                return;
            }

            Enter(false);
            try
            {
                action(target);
            }
            finally
            {
                Exit(false);
            }
        }

        public static T Invoke<T>(Func<T> action)
        {
            if (action == null)
            {
                return default(T);
            }

            Enter(false);
            try
            {
                return action();
            }
            finally
            {
                Exit(false);
            }
        }

        public static void InvokeSquad(
            Action<LocalTargetInfo> action,
            LocalTargetInfo target)
        {
            if (action == null)
            {
                return;
            }

            Enter(true);
            try
            {
                action(target);
            }
            finally
            {
                Exit(true);
            }
        }

        public static void InvokeSquad(Action action)
        {
            if (action == null)
            {
                return;
            }

            Enter(true);
            try
            {
                action();
            }
            finally
            {
                Exit(true);
            }
        }

        public static T InvokeSquad<T>(Func<T> action)
        {
            if (action == null)
            {
                return default(T);
            }

            Enter(true);
            try
            {
                return action();
            }
            finally
            {
                Exit(true);
            }
        }

        private static void Enter(bool squad)
        {
            depth++;
            if (squad)
            {
                squadDepth++;
            }
        }

        private static void Exit(bool squad)
        {
            if (squad)
            {
                squadDepth--;
            }
            depth--;
        }
    }

    public sealed class Stance_RimKataAim : Stance_Busy
    {
        private bool leanCacheInitialized;
        private IntVec3 lastLeanRoot = IntVec3.Invalid;
        private IntVec3 lastLeanTargetCell = IntVec3.Invalid;
        private Thing lastLeanTargetThing;
        private Verb lastLeanVerb;
        private bool lastLeanTargetUsable;
        private volatile int cachedMovementDirectionCode;

        public override bool StanceBusy => false;

        public Stance_RimKataAim()
        {
        }

        public Stance_RimKataAim(int ticks, LocalTargetInfo focusTarget, Verb verb)
            : base(ticks, focusTarget, verb)
        {
        }

        public override void StanceTick()
        {
            RefreshLeanNow();
            base.StanceTick();
        }

        internal bool TryGetCachedMovementDirection(out IntVec3 direction)
        {
            int code = cachedMovementDirectionCode;
            if (code == 0)
            {
                direction = IntVec3.Invalid;
                return false;
            }

            int packed = code - 1;
            direction = new IntVec3(
                packed / 3 - 1,
                0,
                packed % 3 - 1);
            return direction != IntVec3.Zero;
        }

        public void RefreshLeanNow()
        {
            Pawn pawn = Pawn;
            int movementDirectionCode = 0;
            if (pawn?.Spawned == true
                && RimKataDodgeMovementUtility.TryGetCurrentMovementDirection(
                    pawn,
                    out IntVec3 movementDirection)
                && movementDirection != IntVec3.Zero)
            {
                movementDirectionCode = (movementDirection.x + 1) * 3
                    + movementDirection.z
                    + 2;
            }

            cachedMovementDirectionCode = movementDirectionCode;
            IntVec3 root = pawn?.Spawned == true
                ? pawn.Position
                : IntVec3.Invalid;
            IntVec3 targetCell = focusTarg.IsValid
                ? focusTarg.Cell
                : IntVec3.Invalid;
            Thing targetThing = focusTarg.HasThing
                ? focusTarg.Thing
                : null;
            bool targetUsable = targetThing == null
                || (targetThing.Spawned && targetThing.Map == pawn?.Map);
            bool targetCellChanged = targetThing == null
                && targetCell != lastLeanTargetCell;
            if (leanCacheInitialized
                && root == lastLeanRoot
                && !targetCellChanged
                && targetThing == lastLeanTargetThing
                && verb == lastLeanVerb
                && targetUsable == lastLeanTargetUsable)
            {
                return;
            }

            leanCacheInitialized = true;
            lastLeanRoot = root;
            lastLeanTargetCell = targetCell;
            lastLeanTargetThing = targetThing;
            lastLeanVerb = verb;
            lastLeanTargetUsable = targetUsable;

            if (pawn?.Spawned != true || pawn.Drawer == null)
            {
                return;
            }

            if (!targetUsable
                || verb == null
                || !focusTarg.IsValid
                || !verb.TryFindShootLineFromTo(
                    root,
                    focusTarg,
                    out ShootLine line))
            {
                line = new ShootLine(root, root);
            }

            pawn.Drawer.Notify_WarmingCastAlongLine(line, root);
        }
    }

    public sealed class JobDriver_RimKataAttack : JobDriver, IRimKataResponseCooldown
    {
        private const int NormalSpeedRefreshIntervalTicks = 600;

        private int warmupTicksRemaining = -1;
        private int cooldownTicksRemaining;
        private int nextNormalSpeedRequestTick;
        private Thing plannedTarget;
        private bool plannedInterception;
        private bool plannedCloseAttack;
        private bool plannedCloseContext;
        private bool dualCycleStateImported;
        private bool endingJob;
        private RimKataPawnCombatState postTickCombatState;

        private Thing AssignedTarget => TargetThingA;
        private bool IsPlayerForced => job?.playerForced == true;
        internal bool CanAbsorbAutomaticAttackJob => !endingJob;

        public override string GetReport()
        {
            string reportOverride = GetReportStringOverride();
            if (!reportOverride.NullOrEmpty())
            {
                return reportOverride;
            }

            Verb requestedVerb = job?.verbToUse
                ?? RimKataWeaponSlotUtility.PrimaryVerb(pawn?.equipment?.Primary);
            JobDef reportJob = requestedVerb?.IsMeleeAttack == false
                ? JobDefOf.AttackStatic
                : JobDefOf.AttackMelee;
            return ReportStringProcessed(reportJob.reportString);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref warmupTicksRemaining, "rimKataWarmupTicksRemaining", -1);
            Scribe_Values.Look(ref cooldownTicksRemaining, "rimKataCooldownTicksRemaining");
            Scribe_References.Look(ref plannedTarget, "rimKataPlannedTarget");
            Scribe_Values.Look(ref plannedInterception, "rimKataPlannedInterception");
            Scribe_Values.Look(ref plannedCloseAttack, "rimKataPlannedCloseAttack");
            Scribe_Values.Look(ref plannedCloseContext, "rimKataPlannedCloseContext");
            Scribe_Values.Look(ref dualCycleStateImported, "rimKataDualCycleStateImported");
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(delegate
            {
                endingJob = true;
                postTickCombatState = null;
                ClearPlannedAttack();
                ClearAimStance();
                RimKataDualWeaponController
                    .NotifyDedicatedCombatJobFinished(pawn);
            });

            Toil initialization = ToilMaker.MakeToil(
                "RimKataCombatInitialization");
            initialization.initAction = delegate
            {
                if (!dualCycleStateImported)
                {
                    dualCycleStateImported = true;
                    RimKataDualWeaponController.ImportLegacyPrimaryState(
                        pawn,
                        cooldownTicksRemaining,
                        warmupTicksRemaining,
                        plannedTarget,
                        plannedInterception,
                        plannedCloseAttack,
                        plannedCloseContext);
                    cooldownTicksRemaining = 0;
                    warmupTicksRemaining = -1;
                    plannedTarget = null;
                    plannedInterception = false;
                    plannedCloseAttack = false;
                    plannedCloseContext = false;
                }

                EnsurePathToAssignedTarget();
            };
            initialization.defaultCompleteMode =
                ToilCompleteMode.Instant;
            yield return initialization;

            Toil combat = ToilMaker.MakeToil("RimKataCombatLoop");
            combat.tickAction = CombatTick;
            combat.defaultCompleteMode = ToilCompleteMode.Never;
            yield return combat;
        }

        public bool TryApplyResponseCooldown(
            ThingWithComps weapon,
            Verb verb,
            LocalTargetInfo focus)
        {
            if (pawn != null && pawn.IsBurning())
            {
                CancelForFire();
                return true;
            }

            return RimKataDualWeaponController.TryApplyResponseCooldown(pawn, weapon, verb, focus);
        }

        private void CombatTick()
        {
            RimKataDualWeaponController.TickCombat(pawn, false);
        }

        internal void ShareCombatStateWithPostTick(RimKataPawnCombatState state)
        {
            postTickCombatState = state;
        }

        internal bool TryTakePostTickCombatState(out RimKataPawnCombatState state)
        {
            state = postTickCombatState;
            postTickCombatState = null;
            // A finish callback or state removal must not revive the old state.
            return !endingJob
                && state?.ownerComponent != null
                && state.ownerComponent.map == pawn.Map;
        }

        internal Thing PrepareAssignedTarget(
            RimKataPawnCombatState state,
            out bool assignedTargetValid,
            out bool weaponScopedFocusJob)
        {
            Thing assignedTarget = AssignedTarget;
            weaponScopedFocusJob =
                RimKataDualWeaponController.IsWeaponScopedFocusJob(
                    pawn,
                    state,
                    assignedTarget);
            assignedTargetValid = IsValidAssignedTarget(assignedTarget);
            if (assignedTargetValid)
            {
                RimKataDualWeaponController.RefreshDedicatedTargetContinuity(
                    pawn,
                    state,
                    assignedTarget);
            }
            return assignedTarget;
        }

        internal void TickPreparedCombat(
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool assignedTargetValid,
            bool weaponScopedFocusJob,
            bool allowAutomaticRangedFire)
        {
            if (!assignedTargetValid)
            {
                if (weaponScopedFocusJob)
                {
                    job.playerForced = false;
                    job.killIncappedTarget = false;
                }

                RimKataDualWeaponController.EnsureContinuationSearchBeforeExit(
                    pawn);
                if (TryAdoptContinuationTarget(out assignedTarget))
                {
                    assignedTargetValid = true;
                }
                else
                {
                    RimKataDualWeaponController.TickPreparedWeaponCycles(
                        pawn,
                        state,
                        null,
                        IsPlayerForced,
                        job.killIncappedTarget,
                        null,
                        false,
                        allowAutomaticRangedFire);

                    if (TryAdoptContinuationTarget(out assignedTarget))
                    {
                        assignedTargetValid = true;
                    }
                    else if (RimKataDualWeaponController.HasContinuationSearchWork(pawn))
                    {
                        pawn.pather?.StopDead();
                        return;
                    }
                }

                if (!assignedTargetValid)
                {
                    EndRimKataJobWith(JobCondition.Succeeded);
                    return;
                }
            }

            bool assignedTargetInTouchRange = assignedTargetValid
                && pawn.CanReachImmediate(assignedTarget, PathEndMode.Touch);
            bool closeTargetResolutionKnown = RimKataDualWeaponController
                .ReconcileCloseCombatBeforeContinuityCheck(
                    pawn,
                    state,
                    assignedTarget,
                    IsPlayerForced,
                    job.killIncappedTarget,
                    out Thing immediateCloseTarget,
                    assignedTargetValid,
                    assignedTargetInTouchRange);
            if (!RimKataDualWeaponController.HasCombatContinuity(pawn, state))
            {
                EndRimKataJobWith(JobCondition.Succeeded);
                return;
            }

            MaintainCombatNormalSpeedRequest(assignedTarget);

            if (!closeTargetResolutionKnown)
            {
                immediateCloseTarget = RimKataDualWeaponController.ResolveImmediateCloseTarget(
                    pawn,
                    state,
                    assignedTarget,
                    IsPlayerForced,
                    job.killIncappedTarget,
                    assignedTargetValid,
                    assignedTargetInTouchRange);
            }

            if (RimKataDodgeMovementUtility.CalculateIsActive(
                    pawn,
                    state))
            {
                TickCombatFire(
                    state,
                    assignedTarget,
                    immediateCloseTarget,
                    allowAutomaticRangedFire);
                return;
            }

            bool closeCombatActive = assignedTargetInTouchRange || immediateCloseTarget != null;
            if (!closeCombatActive)
            {
                bool canRush = RimKataDualWeaponController.CanRushTarget(
                    pawn,
                    assignedTarget);
                if (!canRush && !CanAttackWithoutRushing(assignedTarget))
                {
                    pawn.pather?.StopDead();
                    TickCombatFire(state, assignedTarget, null, allowAutomaticRangedFire);
                    return;
                }

                if (canRush
                    && (pawn.pather?.Moving != true
                        || pawn.pather.Destination.Thing != assignedTarget))
                {
                    EnsurePathToAssignedTarget();
                }

                TickCombatFire(state, assignedTarget, null, allowAutomaticRangedFire);
                return;
            }

            if (assignedTargetInTouchRange)
            {
                state.EnterCloseCombat(assignedTarget);
            }

            pawn.pather.StopDead();
            TickCombatFire(state, assignedTarget, immediateCloseTarget, allowAutomaticRangedFire);
        }

        private void MaintainCombatNormalSpeedRequest(Thing assignedTarget)
        {
            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (currentTick < 0 || currentTick < nextNormalSpeedRequestTick)
            {
                return;
            }

            nextNormalSpeedRequestTick = currentTick
                + NormalSpeedRefreshIntervalTicks;
            RimKataVerbUtility.RequestNormalSpeedForCombat(
                job?.verbToUse,
                new LocalTargetInfo(assignedTarget));
        }

        private void TickCombatFire(
            RimKataPawnCombatState state,
            Thing assignedTarget,
            Thing resolvedCloseTarget,
            bool allowAutomaticRangedFire)
        {
            RimKataDualWeaponController.TickPreparedWeaponCycles(
                pawn,
                state,
                assignedTarget,
                IsPlayerForced,
                job.killIncappedTarget,
                resolvedCloseTarget,
                true,
                allowAutomaticRangedFire);
        }

        private void ClearAimStance()
        {
            if (pawn?.stances?.curStance is Stance_RimKataAim)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }
        }

        internal void EndRimKataJobWith(JobCondition condition)
        {
            endingJob = true;
            EndJobWith(condition);
        }

        private void ClearPlannedAttack(bool resetWarmup = true)
        {
            plannedTarget = null;
            plannedInterception = false;
            plannedCloseAttack = false;
            plannedCloseContext = false;
            if (resetWarmup)
            {
                warmupTicksRemaining = -1;
            }
        }

        internal void CancelForFire(RimKataPawnCombatState state = null)
        {
            warmupTicksRemaining = -1;
            cooldownTicksRemaining = 0;
            ClearPlannedAttack();
            ClearAimStance();
            if (state != null)
            {
                RimKataDualWeaponController.CancelOffenseForFire(pawn, state);
            }
            else
            {
                RimKataDraftedFireController.CancelForFire(pawn);
            }
        }

        private void EnsurePathToAssignedTarget(
            bool targetReachabilityConfirmed = false)
        {
            if (RimKataDodgeMovementUtility.IsActive(pawn))
            {
                return;
            }

            Thing target = AssignedTarget;
            if (!RimKataDualWeaponController.CanRushTarget(pawn, target))
            {
                pawn.pather?.StopDead();
                return;
            }

            if (target == null || !target.Spawned || pawn.CanReachImmediate(target, PathEndMode.Touch))
            {
                return;
            }

            if (pawn.pather.Moving && pawn.pather.Destination.Thing == target)
            {
                return;
            }

            if (!targetReachabilityConfirmed
                && !pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly))
            {
                pawn.pather?.StopDead();
                return;
            }

            pawn.pather.StartPath(target, PathEndMode.Touch);
        }

        private bool CanAttackWithoutRushing(Thing target)
        {
            ThingWithComps orderedWeapon = IsPlayerForced
                ? job?.verbToUse?.EquipmentSource as ThingWithComps
                : null;
            bool weaponScopedFocusJob = orderedWeapon != null
                && RimKataDualWeaponController.IsWeaponScopedFocusJob(
                    pawn,
                    target);
            if (weaponScopedFocusJob)
            {
                return RimKataWeaponSlotUtility
                    .CanWeaponAttackTargetWithoutRushing(
                        pawn,
                        orderedWeapon,
                        target);
            }

            return RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(pawn, target);
        }

        private bool TryAdoptContinuationTarget(out Thing target)
        {
            if (!RimKataDualWeaponController.TryGetContinuationTarget(pawn, IsPlayerForced, job.killIncappedTarget, out target))
            {
                return false;
            }

            SetAssignedTarget(target);
            return true;
        }

        private void SetAssignedTarget(
            Thing target,
            bool targetReachabilityConfirmed = false)
        {
            Thing previousTarget = AssignedTarget;
            if (target == null || previousTarget == target)
            {
                return;
            }

            job.targetA = target;
            EnsurePathToAssignedTarget(targetReachabilityConfirmed);
            if (!IsPlayerForced
                && pawn?.Drafted != true
                && (job?.jobGiver is JobGiver_ConfigurableHostilityResponse
                    || job?.jobGiver is JobGiver_ReactToCloseMeleeThreat)
                && RimKataDualWeaponController
                    .CounterattackControlEnabled(pawn))
            {
                MoteMaker.MakeColonistActionOverlay(
                    pawn,
                    ThingDefOf.Mote_ColonistAttacking);
            }
        }

        internal bool TryPromoteAutomaticJobTarget(Thing target)
        {
            Thing currentTarget = AssignedTarget;
            if (endingJob
                || pawn?.Map == null
                || pawn.InMentalState
                || IsPlayerForced
                || job?.def != RimKataDefOf.RimKata_Attack
                || !RimKataEligibility.RandomAttackEnabledForPawn(pawn)
                || target == null
                || target is Projectile
                || target == currentTarget
                || !IsValidAssignedTarget(target)
                || !pawn.CanReach(
                    target,
                    PathEndMode.Touch,
                    Danger.Deadly))
            {
                return false;
            }

            SetAssignedTarget(target, true);
            return true;
        }

        private bool IsValidAssignedTarget(Thing target)
        {
            if (target == null || target.Destroyed || !target.Spawned || target.Map != pawn.Map)
            {
                return false;
            }

            if (target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    IsPlayerForced && job.killIncappedTarget))
            {
                return false;
            }

            return IsPlayerForced
                || RimKataTargeting.IsValidAutomaticAttackTarget(
                    pawn,
                    target);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.OrderForceTarget))]
    public static class Patch_Verb_OrderForceTarget_RimKata
    {
        public static bool Prefix(Verb __instance, LocalTargetInfo target)
        {
            Pawn pawn = __instance?.CasterPawn;
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || RimKataAttackGizmoTargetContext.Active)
            {
                return true;
            }

            if (__instance?.IsMeleeAttack == true)
            {
                bool closeTargetAccepted = target.HasThing
                    && RimKataDualWeaponController
                        .TryNotifyPlayerMeleeCloseTarget(
                            pawn,
                            target.Thing,
                            true);
                return !closeTargetAccepted
                    || pawn?.CurJobDef != RimKataDefOf.RimKata_Attack;
            }

            bool directIncapacitatedOrder =
                RimKataTargeting.IsIncapacitatedTarget(target.Pawn);
            if (!target.HasThing
                || (target.Pawn != null
                    && !RimKataTargeting.IsPawnTargetStateValid(
                        target.Pawn,
                        directIncapacitatedOrder)))
            {
                return true;
            }

            if (!RimKataDualWeaponController.CanUsePlayerWeaponCommand(
                pawn,
                __instance))
            {
                return true;
            }

            if (directIncapacitatedOrder)
            {
                if (pawn.IsBurning()
                    || !RimKataWeaponSlotUtility
                        .CanWeaponAttackTargetWithoutRushing(
                            pawn,
                            __instance.EquipmentSource as ThingWithComps,
                            target.Thing))
                {
                    return true;
                }

                Job incapacitatedTargetJob = JobMaker.MakeJob(
                    RimKataDefOf.RimKata_Attack,
                    target);
                incapacitatedTargetJob.playerForced = true;
                incapacitatedTargetJob.killIncappedTarget = true;
                incapacitatedTargetJob.verbToUse = __instance;
                pawn.jobs.TryTakeOrderedJob(
                    incapacitatedTargetJob,
                    JobTag.Misc);
                return false;
            }

            bool fixedWeaponTarget =
                RimKataDualWeaponController.NotifyPlayerWeaponTarget(
                    pawn,
                    __instance,
                    target.Thing,
                    true);
            bool playerCloseFire =
                RimKataDualWeaponController.BeginPlayerRangedCloseAttack(
                    pawn,
                    __instance,
                    target.Thing);

            if (fixedWeaponTarget
                && (pawn?.Drafted == true
                    || pawn?.CurJobDef == RimKataDefOf.RimKata_Attack))
            {
                return false;
            }

            bool startUndraftedFocusJob = fixedWeaponTarget
                && pawn?.Drafted != true;
            if (!startUndraftedFocusJob
                && !playerCloseFire
                && !RimKataDualWeaponController.IsDedicatedFollowupActive(pawn))
            {
                return true;
            }

            if (pawn?.IsBurning() == true
                || !RimKataWeaponSlotUtility.CanWeaponAttackTargetWithoutRushing(
                    pawn,
                    __instance.EquipmentSource as ThingWithComps,
                    target.Thing))
            {
                return true;
            }

            Verb selectedVerb = __instance;
            if (selectedVerb == null)
            {
                return true;
            }

            Job job = JobMaker.MakeJob(RimKataDefOf.RimKata_Attack, target);
            job.playerForced = true;
            job.verbToUse = selectedVerb;
            if (target.Pawn != null)
            {
                job.killIncappedTarget =
                    RimKataTargeting.IsIncapacitatedTarget(target.Pawn);
            }

            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            return false;
        }
    }

    [HarmonyPatch(
        typeof(Verb),
        nameof(Verb.CanHitTarget),
        new[] { typeof(LocalTargetInfo) })]
    public static class Patch_Verb_CanHitTarget_RimKataCloseOrder
    {
        public static void Postfix(
            Verb __instance,
            LocalTargetInfo targ,
            ref bool __result)
        {
            if (__result
                || __instance?.IsMeleeAttack != false
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(__instance.CasterPawn)
                || !targ.HasThing
                || Find.Targeter?.targetingSource?.GetVerb != __instance)
            {
                return;
            }

            if (!RimKataDualWeaponController.CanOrderRangedCloseAttack(
                __instance.CasterPawn,
                __instance,
                targ.Thing))
            {
                return;
            }

            __result = true;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
    public static class Patch_PawnJobTracker_TryTakeOrderedJob_RimKata
    {
        public static bool Prefix(Pawn ___pawn, ref Job job)
        {
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(___pawn))
            {
                return true;
            }

            if (Patch_CommandVerbTarget_RimKataSecondarySwap
                    .TryConsumePendingMeleeAttack(
                        ___pawn,
                        job,
                        out Verb secondaryMeleeVerb)
                && RimKataDualWeaponController
                    .TryConvertSecondaryMeleeAttackOrder(
                        ___pawn,
                        job,
                        secondaryMeleeVerb))
            {
                return true;
            }

            if (RimKataAttackGizmoTargetContext.SquadActive
                && ___pawn?.Drafted != true)
            {
                if (job != null)
                {
                    JobMaker.ReturnToPool(job);
                    job = null;
                }
                return false;
            }

            if (RimKataAttackGizmoTargetContext.Active
                && !RimKataAttackGizmoTargetContext.SquadActive
                && RimKataDualWeaponController.TryConvertPlayerRushOrder(
                    ___pawn,
                    job))
            {
                return true;
            }

            bool orderedAttack = job != null
                && job.def == JobDefOf.AttackStatic
                && job.targetA.HasThing;
            if (orderedAttack
                && ___pawn?.Drafted == true
                && RimKataTargeting.IsIncapacitatedTarget(job.targetA.Pawn))
            {
                job.killIncappedTarget = true;
            }

            // Right-click orders reach this prefix before vanilla sets
            // playerForced. A downed target needs the same dedicated request
            // context as a direct weapon gizmo, not automatic target selection.
            bool playerIncapacitatedRangedOrder = orderedAttack
                && ___pawn?.Drafted == true
                && ___pawn.IsPlayerControlled
                && RimKataTargeting.IsIncapacitatedTarget(job.targetA.Pawn);

            bool playerSquadRangedOrder =
                RimKataAttackGizmoTargetContext.SquadActive
                && orderedAttack;
            Verb orderedVerb = job?.verbToUse;
            bool playerRangedCloseOrder = orderedAttack
                && job.playerForced
                && RimKataDualWeaponController.CanOrderRangedCloseAttack(
                    ___pawn,
                    orderedVerb,
                    job.targetA.Thing);
            if (!playerSquadRangedOrder
                && !RimKataDualWeaponController.IsDedicatedFollowupActive(___pawn)
                && !playerRangedCloseOrder
                && !playerIncapacitatedRangedOrder)
            {
                return true;
            }

            if (!RimKataEligibility.CanBeginGunKataAttack(___pawn))
            {
                return true;
            }

            if (___pawn?.Drafted != true
                || job == null
                || !orderedAttack
                || ___pawn.IsBurning()
                || !RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                    ___pawn,
                    job.targetA.Thing))
            {
                return true;
            }

            bool preserveOrderedVerb = playerRangedCloseOrder
                || (playerIncapacitatedRangedOrder
                    && RimKataDualWeaponController.CanUsePlayerWeaponCommand(___pawn, orderedVerb)
                    && RimKataWeaponSlotUtility.CanWeaponAttackTargetWithoutRushing(
                        ___pawn, orderedVerb.EquipmentSource as ThingWithComps, job.targetA.Thing));
            Verb verb = preserveOrderedVerb
                ? orderedVerb
                : RimKataWeaponSlotUtility.BestRangedCombatVerb(
                    ___pawn,
                    job.targetA.Thing);
            if (verb == null)
            {
                return true;
            }

            job.def = RimKataDefOf.RimKata_Attack;
            job.verbToUse = verb;
            if (playerIncapacitatedRangedOrder)
            {
                job.playerForced = true;
            }
            job.killIncappedTarget =
                RimKataTargeting.IsIncapacitatedTarget(job.targetA.Pawn);
            return true;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    public static class Patch_PawnJobTracker_StartJob_EnemyRimKata
    {
        public static bool Prefix(
            Pawn ___pawn,
            ref Job newJob,
            ThinkNode jobGiver,
            bool fromQueue)
        {
            if (___pawn?.InMentalState == true)
            {
                if (newJob?.def != RimKataDefOf.RimKata_Attack)
                {
                    return true;
                }

                JobMaker.ReturnToPool(newJob);
                newJob = null;
                return false;
            }

            if (newJob?.def == RimKataDefOf.RimKata_Attack
                && !RimKataDualWeaponController.CanContinueWeaponCycles(___pawn))
            {
                JobMaker.ReturnToPool(newJob);
                newJob = null;
                return false;
            }

            Job counterattackOpeningJob = newJob;
            RimKataCounterattackOpeningResult counterattackOpeningResult =
                RimKataDualWeaponController.HandleCounterattackOpening(
                    ___pawn,
                    newJob,
                    jobGiver,
                    out Job convertedCounterattackJob);
            if (counterattackOpeningResult
                == RimKataCounterattackOpeningResult.Absorbed)
            {
                if (counterattackOpeningJob != null)
                {
                    JobMaker.ReturnToPool(counterattackOpeningJob);
                    newJob = null;
                }

                return false;
            }

            if (counterattackOpeningResult
                == RimKataCounterattackOpeningResult.Converted)
            {
                if (counterattackOpeningJob != null
                    && counterattackOpeningJob != convertedCounterattackJob)
                {
                    JobMaker.ReturnToPool(counterattackOpeningJob);
                }
                newJob = convertedCounterattackJob;
            }

            bool preserveVanillaCounterattack =
                (jobGiver is JobGiver_ConfigurableHostilityResponse
                    || jobGiver is JobGiver_ReactToCloseMeleeThreat)
                && !RimKataDualWeaponController
                    .CounterattackControlEnabled(___pawn);
            if (!preserveVanillaCounterattack)
            {
                NormalizeMeleeVerb(___pawn, newJob);
                PreferPairRangedVerb(___pawn, newJob);
            }
            NotifyMeleeTarget(___pawn, newJob);

            if (preserveVanillaCounterattack)
            {
                return true;
            }

            if (!RimKataDualWeaponController.IsDedicatedFollowupActive(___pawn))
            {
                return true;
            }

            if (NormalizeQueuedPlayerRimKataAttack(___pawn, newJob, fromQueue))
            {
                return true;
            }

            if (ShouldConvertQueuedPlayerAttack(___pawn, newJob, fromQueue, out Verb queuedVerb))
            {
                newJob.def = RimKataDefOf.RimKata_Attack;
                newJob.verbToUse = queuedVerb;
                return true;
            }

            if (!ShouldConvertEnemyAttack(___pawn, newJob, out Verb verb))
            {
                return true;
            }

            newJob.def = RimKataDefOf.RimKata_Attack;
            newJob.verbToUse = verb;
            return true;
        }

        public static void Postfix(
            Pawn ___pawn,
            Job newJob,
            ThinkNode jobGiver)
        {
            ThinkNode effectiveJobGiver = jobGiver ?? newJob?.jobGiver;
            if (___pawn?.CurJob == newJob
                && newJob?.def == RimKataDefOf.RimKata_Attack
                && !newJob.playerForced
                && ___pawn.Drafted != true
                && ___pawn.InMentalState != true
                && (effectiveJobGiver is JobGiver_ConfigurableHostilityResponse
                    || effectiveJobGiver is JobGiver_ReactToCloseMeleeThreat)
                && newJob.targetA.HasThing
                && RimKataDualWeaponController
                    .CounterattackControlEnabled(___pawn))
            {
                MoteMaker.MakeColonistActionOverlay(
                    ___pawn,
                    ThingDefOf.Mote_ColonistAttacking);
            }
        }

        private static void NormalizeMeleeVerb(Pawn pawn, Job job)
        {
            if (job?.def != JobDefOf.AttackMelee
                || pawn == null
                || (job.verbToUse != null
                    && job.verbToUse.IsMeleeAttack)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                return;
            }

            job.verbToUse = null;
        }

        private static void PreferPairRangedVerb(Pawn pawn, Job job)
        {
            if (job?.def != JobDefOf.AttackStatic
                || !job.targetA.HasThing
                || pawn?.Map == null
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return;
            }

            Verb requestedVerb = job.verbToUse;
            ThingWithComps requestedWeapon =
                requestedVerb?.EquipmentSource as ThingWithComps;
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                : null;
            if (requestedVerb != null
                && !requestedVerb.IsMeleeAttack
                && (requestedWeapon == primary || requestedWeapon == secondary))
            {
                return;
            }

            Verb verb = RimKataWeaponSlotUtility.BestRangedCombatVerb(
                pawn,
                job.targetA.Thing);
            if (verb != null)
            {
                job.verbToUse = verb;
            }
        }

        private static void NotifyMeleeTarget(Pawn attacker, Job job)
        {
            if (job?.def == JobDefOf.AttackMelee && job.targetA.Pawn is Pawn target)
            {
                RimKataDraftedFireController.NotifyTargetedByHostile(target, attacker);
            }
        }

        private static bool NormalizeQueuedPlayerRimKataAttack(
            Pawn pawn,
            Job job,
            bool fromQueue)
        {
            if (!fromQueue
                || job?.def != RimKataDefOf.RimKata_Attack
                || job.playerForced != true
                || pawn?.Drafted != true
                || pawn.InMentalState
                || pawn.IsBurning())
            {
                return false;
            }

            Verb enabledVerb = RimKataWeaponSlotUtility.BestRangedCombatVerb(
                pawn,
                job.targetA.Thing)
                ?? RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    RimKataWeaponSlotUtility.PrimaryWeapon(pawn));
            if (enabledVerb != null)
            {
                if (!RimKataDualWeaponController.CanRushTarget(
                        pawn,
                        job.targetA.Thing)
                    && !RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                        pawn,
                        job.targetA.Thing))
                {
                    job.def = enabledVerb.IsMeleeAttack
                        ? JobDefOf.AttackMelee
                        : JobDefOf.AttackStatic;
                    job.verbToUse = enabledVerb.IsMeleeAttack ? null : enabledVerb;
                    return true;
                }

                job.verbToUse = enabledVerb;
                return true;
            }

            Verb currentVerb = pawn.equipment?.Primary?.TryGetComp<CompEquippable>()?.PrimaryVerb;
            bool hasRangedVerb = currentVerb != null && !currentVerb.IsMeleeAttack;
            job.def = hasRangedVerb ? JobDefOf.AttackStatic : JobDefOf.AttackMelee;
            job.verbToUse = hasRangedVerb ? currentVerb : null;
            return true;
        }

        private static bool ShouldConvertQueuedPlayerAttack(
            Pawn pawn,
            Job job,
            bool fromQueue,
            out Verb verb)
        {
            verb = null;
            bool vanillaCombatJob = job?.def == JobDefOf.AttackStatic;
            if (!fromQueue
                || !vanillaCombatJob
                || job.playerForced != true
                || pawn?.Drafted != true
                || pawn.InMentalState
                || pawn.IsBurning()
                || !job.targetA.HasThing)
            {
                return false;
            }

            Thing target = job.targetA.Thing;
            return target != null
                && target != pawn
                && target.Spawned
                && target.Map == pawn.Map
                && (!(target is Pawn targetPawn) || !targetPawn.IsPsychologicallyInvisible())
                && (verb = RimKataWeaponSlotUtility.BestRangedCombatVerb(
                    pawn,
                    target)) != null
                && RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                    pawn,
                    target);
        }

        private static bool ShouldConvertEnemyAttack(Pawn pawn, Job job, out Verb verb)
        {
            verb = null;
            bool vanillaCombatJob = job?.def == JobDefOf.AttackStatic || job?.def == JobDefOf.AttackMelee;
            if (!vanillaCombatJob
                || job.playerForced
                || !IsEligibleHostileRimKataPawn(pawn)
                || !job.targetA.HasThing)
            {
                return false;
            }

            Thing target = job.targetA.Thing;
            if (!IsValidEnemyTarget(pawn, target)
                || (!RimKataDualWeaponController.CanRushTarget(pawn, target)
                    && !RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                        pawn,
                        target)))
            {
                return false;
            }

            if (job.def == JobDefOf.AttackMelee
                && !RimKataDualWeaponController.HasUsableWeapon(pawn, true))
            {
                return false;
            }

            verb = RimKataWeaponSlotUtility.BestRangedCombatVerb(pawn, target)
                ?? RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    RimKataWeaponSlotUtility.PrimaryWeapon(pawn));
            return verb != null;
        }

        private static bool IsEligibleHostileRimKataPawn(Pawn pawn)
        {
            return pawn?.Faction != null
                && Faction.OfPlayer != null
                && pawn.Faction.HostileTo(Faction.OfPlayer)
                && !pawn.InMentalState
                && !pawn.IsBurning();
        }

        private static bool IsValidEnemyTarget(Pawn pawn, Thing target)
        {
            return pawn?.Map != null
                && target != null
                && target != pawn
                && target.Spawned
                && !target.Destroyed
                && target.Map == pawn.Map
                && RimKataTargeting.IsAutomaticEnemy(pawn, target)
                && (!(target is Pawn targetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(targetPawn));
        }
    }

    [HarmonyPatch(
        typeof(FloatMenuUtility),
        nameof(FloatMenuUtility.GetMeleeAttackAction))]
    public static class Patch_FloatMenuUtility_GetMeleeAttackAction_RimKata
    {
        public static void Postfix(
            Pawn pawn,
            LocalTargetInfo target,
            ref Action __result)
        {
            if (__result == null
                || !RimKataAttackGizmoTargetContext.Active
                || !target.HasThing
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
            {
                return;
            }

            Action originalAction = __result;
            Thing targetThing = target.Thing;
            bool squadOrder = RimKataAttackGizmoTargetContext.SquadActive;
            __result = delegate
            {
                if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
                {
                    originalAction();
                    return;
                }

                if (squadOrder)
                {
                    if (pawn?.Drafted == true)
                    {
                        RimKataAttackGizmoTargetContext.InvokeSquad(
                            originalAction);
                    }
                    return;
                }

                bool closeTargetAccepted = RimKataDualWeaponController
                    .TryNotifyPlayerMeleeCloseTarget(
                        pawn,
                        targetThing,
                        true);
                if (!closeTargetAccepted
                    || pawn?.CurJobDef != RimKataDefOf.RimKata_Attack)
                {
                    originalAction();
                }
            };
        }
    }

    [HarmonyPatch(typeof(FloatMenuOptionProvider_DraftedAttack), "GetRangedAttackAction")]
    public static class Patch_FloatMenuOptionProvider_DraftedAttack_RimKataSecondary
    {
        public static void Postfix(
            Pawn pawn,
            Thing target,
            ref Action __result,
            ref string failStr)
        {
            // Supplement only a single pawn's missing right-click option.
            // Group right-click orders and squad gizmos keep the primary's role.
            if (__result != null
                || !failStr.NullOrEmpty()
                || RimKataAttackGizmoTargetContext.Active
                || pawn?.Drafted != true
                || pawn.IsBurning()
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || !RimKataEligibility.CanBeginGunKataAttack(pawn)
                || FloatMenuUtility.UseRangedAttack(pawn)
                || Patch_FloatMenuUtility_GetRangedAttackAction_RimKata
                    .IsSelectedPlayerPawnInGroup(pawn)
                || !RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                || target?.Spawned != true
                || target.Destroyed
                || target.Map != pawn.Map
                || (target is Pawn targetPawn
                    && !RimKataTargeting.IsPawnTargetStateValid(targetPawn, true)))
            {
                return;
            }

            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            if (!RimKataEquipmentUtility.IsWeaponEnabled(secondary?.def)
                || !RimKataDualWeaponController.CanUsePlayerWeaponCommand(pawn, verb))
            {
                return;
            }

            failStr = Patch_FloatMenuUtility_GetRangedAttackAction_RimKata
                .PlayerRangedAttackFailure(pawn, target);
            if (failStr.NullOrEmpty()
                && !RimKataWeaponSlotUtility.CanWeaponAttackTargetWithoutRushing(
                    pawn, secondary, target))
            {
                float minRange = verb.verbProps.EffectiveMinRange(target, pawn);
                failStr = !pawn.Position.InHorDistOf(target.Position, verb.EffectiveRange)
                    ? "OutOfRange".Translate()
                    : pawn.Position.DistanceToSquared(target.Position) < minRange * minRange
                        ? "TooClose".Translate()
                        : "CannotHitTarget".Translate();
            }

            if (!failStr.NullOrEmpty())
            {
                failStr = failStr.CapitalizeFirst();
                return;
            }

            __result = delegate
            {
                if (pawn.Drafted
                    && !pawn.IsBurning()
                    && RimKataDualWeaponController.CanUsePlayerWeaponCommand(pawn, verb))
                {
                    Job job = JobMaker.MakeJob(RimKataDefOf.RimKata_Attack, target);
                    job.playerForced = true;
                    job.verbToUse = verb;
                    job.killIncappedTarget =
                        RimKataTargeting.IsIncapacitatedTarget(target as Pawn);
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
            };
        }
    }

    [HarmonyPatch(typeof(FloatMenuUtility), nameof(FloatMenuUtility.GetRangedAttackAction))]
    public static class Patch_FloatMenuUtility_GetRangedAttackAction_RimKata
    {
        public static bool Prefix(
            Pawn pawn,
            LocalTargetInfo target,
            ref System.Action __result,
            ref string failStr,
            ref bool __state)
        {
            bool activeRimKataAttack =
                RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    && RimKataEligibility.CanBeginGunKataAttack(pawn);
            __state = activeRimKataAttack
                && !RimKataAttackGizmoTargetContext.Active
                && IsSelectedPlayerPawnInGroup(pawn);
            if (!activeRimKataAttack
                || RimKataAttackGizmoTargetContext.Active || __state)
            {
                return true;
            }

            if (pawn?.Drafted != true
                || pawn.IsBurning()
                || !target.IsValid
                || !target.HasThing
                || (target.Pawn != null
                    && !RimKataTargeting.IsPawnTargetStateValid(target.Pawn)))
            {
                return true;
            }

            Verb verb = RimKataWeaponSlotUtility.BestRangedCombatVerb(
                pawn,
                target.Thing);
            if (verb == null)
            {
                return true;
            }

            if (!RimKataDualWeaponController.CanUsePlayerWeaponCommand(
                    pawn,
                    verb)
                || !RimKataWeaponSlotUtility.CanWeaponAttackTargetWithoutRushing(
                    pawn,
                    verb.EquipmentSource as ThingWithComps,
                    target.Thing))
            {
                return true;
            }

            failStr = PlayerRangedAttackFailure(pawn, target);
            if (failStr.NullOrEmpty())
            {
                __result = delegate
                {
                    if (RimKataDualWeaponController.NotifyPlayerWeaponTarget(
                            pawn,
                            verb,
                            target.Thing,
                            true))
                    {
                        RimKataDualWeaponController
                            .BeginPlayerRangedCloseAttack(
                                pawn,
                                verb,
                                target.Thing);
                    }
                };
                return false;
            }

            failStr = failStr.CapitalizeFirst();
            __result = null;
            return false;
        }

        internal static string PlayerRangedAttackFailure(Pawn pawn, LocalTargetInfo target)
        {
            if (!pawn.IsColonistPlayerControlled && !pawn.IsColonyMech && !pawn.IsColonySubhumanPlayerControlled)
            {
                return "CannotOrderNonControlledLower".Translate();
            }
            else if (pawn.IsColonyMechPlayerControlled && !MechanitorUtility.InMechanitorCommandRange(pawn, target))
            {
                return "OutOfCommandRange".Translate();
            }
            else if (pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                return "IsIncapableOfViolenceLower".Translate(pawn.LabelShort, pawn);
            }
            else if (pawn == target.Thing)
            {
                return "CannotAttackSelf".Translate();
            }
            else if (target.Thing is Pawn sameFactionPawn
                && (pawn.InSameExtraFaction(sameFactionPawn, ExtraFactionType.HomeFaction) || pawn.InSameExtraFaction(sameFactionPawn, ExtraFactionType.MiniFaction)))
            {
                return "CannotAttackSameFactionMember".Translate();
            }
            else if (target.Thing is Pawn innocentAnimal
                && HistoryEventUtility.IsKillingInnocentAnimal(pawn, innocentAnimal)
                && !new HistoryEvent(HistoryEventDefOf.KilledInnocentAnimal, pawn.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo())
            {
                return "IdeoligionForbids".Translate();
            }
            else if (target.Thing is Pawn veneratedAnimal
                && pawn.Ideo != null
                && pawn.Ideo.IsVeneratedAnimal(veneratedAnimal)
                && !new HistoryEvent(HistoryEventDefOf.HuntedVeneratedAnimal, pawn.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo())
            {
                return "IdeoligionForbids".Translate();
            }

            return string.Empty;
        }

        public static void Postfix(
            Pawn pawn,
            LocalTargetInfo target,
            ref System.Action __result,
            ref string failStr,
            bool __state)
        {
            bool activeRimKataAttack =
                RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    && RimKataEligibility.CanBeginGunKataAttack(pawn);
            bool squadContext = (activeRimKataAttack
                    && RimKataAttackGizmoTargetContext.SquadActive)
                || __state;
            bool explicitIncapacitatedOrder = pawn?.Drafted == true
                && activeRimKataAttack
                && RimKataTargeting.IsIncapacitatedTarget(target.Pawn);
            bool attackContext = (activeRimKataAttack
                    && RimKataAttackGizmoTargetContext.Active)
                || __state
                || explicitIncapacitatedOrder;
            if (__result == null
                && attackContext
                && target.HasThing
                && failStr == "TooClose".Translate().CapitalizeFirst())
            {
                Func<KeyValuePair<Action, string>> meleeActionFactory =
                    delegate
                    {
                        Action action = FloatMenuUtility.GetMeleeAttackAction(
                            pawn,
                            target,
                            out string meleeFailStr,
                            false);
                        return new KeyValuePair<Action, string>(
                            action,
                            meleeFailStr);
                    };
                KeyValuePair<Action, string> meleeResult = squadContext
                    ? RimKataAttackGizmoTargetContext.InvokeSquad(
                        meleeActionFactory)
                    : RimKataAttackGizmoTargetContext.Invoke(
                        meleeActionFactory);
                __result = meleeResult.Key;
                failStr = meleeResult.Value;
            }

            if (__result == null || !squadContext)
            {
                return;
            }

            Action originalAction = __result;
            __result = delegate
            {
                if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn))
                {
                    originalAction();
                    return;
                }

                if (pawn?.Drafted == true)
                {
                    RimKataAttackGizmoTargetContext.InvokeSquad(
                        originalAction);
                }
            };
        }

        internal static bool IsSelectedPlayerPawnInGroup(Pawn pawn)
        {
            List<object> selectedObjects = Find.Selector?
                .SelectedObjectsListForReading;
            if (pawn == null
                || selectedObjects == null)
            {
                return false;
            }

            bool pawnSelected = false;
            int selectedPlayerPawns = 0;
            for (int i = 0; i < selectedObjects.Count; i++)
            {
                if (!(selectedObjects[i] is Pawn selectedPawn))
                {
                    continue;
                }

                if (selectedPawn == pawn)
                {
                    pawnSelected = true;
                }

                if (selectedPawn?.Spawned == true
                    && selectedPawn.IsPlayerControlled)
                {
                    selectedPlayerPawns++;
                    if (pawnSelected && selectedPlayerPawns >= 2)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_ConfigurableHostilityResponse_RimKata
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(JobGiver_ConfigurableHostilityResponse), "TryGetAttackNearbyEnemyJob");
        }

        public static void Postfix(
            Pawn pawn,
            ref Job __result)
        {
            if (PreferVanillaCloseMeleeThreat(pawn, ref __result))
            {
                return;
            }

            if (RejectNonAutomaticMeleeThreat(pawn, ref __result))
            {
                return;
            }

            if (pawn?.Drafted == true
                || pawn == null
                || pawn.IsBurning()
                || !RimKataEligibility.RandomAttackEnabledForPawn(pawn)
                || (__result?.def != JobDefOf.AttackStatic && __result?.def != JobDefOf.AttackMelee)
                || !__result.targetA.HasThing
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return;
            }

            Thing target = __result.targetA.Thing;

            if (target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || !RimKataTargeting.IsAutomaticEnemy(pawn, target))
            {
                return;
            }
            if (__result.def == JobDefOf.AttackMelee)
            {
                return;
            }

            Verb rangedVerb =
                RimKataWeaponSlotUtility.BestRangedCombatVerb(pawn, target);
            if (rangedVerb != null)
            {
                __result.verbToUse = rangedVerb;
            }
        }

        private static bool PreferVanillaCloseMeleeThreat(
            Pawn pawn,
            ref Job job)
        {
            Thing threat = pawn?.mindState?.meleeThreat;
            if (pawn?.Map == null
                || !RimKataEligibility.HasActiveRimKataAccess(pawn)
                || RimKataDualWeaponController.CounterattackControlEnabled(pawn)
                || pawn.Drafted
                || pawn.InMentalState
                || pawn.IsBurning()
                || pawn.WorkTagIsDisabled(WorkTags.Violent)
                || PawnUtility.PlayerForcedJobNowOrSoon(pawn)
                || job == null
                || (job.def != JobDefOf.AttackStatic
                    && job.def != JobDefOf.AttackMelee)
                || job.playerForced
                || threat == null
                || threat.Destroyed
                || !threat.Spawned
                || threat.Map != pawn.Map
                || !pawn.mindState.MeleeThreatStillThreat
                || !RimKataTargeting.IsAutomaticEnemy(pawn, threat)
                || !pawn.CanReachImmediate(threat, PathEndMode.Touch))
            {
                return false;
            }

            Job currentJob = pawn.CurJob;
            if (currentJob?.def == JobDefOf.AttackMelee
                && currentJob.targetA.Thing == threat
                && !currentJob.playerForced)
            {
                if (job != currentJob)
                {
                    JobMaker.ReturnToPool(job);
                }

                job = null;
                return true;
            }

            if (job.def == JobDefOf.AttackMelee
                && job.targetA.Thing == threat)
            {
                return true;
            }

            Job previousJob = job;
            job = JobMaker.MakeJob(JobDefOf.AttackMelee, threat);
            JobMaker.ReturnToPool(previousJob);
            return true;
        }

        private static bool RejectNonAutomaticMeleeThreat(
            Pawn pawn,
            ref Job job)
        {
            // Nonhostile threat cleanup is a safety boundary, not permission
            // to start RimKata combat. Keep it active while stunned or unarmed.
            if (pawn == null
                || job?.def != JobDefOf.AttackMelee
                || job.playerForced
                || !job.targetA.HasThing
                || !RimKataEligibility.HasRimKataAccess(pawn)
                || RimKataTargeting.IsAutomaticEnemy(
                    pawn,
                    job.targetA.Thing))
            {
                return false;
            }

            ClearRejectedMeleeThreat(pawn, job.targetA.Thing);
            job = null;
            return true;
        }

        internal static void ClearRejectedMeleeThreat(
            Pawn pawn,
            Thing target)
        {
            if (pawn?.mindState?.meleeThreat == target)
            {
                pawn.mindState.meleeThreat = null;
            }
        }
    }

    [HarmonyPatch(typeof(JobGiver_ReactToCloseMeleeThreat), "TryGiveJob")]
    public static class Patch_JobGiverReactToCloseMeleeThreat_RimKata
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (pawn == null)
            {
                return;
            }

            Thing meleeThreat = pawn.mindState?.meleeThreat;
            if (__result == null && meleeThreat == null)
            {
                return;
            }

            // Match the stable-access boundary used by the companion filter.
            if (!RimKataEligibility.HasRimKataAccess(pawn))
            {
                return;
            }

            Thing threat = __result?.targetA.Thing
                ?? meleeThreat;
            if (threat == null
                || RimKataTargeting.IsAutomaticEnemy(pawn, threat))
            {
                return;
            }

            Patch_ConfigurableHostilityResponse_RimKata
                .ClearRejectedMeleeThreat(pawn, threat);
            if (__result?.def == JobDefOf.AttackMelee
                && !__result.playerForced)
            {
                __result = null;
            }
        }
    }

    [HarmonyPatch(
        typeof(Pawn),
        nameof(Pawn.TryGetAttackVerb),
        new Type[]
        {
            typeof(Thing),
            typeof(bool),
            typeof(bool)
        })]
    public static class Patch_Pawn_TryGetAttackVerb_RimKataPairRange
    {
        public static void Postfix(
            Pawn __instance,
            Thing __0,
            ref Verb __result)
        {
            if (__0 == null
                && RimKataGunReadyDrawUtility.IsDrawingEquipmentFor(__instance))
            {
                return;
            }

            if (__instance?.Map == null
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(__instance)
                || !RimKataEligibility.CanBeginGunKataAttack(__instance))
            {
                return;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(
                __instance);
            if (!RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    __instance,
                    primary,
                    true))
            {
                return;
            }

            ThingWithComps secondary = RimKataWeaponSlotUtility
                .SecondaryWeaponWithVerifiedAccess(__instance);
            ThingWithComps resultWeapon = __result?.EquipmentSource as ThingWithComps;
            if (__result != null
                && resultWeapon != primary
                && resultWeapon != secondary)
            {
                return;
            }

            bool targetAdjacent = __0 != null
                && __instance.CanReachImmediate(__0, PathEndMode.Touch);
            if (targetAdjacent)
            {
                return;
            }

            Verb pairVerb = RimKataWeaponSlotUtility.BestRangedCombatVerb(
                __instance,
                __0,
                primary,
                secondary,
                __0 != null ? (bool?)false : null);
            if (pairVerb != null)
            {
                __result = pairVerb;
            }
        }
    }
}
