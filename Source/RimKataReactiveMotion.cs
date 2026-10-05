using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal enum RimKataReactiveKind { None, Sliding, StandingUp, ShakeOff }

    public sealed class RimKataReactiveMotionState : IExposable
    {
        internal Pawn pawn;
        internal RimKataPawnCombatState combat;
        internal RimKataReactiveKind kind;
        internal int startTick, nextHit, ownerJobId;
        internal IntVec3 origin, destination, direction;
        internal Vector3 startDrawPos;
        internal Rot4 facing;
        internal float startAngle, primaryAngle, secondaryAngle;
        internal int turnSign;
        internal ThingWithComps primaryWeapon, secondaryWeapon;
        internal Thing primaryTarget, secondaryTarget, originalTarget;
        internal Thing primaryContinuationTarget, secondaryContinuationTarget;
        internal bool continuationSearchRequested;
        internal bool originalTargetSecondary;
        internal RimKataNativeAttack primaryAttack, secondaryAttack;
        internal Thing primaryPendingTarget, secondaryPendingTarget;
        internal int primaryDueTick = -1, secondaryDueTick = -1;
        internal bool primaryMelee, secondaryMelee, resumeMoving;
        internal LocalTargetInfo resumeDestination;
        internal PathEndMode resumeMode;
        internal bool BlocksCombat => kind == RimKataReactiveKind.Sliding || kind == RimKataReactiveKind.ShakeOff;
        internal bool HasPendingAttack => primaryPendingTarget != null || secondaryPendingTarget != null;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref primaryWeapon, "primaryWeapon");
            Scribe_References.Look(ref secondaryWeapon, "secondaryWeapon");
            Scribe_References.Look(ref primaryTarget, "primaryTarget");
            Scribe_References.Look(ref secondaryTarget, "secondaryTarget");
            Scribe_References.Look(ref originalTarget, "originalTarget");
            Scribe_References.Look(ref primaryContinuationTarget, "primaryContinuationTarget");
            Scribe_References.Look(ref secondaryContinuationTarget, "secondaryContinuationTarget");
            Scribe_Values.Look(ref continuationSearchRequested, "continuationSearchRequested");
            Scribe_Values.Look(ref originalTargetSecondary, "originalTargetSecondary");
            Scribe_References.Look(ref primaryPendingTarget, "primaryPendingTarget");
            Scribe_References.Look(ref secondaryPendingTarget, "secondaryPendingTarget");
            Scribe_Values.Look(ref primaryDueTick, "primaryDueTick", -1);
            Scribe_Values.Look(ref secondaryDueTick, "secondaryDueTick", -1);
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref startTick, "startTick");
            Scribe_Values.Look(ref nextHit, "nextHit");
            Scribe_Values.Look(ref ownerJobId, "ownerJobId", -1);
            Scribe_Values.Look(ref origin, "origin");
            Scribe_Values.Look(ref destination, "destination");
            Scribe_Values.Look(ref direction, "direction");
            Scribe_Values.Look(ref startDrawPos, "startDrawPos");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref startAngle, "startAngle");
            Scribe_Values.Look(ref primaryAngle, "primaryAngle");
            Scribe_Values.Look(ref secondaryAngle, "secondaryAngle");
            Scribe_Values.Look(ref turnSign, "turnSign", 1);
            Scribe_Values.Look(ref primaryMelee, "primaryMelee");
            Scribe_Values.Look(ref secondaryMelee, "secondaryMelee");
            Scribe_Values.Look(ref resumeMoving, "resumeMoving");
            Scribe_TargetInfo.Look(ref resumeDestination, "resumeDestination");
            Scribe_Values.Look(ref resumeMode, "resumeMode");
        }
    }

    public sealed class RimKataReactiveRegistry : GameComponent
    {
        private readonly Game game;
        internal readonly Dictionary<Pawn, RimKataReactiveMotionState> states = new Dictionary<Pawn, RimKataReactiveMotionState>();
        private readonly List<RimKataReactiveMotionState> scratch = new List<RimKataReactiveMotionState>();
        private List<RimKataReactiveMotionState> saved;
        internal bool IsCurrent => ReferenceEquals(game, Current.Game);
        public RimKataReactiveRegistry(Game game) { this.game = game; RimKataReactiveMotion.SetRegistry(this); }
        public override void GameComponentTick()
        {
            if (states.Count == 0) return;
            scratch.Clear();
            scratch.AddRange(states.Values);
            foreach (var state in scratch)
                if (states.TryGetValue(state.pawn, out var live) && live == state)
                    RimKataReactiveMotion.Tick(state);
            scratch.Clear();
        }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) saved = new List<RimKataReactiveMotionState>(states.Values);
            Scribe_Collections.Look(ref saved, "reactiveMotions", LookMode.Deep);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            states.Clear();
            if (saved != null) foreach (var state in saved)
                if (state?.pawn != null) states[state.pawn] = state;
            saved = null;
        }
        public override void FinalizeInit()
        {
            RimKataReactiveMotion.SetRegistry(this);
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                if (!IsCurrent) return;
                foreach (var state in new List<RimKataReactiveMotionState>(states.Values))
                {
                    state.combat = RimKataReactiveMotion.ExistingCombat(state.pawn);
                    if (state.combat == null)
                    { RimKataReactiveMotion.Remove(state.pawn, false); continue; }
                    state.combat.reactiveMotion = state;
                    if (!RimKataReactiveMotion.Valid(state))
                    { RimKataReactiveMotion.Remove(state.pawn, false); continue; }
                    RimKataMotionJobGate.Refresh(state.combat);
                    if (state.BlocksCombat) state.pawn.pather?.StopDead();
                    RimKataReactiveMotion.RestorePendingAttacks(state);
                    RimKataReactiveRender.Publish(state);
                }
            });
        }
    }

    internal static class RimKataReactiveMotion
    {
        internal static readonly IntVec3[] Directions = { IntVec3.North, IntVec3.NorthEast, IntVec3.East,
            IntVec3.SouthEast, IntVec3.South, IntVec3.SouthWest, IntVec3.West, IntVec3.NorthWest };
        private static RimKataReactiveRegistry registry;
        internal static int AttackStateVersion { get; private set; }
        internal static bool Any => registry?.IsCurrent == true && registry.states.Count != 0;
        private static readonly AccessTools.FieldRef<StaggerHandler, int> StaggerTicks =
            AccessTools.FieldRefAccess<StaggerHandler, int>("staggerTicksLeft");
        private static readonly AccessTools.FieldRef<StaggerHandler, float> StaggerSpeed =
            AccessTools.FieldRefAccess<StaggerHandler, float>("staggerMoveSpeedFactor");
        private static readonly AccessTools.FieldRef<Pawn_PathFollower, PathEndMode> PathMode =
            AccessTools.FieldRefAccess<Pawn_PathFollower, PathEndMode>("peMode");
        internal static void SetRegistry(RimKataReactiveRegistry value)
        {
            if (registry == value) return;
            if (registry != null) foreach (var state in new List<RimKataReactiveMotionState>(registry.states.Values))
            {
                CancelAttacks(state);
                if (state.combat != null) state.combat.reactiveMotion = null;
                RimKataMotionJobGate.Clear(state.combat);
                RimKataReactiveRender.Remove(state.pawn);
            }
            registry = value;
            AttackStateVersion++;
        }
        internal static RimKataReactiveMotionState Get(Pawn pawn)
            => pawn != null && registry?.IsCurrent == true && registry.states.Count != 0
                && registry.states.TryGetValue(pawn, out var state) ? state : null;
        internal static RimKataReactiveMotionState Participant(Pawn pawn)
        {
            RimKataReactiveMotionState state = Get(pawn);
            if (state == null) return null;
            // Damage and Job callbacks can invalidate participants between component ticks.
            if (!Valid(state))
            {
                Remove(pawn, false);
                return null;
            }
            return state;
        }
        internal static RimKataPawnCombatState ExistingCombat(Pawn pawn)
            => pawn != null && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                && RimKataCombatStatePresenceCache.TryGetOwner(pawn, out var owner)
                    ? owner.GetState(pawn, false) : null;
        internal static bool IsFighting(RimKataPawnCombatState state)
            => state?.pawn != null
                && (RimKataDualWeaponController.IsActualCombatActive(
                        state.pawn, state.pawn.CurJobDef, state)
                    || state.huntingSession != null && state.WeaponCyclesActive);
        internal static bool BlocksCombat(RimKataPawnCombatState state)
            => state?.reactiveMotion is RimKataReactiveMotionState motion
                && (motion.BlocksCombat || motion.HasPendingAttack);
        internal static bool Protected(Pawn pawn) => Participant(pawn)?.BlocksCombat == true;
        internal static bool Valid(RimKataReactiveMotionState state)
            => state.pawn?.Spawned == true && !state.pawn.Dead && !state.pawn.Downed
                && state.combat?.reactiveMotion == state
                && state.pawn.CurJob?.loadID == state.ownerJobId
                && RimKataEligibilityCache.IsCachedQualifiedPawn(state.pawn)
                && !RimKataTemporaryInactivity.IsInactive(state.pawn);

        internal static void StaggerApplied(Pawn pawn)
        {
            var combat = ExistingCombat(pawn);
            if (!IsFighting(combat) || pawn.Dead || pawn.Downed
                || RimKataTemporaryInactivity.IsInactive(pawn) || BlocksCombat(combat)
                || RimKataReactiveMovement.entering == pawn
                || RimKataBreachUtility.Get(pawn) != null || RimKataSubdueUtility.IsHolding(pawn)
                || combat.DodgeVisualLocked) return;
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(pawn);
            if (settings == null || !settings.slidingEnabled && !settings.shakeOffEnabled) return;
            Thing target = CurrentTarget(combat);
            bool sliding = settings.slidingEnabled && Rand.Chance(settings.SlidingChance);
            if (sliding)
            {
                IntVec3 forward;
                if (!RimKataDodgeMovementUtility.TryGetCurrentMovementDirection(pawn, out forward))
                    forward = Directions[Sector(target?.Spawned == true ? (target.Position - pawn.Position).AngleFlat : pawn.Rotation.AsAngle)];
                if (RimKataDodgeMovementUtility.TryChooseDestination(pawn, forward, out var destination)
                    && BeginSlide(pawn, combat, target, destination))
                {
                    combat.shakeOffPending = false;
                    StaggerTicks(pawn.stances.stagger) = 0;
                    StaggerSpeed(pawn.stances.stagger) = StaggerHandler.DefaultStaggerMoveSpeedFactor;
                    if (combat.kick != null) RimKataKick.StaggerEnded(combat);
                    return;
                }
            }
            combat.shakeOffPending = settings.shakeOffEnabled;
        }

        internal static void AttackStarting(RimKataNativeAttack request)
        {
            RimKataPawnCombatState combat = request.state;
            if (request.reactiveMotion != null || combat == null || !combat.shakeOffPending) return;
            if (!RimKataReactiveAttack.HasAmmo(request.verb, request.weapon)) return;
            combat.shakeOffPending = false;
            Pawn pawn = combat.pawn;
            if (!IsFighting(combat) || pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || pawn.stances?.stagger?.Staggered != true || BlocksCombat(combat)
                || RimKataTemporaryInactivity.IsInactive(pawn)) return;
            var settings = RimKataTargetAccess.SettingsFor(pawn);
            if (settings?.shakeOffEnabled != true || !Rand.Chance(settings.ShakeOffChance)) return;
            if (RimKataSubdueUtility.IsHolding(pawn) || RimKataBreachUtility.Get(pawn) != null) return;
            Thing target = request.firedTarget;
            bool secondary = request.cycle == combat.secondaryWeaponCycle;
            var state = Create(pawn, combat, target);
            state.kind = RimKataReactiveKind.ShakeOff;
            state.startAngle = Sector(target?.Spawned == true ? (target.Position - pawn.Position).AngleFlat : pawn.Rotation.AsAngle) * 45f;
            if (secondary) state.startAngle -= 180f;
            state.originalTargetSecondary = secondary;
            state.turnSign = Rand.Bool ? 1 : -1;
            state.nextHit = 1;
            if (secondary) state.secondaryAttack = request;
            else state.primaryAttack = request;
            request.BeginReactiveAttack(state);
            SetPending(state, secondary, target, state.startTick);
            Install(state, request);
            if (Get(pawn) != state || !Valid(state))
            {
                request.Cancel();
                if (Get(pawn) == state) Remove(pawn, false);
                return;
            }
            StaggerTicks(pawn.stances.stagger) = 0;
            StaggerSpeed(pawn.stances.stagger) = StaggerHandler.DefaultStaggerMoveSpeedFactor;
            if (combat.kick != null) RimKataKick.StaggerEnded(combat);
            int opposite = (Sector(state.startAngle) + (secondary ? 0 : 4)) & 7;
            Thing otherTarget = (secondary ? state.primaryWeapon : state.secondaryWeapon) != null
                ? EnemyAt(state, pawn.Position + Directions[opposite]) : null;
            if (secondary) state.primaryTarget = otherTarget;
            else state.secondaryTarget = otherTarget;
            UpdateAngles(state, 0);
            RimKataReactiveRender.Publish(state);
            QueueHit(state, !secondary, state.startTick);
        }

        private static Thing CurrentTarget(RimKataPawnCombatState combat)
        {
            if (RimKataDualWeaponController.TryGetNextAim(
                    combat.pawn, combat, out _, out LocalTargetInfo aim)
                && aim.HasThing)
                return aim.Thing;
            return combat.primaryWeaponCycle?.plannedTarget
                ?? combat.secondaryWeaponCycle?.plannedTarget
                ?? combat.primaryWeaponCycle?.lastFiredTarget
                ?? combat.secondaryWeaponCycle?.lastFiredTarget
                ?? combat.dualCloseTarget
                ?? combat.pawn.CurJob?.targetA.Thing;
        }
        internal static int Sector(float angle) => Mathf.RoundToInt(Mathf.Repeat(angle, 360f) / 45f) & 7;
        private static RimKataReactiveMotionState Create(Pawn pawn, RimKataPawnCombatState combat, Thing target)
        {
            var primary = combat.primaryWeaponCycle.weapon;
            var secondary = combat.secondaryWeaponCycle.weapon;
            var pather = pawn.pather;
            return new RimKataReactiveMotionState { pawn = pawn, combat = combat,
                ownerJobId = pawn.CurJob?.loadID ?? -1, startTick = Find.TickManager.TicksGame,
                origin = pawn.Position, destination = pawn.Position, startDrawPos = pawn.DrawPos,
                facing = pawn.Rotation, originalTarget = target, primaryTarget = target, secondaryTarget = target,
                primaryWeapon = primary, secondaryWeapon = secondary,
                primaryMelee = RimKataWeaponSlotUtility.CombatVerb(pawn, primary)?.IsMeleeAttack == true,
                secondaryMelee = RimKataWeaponSlotUtility.CombatVerb(pawn, secondary)?.IsMeleeAttack == true,
                resumeMoving = pather?.Moving == true, resumeDestination = pather?.Destination ?? LocalTargetInfo.Invalid,
                resumeMode = pather != null ? PathMode(pather) : PathEndMode.OnCell };
        }
        private static bool BeginSlide(Pawn pawn, RimKataPawnCombatState combat, Thing target, IntVec3 destination)
        {
            var previousMotion = combat.reactiveMotion;
            var state = Create(pawn, combat, target);
            state.kind = RimKataReactiveKind.Sliding;
            state.destination = destination;
            state.direction = destination - pawn.Position;
            state.startAngle = state.direction.AngleFlat;
            state.facing = Rot4.FromAngleFlat(state.startAngle);
            RimKataDodgeMovementUtility.AdjacentDirections(state.direction, out var left, out var right);
            Thing first = EnemyAt(state, state.origin + left), second = EnemyAt(state, state.origin + right);
            if (second == target) { Thing swap = first; first = second; second = swap; }
            state.primaryTarget = first ?? second ?? target;
            state.secondaryTarget = second ?? first ?? target;
            if (!RimKataReactiveMovement.Enter(pawn, destination))
            { Resume(state, false); return false; }
            // Native cell entry callbacks can down, despawn or retask the pawn.
            if (pawn.Spawned != true || pawn.Dead || pawn.Downed
                || pawn.CurJob?.loadID != state.ownerJobId
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || RimKataTemporaryInactivity.IsInactive(pawn)
                || ExistingCombat(pawn) != combat || combat.reactiveMotion != previousMotion)
            { Resume(state, false); return false; }
            Install(state);
            UpdateAngles(state, 0);
            RimKataReactiveRender.Publish(state);
            RimKataReactiveMovement.RefreshNearbyPursuers(pawn, state.origin);
            return true;
        }
        private static void Install(RimKataReactiveMotionState state, RimKataNativeAttack originatingRequest = null)
        {
            var combat = state.combat;
            if (combat.reactiveMotion != null) Remove(state.pawn, false);
            combat.reactiveMotion = state;
            registry.states[state.pawn] = state;
            RimKataMotionJobGate.Refresh(combat);
            AttackStateVersion++;
            combat.shakeOffPending = false;
            combat.primaryWeaponCycle.openingWarmupPending = false;
            combat.secondaryWeaponCycle.openingWarmupPending = false;
            if (originatingRequest?.cycle != combat.primaryWeaponCycle) combat.primaryWeaponCycle.ClearPlan();
            if (originatingRequest?.cycle != combat.secondaryWeaponCycle) combat.secondaryWeaponCycle.ClearPlan();
            RimKataGroundPoseUtility.Clear(combat);
            state.pawn.pather?.StopDead();
        }
        private static Thing EnemyAt(RimKataReactiveMotionState state, IntVec3 cell)
        {
            if (!cell.InBounds(state.pawn.Map)) return null;
            var things = cell.GetThingList(state.pawn.Map);
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is Pawn enemy) || enemy == state.pawn
                    || !RimKataTargeting.IsPawnTargetStateValid(enemy)
                    || !RimKataTargeting.IsAutomaticEnemy(state.pawn, enemy)) continue;
                if (state.kind == RimKataReactiveKind.ShakeOff
                    || state.combat.primaryWeaponCycle.ContainsAutomaticCandidate(enemy)
                    || state.combat.secondaryWeaponCycle.ContainsAutomaticCandidate(enemy)
                    || enemy == state.originalTarget
                    || RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(state.pawn, state.combat, enemy, true)) return enemy;
            }
            return null;
        }

        internal static void PrepareShakeOffHits(RimKataReactiveMotionState state)
        {
            int tick = Find.TickManager.TicksGame;
            if (state.kind != RimKataReactiveKind.ShakeOff || state.nextHit > 8
                || tick < state.startTick + state.nextHit * 3) return;
            if (!Valid(state)) { Remove(state.pawn, false); return; }
            if (RimKataTargetAccess.SettingsFor(state.pawn)?.shakeOffEnabled != true)
            { Remove(state.pawn, true); return; }
            int index = state.nextHit++;
            int sector = (Sector(state.startAngle) + state.turnSign * index + 16) & 7;
            state.primaryTarget = state.primaryWeapon != null
                ? EnemyAt(state, state.pawn.Position + Directions[sector]) : null;
            state.secondaryTarget = state.secondaryWeapon != null
                ? EnemyAt(state, state.pawn.Position + Directions[(sector + 4) & 7]) : null;
            if (index == 8 && LiveTarget(state, state.originalTarget))
            {
                if (state.originalTargetSecondary) state.secondaryTarget = state.originalTarget;
                else state.primaryTarget = state.originalTarget;
            }
            QueueHit(state, false, tick);
            if (Get(state.pawn) != state) return;
            QueueHit(state, true, tick);
        }

        internal static void Tick(RimKataReactiveMotionState state)
        {
            if (!Valid(state)) { Remove(state.pawn, false); return; }
            var settings = RimKataTargetAccess.SettingsFor(state.pawn);
            if (settings == null || (state.kind == RimKataReactiveKind.ShakeOff || state.kind == RimKataReactiveKind.None
                ? !settings.shakeOffEnabled : !settings.slidingEnabled))
            { Remove(state.pawn, true); return; }
            if (state.BlocksCombat || state.HasPendingAttack)
            {
                // The last special hit starts recovery while the ordinary controller remains suspended.
                state.combat.primaryWeaponCycle.TickTimers();
                state.combat.secondaryWeaponCycle.TickTimers();
            }
            int elapsed = Math.Max(0, Find.TickManager.TicksGame - state.startTick);
            if (state.kind == RimKataReactiveKind.Sliding)
            {
                if (elapsed >= 5 && state.nextHit == 0)
                {
                    state.nextHit = 1;
                    QueueHit(state, false, state.startTick + 6);
                    if (Get(state.pawn) != state) return;
                    QueueHit(state, true, state.startTick + 6);
                }
                if (Get(state.pawn) != state) return;
                if (elapsed >= 12)
                {
                    state.kind = RimKataReactiveKind.StandingUp;
                    AttackStateVersion++;
                    state.startTick += 12;
                    RequestContinuationSearch(state);
                    Resume(state);
                    // StartPath can synchronously end the old Job and remove this participant.
                    if (Get(state.pawn) != state) return;
                    if (!Valid(state)) { Remove(state.pawn, false); return; }
                    elapsed -= 12;
                }
            }
            else if (state.kind == RimKataReactiveKind.ShakeOff)
            {
                if (elapsed >= 24)
                {
                    if (!state.HasPendingAttack) { Complete(state); return; }
                    state.kind = RimKataReactiveKind.None;
                    AttackStateVersion++;
                    RimKataReactiveRender.Remove(state.pawn);
                    return;
                }
            }
            if (state.kind == RimKataReactiveKind.None)
            {
                if (!state.HasPendingAttack) Complete(state);
                return;
            }
            if (state.kind == RimKataReactiveKind.StandingUp && elapsed >= 6)
            { Remove(state.pawn, false); return; }
            UpdateAngles(state, elapsed);
            RimKataReactiveRender.Publish(state);
        }
        private static bool LiveTarget(RimKataReactiveMotionState state, Thing target)
            => target is Pawn victim && victim.Spawned && victim.Map == state.pawn.Map
                && RimKataTargeting.IsPawnTargetStateValid(victim);
        private static void QueueHit(RimKataReactiveMotionState state, bool secondary, int dueTick)
        {
            Thing target = secondary ? state.secondaryTarget : state.primaryTarget;
            if (!LiveTarget(state, target)
                || (secondary ? state.secondaryPendingTarget : state.primaryPendingTarget) != null) return;
            SetPending(state, secondary, target, dueTick);
            if (!RimKataReactiveAttack.Queue(state, secondary, target, dueTick))
                SetPending(state, secondary, null, -1);
        }

        private static void SetPending(RimKataReactiveMotionState state, bool secondary, Thing target, int dueTick)
        {
            if (secondary)
            {
                if (state.secondaryPendingTarget == target && state.secondaryDueTick == dueTick) return;
                state.secondaryPendingTarget = target;
                state.secondaryDueTick = dueTick;
            }
            else
            {
                if (state.primaryPendingTarget == target && state.primaryDueTick == dueTick) return;
                state.primaryPendingTarget = target;
                state.primaryDueTick = dueTick;
            }
            AttackStateVersion++;
        }

        internal static void RestorePendingAttacks(RimKataReactiveMotionState state)
        {
            if (state.primaryPendingTarget != null
                && !RimKataReactiveAttack.Queue(state, false, state.primaryPendingTarget, state.primaryDueTick))
                SetPending(state, false, null, -1);
            if (state.secondaryPendingTarget != null
                && !RimKataReactiveAttack.Queue(state, true, state.secondaryPendingTarget, state.secondaryDueTick))
                SetPending(state, true, null, -1);
        }

        internal static void AttackCompleted(RimKataNativeAttack request)
        {
            RimKataReactiveMotionState state = request.reactiveMotion;
            if (!request.HasFired || request.Cancelled || Get(state.pawn) != state || !Valid(state)) return;
            RimKataWeaponCycleState cycle = request.cycle;
            if (cycle.weapon != request.weapon
                || RimKataWeaponSlotUtility.CombatVerb(state.pawn, request.weapon) != request.cycleVerb) return;
            cycle.ClearPlan();
            cycle.cooldownTicksRemaining = RimKataCombatMath.CooldownTicksForSingleShot(request.verb, state.pawn, false);
            cycle.rangedCooldown = !request.verb.IsMeleeAttack;
            cycle.lastFiredTarget = request.firedTarget;
            cycle.visualTarget = request.firedTarget;
            cycle.visualAimTicksRemaining = cycle.cooldownTicksRemaining;
            cycle.cooldownTurnTarget = null;
            cycle.cooldownTurnTicks = 0;
            cycle.StampNativeActionTick();
            if (state.kind == RimKataReactiveKind.Sliding || state.kind == RimKataReactiveKind.StandingUp
                || request.notBeforeTick >= state.startTick + 24)
            {
                if (cycle == state.combat.primaryWeaponCycle) state.primaryContinuationTarget = request.firedTarget;
                else state.secondaryContinuationTarget = request.firedTarget;
                RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(state.pawn, state.combat, request.firedTarget);
                RequestContinuationSearch(state);
            }
        }

        private static void RequestContinuationSearch(RimKataReactiveMotionState state)
        {
            if (state.continuationSearchRequested) return;
            state.continuationSearchRequested = true;
            RimKataSharedTargetSearch.Begin(state.pawn, state.combat, state.pawn.Position);
        }

        private static void Complete(RimKataReactiveMotionState state)
        {
            RequestContinuationSearch(state);
            Remove(state.pawn, true);
        }

        internal static void ReleaseAttack(RimKataNativeAttack request)
        {
            RimKataReactiveMotionState state = request.reactiveMotion;
            bool secondary = state.secondaryAttack == request;
            SetPending(state, secondary, null, -1);
            if (request.reactiveOpeningAttack)
            {
                if (secondary) state.secondaryAttack = null;
                else state.primaryAttack = null;
                if ((!request.HasFired || request.Cancelled) && Get(state.pawn) == state)
                {
                    request.cycle.ClearPlan();
                    Remove(state.pawn, Valid(state));
                }
            }
        }
        private static void UpdateAngles(RimKataReactiveMotionState state, int elapsed)
        {
            if (state.kind == RimKataReactiveKind.ShakeOff)
            {
                state.primaryAngle = state.startAngle + state.turnSign * 360f * Mathf.Clamp01(elapsed / 24f);
                state.secondaryAngle = state.primaryAngle + 180f;
            }
            else if (state.kind == RimKataReactiveKind.Sliding)
            {
                float sweep = state.startAngle + Mathf.Lerp(30f, 150f, Mathf.Clamp01(elapsed / 12f));
                Vector3 center = Vector3.Lerp(state.startDrawPos, state.destination.ToVector3Shifted(), Mathf.Clamp01(elapsed / 12f));
                state.primaryAngle = state.primaryMelee ? sweep : Aim(state.primaryTarget, center, state.startAngle);
                state.secondaryAngle = state.secondaryMelee ? sweep + (state.secondaryTarget != state.primaryTarget ? 180f : 0f)
                    : Aim(state.secondaryTarget, center, state.startAngle);
            }
        }
        private static float Aim(Thing target, Vector3 center, float fallback)
            => target?.Spawned == true ? (target.DrawPos - center).AngleFlat() : fallback;
        internal static void Remove(Pawn pawn, bool resume = false)
        {
            var state = Get(pawn);
            if (state == null) return;
            registry.states.Remove(pawn);
            if (state.combat?.reactiveMotion == state) state.combat.reactiveMotion = null;
            RimKataMotionJobGate.Refresh(state.combat);
            AttackStateVersion++;
            CancelAttacks(state);
            RimKataReactiveRender.Remove(pawn);
            if (resume) Resume(state);
        }
        private static void CancelAttacks(RimKataReactiveMotionState state)
        {
            if (state.primaryAttack?.reactiveMotion == state) state.primaryAttack.Cancel();
            if (state.secondaryAttack?.reactiveMotion == state) state.secondaryAttack.Cancel();
        }
        private static void Resume(RimKataReactiveMotionState state, bool allowCombatHandoff = true)
        {
            bool resumeMoving = state.resumeMoving;
            state.resumeMoving = false;
            Pawn pawn = state.pawn;
            if (!pawn.Spawned || pawn.Dead || pawn.Downed
                || pawn.CurJob?.loadID != state.ownerJobId) return;
            if (pawn.CurJobDef == RimKataDefOf.RimKata_Attack)
            {
                if (allowCombatHandoff)
                    RimKataReactiveMovement.RetargetUnreachableAttack(pawn, state.combat,
                        state.primaryContinuationTarget, state.secondaryContinuationTarget);
                return;
            }
            if (resumeMoving && state.resumeDestination.IsValid
                && !state.resumeDestination.ThingDestroyed)
                pawn.pather.StartPath(state.resumeDestination, state.resumeMode);
        }
    }
}
