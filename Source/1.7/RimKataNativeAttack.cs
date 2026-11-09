using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal sealed class RimKataNativeAttack
    {
        private sealed class VerbBinding
        {
            internal RimKataNativeAttack request;
            internal OriginalExecution original;
            internal bool cancelAfterLoad;
            internal bool interruptedBurstAfterLoad;
            internal bool resettingAfterLoad;
        }

        // Keep the original stance object and its own StanceTick, rather than
        // inventing a completion timer or borrowing the other weapon's cooldown.
        internal sealed class OriginalExecution
        {
            internal Game game;
            internal Verb verb;
            internal Pawn pawn;
            internal Job job;
            internal RimKataWeaponCycleState cycle;
            internal Stance stance;
            internal Stance_Mobile mobile;
            internal int lastTick = -1;
            internal int warmupTotalTicks;
            internal bool cancelled;
        }

        internal struct OriginalScope
        {
            internal OriginalExecution execution;
            internal Stance previous;
        }

        private static readonly ConditionalWeakTable<Verb, VerbBinding> bindings =
            new ConditionalWeakTable<Verb, VerbBinding>();
        private static int reactiveRequestCount;
        private static Game originalGame;
        private static int originalCount;
        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> currentTarget =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentTarget");
        private static readonly AccessTools.FieldRef<Verb, LocalTargetInfo> currentDestination =
            AccessTools.FieldRefAccess<Verb, LocalTargetInfo>("currentDestination");
        private static readonly AccessTools.FieldRef<Verb, bool> surpriseAttack =
            AccessTools.FieldRefAccess<Verb, bool>("surpriseAttack");
        private static readonly AccessTools.FieldRef<Verb, bool> canHitNonTargetPawns =
            AccessTools.FieldRefAccess<Verb, bool>("canHitNonTargetPawnsNow");
        private static readonly AccessTools.FieldRef<Verb, bool> preventFriendlyFire =
            AccessTools.FieldRefAccess<Verb, bool>("preventFriendlyFire");
        private static readonly AccessTools.FieldRef<Verb, bool> nonInterruptingSelfCast =
            AccessTools.FieldRefAccess<Verb, bool>("nonInterruptingSelfCast");
        private static readonly AccessTools.FieldRef<Verb, int?> cachedBurstShotCount =
            AccessTools.FieldRefAccess<Verb, int?>("cachedBurstShotCount");
        private static readonly AccessTools.FieldRef<Verb, int> burstShotsLeft =
            AccessTools.FieldRefAccess<Verb, int>("burstShotsLeft");
        private static readonly AccessTools.FieldRef<Verb, Action> castCompleteCallback =
            AccessTools.FieldRefAccess<Verb, Action>("castCompleteCallback");

        internal Pawn pawn;
        internal RimKataPawnCombatState state;
        internal RimKataWeaponCycleState cycle;
        internal RimKataHuntingSession huntingSession;
        internal RimKataReactiveMotionState reactiveMotion;
        internal RimKataSubdueState subdueState;
        internal RimKataKickAttack kickAttack;
        internal bool reactiveOpeningAttack;
        internal int notBeforeTick;
        internal ThingWithComps weapon;
        internal Verb verb;
        internal Verb cycleVerb;
        internal bool closeFireMelee;
        internal Verb CooldownVerb => closeFireMelee ? cycleVerb : verb;
        internal Job job;
        internal Thing assignedTarget;
        internal Thing firedTarget;
        internal LocalTargetInfo target;
        internal bool playerForced;
        internal bool killIncappedTarget;
        internal bool targetSelectionLocked;
        internal bool closeCombatContext;
        internal bool allowAutomaticRangedFire;
        internal bool randomAttackEnabled;
        internal bool firedFromVanillaOpening;
        internal bool movingShot;
        internal bool closeShot;
        internal bool interceptionShot;
        internal bool closeMeleeResolution;
        internal bool closeMeleeHit;
        internal Thing interceptionTarget;
        internal RimKataCloseDefensePrecheck closeDefensePrecheck;
        internal bool Pending { get; private set; }
        internal bool Executing { get; private set; }
        internal bool Started { get; set; }
        internal bool HasFired { get; private set; }
        internal bool OriginalHandoff { get; private set; }
        internal bool BurstActive => Pending && Started && verb?.state == VerbState.Bursting;
        private bool cancelled;
        internal bool Cancelled => cancelled;
        private Verb bindingVerb;
        private VerbBinding nativeBinding;
        private RimKataFireContext.ScopeState previousContext;
        private RimKataWeaponEffectStats.Scope previousStats;
        private byte executionScopes;
        private const byte StatsScope = 1, FireScope = 2, SlidingScope = 4,
            FlyingScope = 8, KickScope = 16, SubdueScope = 32, DirectionScope = 64;
        private RimKataSlidingAttackOrigin.Scope previousSlidingOrigin;
        private RimKataFlyingKickRender.ShotScope previousFlyingKickOrigin;
        private RimKataKickRender.ShotScope previousKickOrigin;
        private Stance_RimKataAim previousAim;
        private Stance previousSpecialStance;
        private RimKataSubdueCombat.ExecutionScope previousSubdueExecution;
        private RimKataDirectionalFire.ExecutionScope previousDirectionalExecution;
        private Rot4 previousSubdueRotation;
        internal int extraAimTicks;
        private int? previousBurstShotCount;
        private bool singleShotOverride;
        private bool kickOpportunityNotified;
        internal RimKataWeaponExecutionPlan executionPlan;
        internal bool observedShotAttempted;
        private bool nativeCompleted;
        private Action previousCompletion;
        private Action completionCallback;

        internal static RimKataNativeAttack PendingRequest(Verb verb)
            => verb != null && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Pending == true ? binding.request : null;
        internal static bool HasPendingRequest(Verb verb) => PendingRequest(verb) != null;

        internal static bool IsOriginalCastBusy(Verb verb)
            => originalCount != 0 && originalGame == Current.Game
                && verb != null && bindings.TryGetValue(verb, out var binding)
                && binding.original is OriginalExecution execution && !execution.cancelled
                && (execution.stance is Stance_Busy || verb.state == VerbState.Bursting);

        internal static bool TryGetOriginalVisualData(Pawn pawn, RimKataWeaponCycleState cycle,
            out RimKataWeaponVisualData data)
        {
            data = default;
            Verb verb = cycle?.boundVerb;
            if (originalCount == 0 || originalGame != Current.Game || verb == null
                || !bindings.TryGetValue(verb, out var binding)
                || !(binding.original is OriginalExecution execution) || execution.cancelled
                || execution.pawn != pawn || execution.cycle != cycle
                || execution.job != pawn.CurJob
                || cycle.weapon != verb.EquipmentSource) return false;
            Stance_Busy busy = execution.stance as Stance_Busy;
            if (busy == null && verb.state != VerbState.Bursting) return false;
            bool warming = busy is Stance_Warmup;
            int warmupTicks = warming ? Math.Max(0, busy.ticksLeft) : 0;
            data = new RimKataWeaponVisualData
            {
                weapon = cycle.weapon,
                target = busy != null
                    ? busy.neverAimWeapon ? LocalTargetInfo.Invalid : busy.focusTarg
                    : verb.CurrentTarget,
                warming = warming,
                warmupTicksRemaining = warmupTicks,
                warmupTotalTicks = warming ? Math.Max(warmupTicks, execution.warmupTotalTicks) : 0,
                cooldownTicksRemaining = busy is Stance_Cooldown ? Math.Max(0, busy.ticksLeft) : 0,
                originalTiming = true
            };
            return true;
        }

        private static OriginalScope EnterOriginal(OriginalExecution execution)
        {
            var scope = new OriginalScope { execution = execution, previous = execution.pawn.stances.curStance };
            execution.pawn.stances.curStance = execution.stance ?? execution.mobile;
            return scope;
        }

        internal static void BeginOriginalTick(Verb verb, out OriginalScope scope)
        {
            scope = default;
            if (originalCount == 0 || originalGame != Current.Game) return;
            if (!bindings.TryGetValue(verb, out var binding) || binding.original == null) return;
            OriginalExecution execution = binding.original;
            if (execution.cancelled || execution.pawn?.Spawned != true
                || execution.pawn.Dead || execution.pawn.Downed || execution.pawn.InMentalState
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(execution.pawn)
                || execution.pawn.CurJob != execution.job
                || execution.cycle.weapon != verb.EquipmentSource)
            {
                try { ClearOriginal(verb, binding); verb.Reset(); }
                catch (Exception exception) { FailOriginalExecution(verb, execution, exception); }
                return;
            }
            // Preserve the native pause/reset rules; a detached warmup cannot bypass stun.
            if (execution.pawn.stances.stunner.Stunned) return;
            scope = EnterOriginal(execution);
            try
            {
                int tick = Find.TickManager.TicksGame;
                if (execution.lastTick != tick && execution.stance != null)
                {
                    execution.lastTick = tick;
                    execution.stance.StanceTick();
                }
            }
            catch (Exception exception)
            {
                FinishOriginalTick(verb, scope, exception);
                scope = default;
            }
        }

        internal static Exception FinishOriginalTick(Verb verb, OriginalScope scope, Exception exception)
        {
            OriginalExecution execution = scope.execution;
            if (execution == null) return exception;
            Pawn pawn = execution.pawn;
            Stance result = pawn.stances.curStance;
            bool ownStance = result is Stance_Busy busy && busy.verb == verb;
            if (result != execution.stance)
                execution.warmupTotalTicks = ownStance && result is Stance_Warmup warmup
                    ? Math.Max(0, warmup.ticksLeft) : 0;
            execution.stance = ownStance ? result : null;
            // A new dodge/stun/foreign stance owns itself; never overwrite it.
            if (ownStance || result is Stance_Mobile) pawn.stances.curStance = scope.previous;
            if (exception != null)
            {
                FailOriginalExecution(verb, execution, exception);
                return null;
            }
            if (execution.cancelled || (!ownStance && verb.state != VerbState.Bursting))
                if (bindings.TryGetValue(verb, out var completed) && completed.original == execution)
                    RemoveOriginal(completed);
            return null;
        }

        private static void FailOriginalExecution(Verb verb, OriginalExecution execution, Exception exception)
        {
            Thing target = verb.CurrentTarget.Thing;
            bool fallback = false;
            try { fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(verb, exception); }
            catch (Exception failure) { exception = new AggregateException(exception, failure); }
            try { if (bindings.TryGetValue(verb, out var failed)) ClearOriginal(verb, failed); }
            catch (Exception failure) { exception = new AggregateException(exception, failure); }
            try { verb.Reset(); }
            catch (Exception failure) { exception = new AggregateException(exception, failure); }
            RimKataDualWeaponController.NotifyWeaponExecutionFailed(execution.pawn, execution.cycle,
                verb, target, exception, fallback);
        }

        private static void ClearOriginal(Verb verb, VerbBinding binding)
        {
            OriginalExecution execution = binding.original;
            if (execution == null) return;
            RemoveOriginal(binding);
            execution.cancelled = true;
            if (execution.stance is Stance_Warmup warmup)
            {
                // Interrupt cleans the original aim effecter without firing the shot.
                OriginalScope scope = EnterOriginal(execution);
                try { warmup.Interrupt(); }
                finally { FinishOriginalTick(verb, scope, null); }
            }
        }

        private static void RemoveOriginal(VerbBinding binding)
        {
            if (binding.original?.game == originalGame && originalCount > 0) originalCount--;
            binding.original = null;
        }

        internal static void ResetOriginalExecutions() { originalGame = null; originalCount = 0; }

        internal static void Bind(Verb verb)
        {
            if (verb != null)
            {
                VerbBinding binding = bindings.GetOrCreateValue(verb);
                if (binding.request?.Pending == true
                    && !RimKataPreparedWeaponData.IsCurrent(verb))
                {
                    binding.request.Cancel();
                    // A native callback can invalidate settings while the shot still reads its properties.
                    if (binding.request?.Executing == true) return;
                }
                RimKataPreparedWeaponData.Bind(verb);
            }
        }

        internal bool Queue(bool allowOriginalHandoff = false)
        {
            if (Pending || verb == null || verb.state != VerbState.Idle
                || pawn?.Spawned != true || !target.IsValid)
            {
                return false;
            }
            if (bindingVerb != verb)
            {
                if (kickAttack == null) Bind(verb);
                bindingVerb = verb;
                nativeBinding = bindings.GetOrCreateValue(verb);
            }
            else if (kickAttack == null && !RimKataPreparedWeaponData.IsCurrent(verb))
            {
                RimKataPreparedWeaponData.Bind(verb);
            }
            if (nativeBinding.request != null)
            {
                return false;
            }

            executionPlan = RimKataWeaponExecutionRuntime.GetPlan(verb);
            OriginalHandoff = RimKataWeaponExecutionRuntime.RequiresOriginalCast(verb);
            if (OriginalHandoff)
            {
                // Special one-shot responses require an owned completion contract. The
                // ordinary cycle can instead submit one untouched original cast.
                if (!allowOriginalHandoff || !RimKataWeaponExecutionRuntime.CanSubmitOriginalCast(verb)) return false;
                RimKataPreparedWeaponData.Restore(verb);
                cancelled = Started = HasFired = false;
                Pending = true;
                nativeBinding.request = this;
                return true;
            }
            bool observeCompletion = executionPlan != null && !executionPlan.HandledByCompatibility
                && executionPlan.Mode != RimKataWeaponExecutionMode.Vanilla;
            Action nativeCallback = observeCompletion ? castCompleteCallback(verb) : null;
            if (!ResetNativeVerb()) return false;
            currentTarget(verb) = target;
            currentDestination(verb) = LocalTargetInfo.Invalid;
            surpriseAttack(verb) = false;
            canHitNonTargetPawns(verb) = huntingSession == null;
            preventFriendlyFire(verb) = huntingSession != null && job.preventFriendlyFire;
            nonInterruptingSelfCast(verb) = true;
            cancelled = false;
            Started = false;
            HasFired = false;
            kickOpportunityNotified = false;
            Pending = true;
            nativeBinding.request = this;
            observedShotAttempted = nativeCompleted = false;
            if (observeCompletion)
            {
                previousCompletion = nativeCallback;
                if (completionCallback == null) completionCallback = NativeCompleted;
                castCompleteCallback(verb) = completionCallback;
            }
            if (reactiveMotion != null || subdueState != null) LimitToSingleShot();
            return true;
        }

        // Compatibility can reject preparation at this call boundary. Keep the
        // false return in compiled C#: Queue may retain other values on its IL stack.
        internal bool ResetNativeVerb()
        {
            verb.Reset();
            return true;
        }

        internal void BeginReactiveAttack(RimKataReactiveMotionState motion)
        {
            reactiveMotion = motion;
            reactiveOpeningAttack = true;
            HasFired = false;
            LimitToSingleShot();
            if (verb.state == VerbState.Bursting) burstShotsLeft(verb) = 1;
        }

        private void LimitToSingleShot()
        {
            if (singleShotOverride) return;
            previousBurstShotCount = cachedBurstShotCount(verb);
            cachedBurstShotCount(verb) = 1;
            singleShotOverride = true;
            if (reactiveMotion != null) reactiveRequestCount++;
        }

        internal static RimKataNativeAttack ReactiveRequest(Verb verb)
            => reactiveRequestCount != 0 && verb != null
                && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Pending == true && binding.request.reactiveMotion != null
                    ? binding.request : null;

        internal static bool WaitingForNativeTick(Verb verb)
            => verb != null && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Pending == true && !binding.request.Executing;

        internal static bool CanBeginNativeTick(Verb verb)
        {
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing) return false;
            if (request.Started)
            {
                // Reuse the existing registered-request lookup; ordinary Verb ticks gain no new scan.
                if (request.executionPlan != null && !request.executionPlan.HandledByCompatibility)
                    request.ReconcileNativeTick(null);
                return false;
            }
            if (request.reactiveMotion != null && Find.TickManager.TicksGame < request.notBeforeTick)
                return false;
            if (!request.CanContinue() || verb.state != VerbState.Idle)
            {
                request.Cancel();
                return false;
            }
            return request.OriginalHandoff || request.PrepareShot();
        }

        internal void HandOffOriginalCast()
        {
            // This runs only at this Verb's existing tick opportunity. Detach before
            // calling original code: its reset/callback/stance now belong to the weapon.
            Started = true;
            Detach();
            bool accepted = false;
            try
            {
                if (verb.state != VerbState.Idle || !verb.Available()) return;
                bool separate = state?.dualEngagementActive == true;
                if (!separate)
                {
                    if (!(pawn.stances?.curStance is Stance_Busy busy && !(busy is Stance_RimKataAim)))
                        accepted = RimKataWeaponExecutionRuntime.TryStartOriginalCast(verb, target);
                    return;
                }
                // Pawn-wide incapacitation is still handled by CanContinue. Only
                // weapon warmup/cooldown is moved into this Verb's existing binding.
                var original = new OriginalExecution
                {
                    game = Current.Game, verb = verb, pawn = pawn, job = job, cycle = cycle,
                    lastTick = Find.TickManager.TicksGame,
                    mobile = new Stance_Mobile { stanceTracker = pawn.stances }
                };
                if (originalGame != Current.Game) { originalGame = Current.Game; originalCount = 0; }
                bindings.GetOrCreateValue(verb).original = original;
                originalCount++;
                OriginalScope scope = EnterOriginal(original);
                try { accepted = RimKataWeaponExecutionRuntime.TryStartOriginalCast(verb, target); }
                catch (Exception exception)
                {
                    FinishOriginalTick(verb, scope, exception);
                    scope = default;
                }
                finally { if (scope.execution != null) FinishOriginalTick(verb, scope, null); }
            }
            catch (Exception exception)
            {
                bool fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(verb, exception);
                RimKataDualWeaponController.NotifyWeaponExecutionFailed(pawn, cycle, verb,
                    target.Thing, exception, fallback);
            }
            finally
            {
                try { RimKataDualWeaponController.CompleteOriginalCastHandoff(this, accepted); }
                finally { ReleaseReferences(); }
            }
        }

        private bool CanContinue()
        {
            if (kickAttack != null) return kickAttack.CanContinue();
            if (reactiveMotion != null) return RimKataReactiveAttack.CanContinue(this);
            if (subdueState != null) return RimKataSubdueCombat.CanContinueAttack(this);
            if (state?.autoSubduePending == true) return false;
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed || pawn.InMentalState
                || pawn.stances.stunner.Stunned || pawn.CurJob != job
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || cycle.weapon != weapon || cycle.plannedTarget != firedTarget
                || (!target.HasThing && (!cycle.HasDirectionalFire
                    || cycle.plannedDirectionalFireCell != target.Cell))
                || verb.CasterPawn != pawn
                || !RimKataDualWeaponController.NativeAttackStillAllowed(this))
            {
                return false;
            }
            Thing targetThing = target.Thing;
            if (targetThing != null && (!targetThing.Spawned || targetThing.Map != pawn.Map
                || (targetThing is Pawn victim && (victim.Dead
                    || (huntingSession == null && RimKataTargeting.IsIncapacitatedTarget(victim)
                        && !(playerForced && killIncappedTarget)
                        && !RimKataPerspectiveShiftCompat.IsManualTarget(pawn, weapon, targetThing))))))
            {
                return false;
            }

            return (!(closeShot || closeFireMelee) || pawn.CanReachImmediate(target, PathEndMode.Touch))
                && (!interceptionShot || RimKataTargeting.IsInterceptionTargetActive(interceptionTarget));
        }

        internal bool PrepareShot()
        {
            if (!target.HasThing && cycle?.HasDirectionalFire == true
                && reactiveMotion == null && subdueState == null && kickAttack == null && huntingSession == null
                && RimKataPerspectiveShiftCompat.HasManualFireInput(pawn))
            {
                // PS can move after Queue; project at the actual shot without restarting its cycle.
                if (!RimKataDirectionalFire.TryGetShotTarget(pawn, cycle, verb, out LocalTargetInfo shot))
                {
                    Cancel();
                    return false;
                }
                target = shot;
                cycle.plannedDirectionalFireCell = shot.Cell;
                currentTarget(verb) = shot;
            }
            if (subdueState != null)
            {
                try
                {
                    if (!RimKataSubdueCombat.AttackStarting(this))
                    {
                        Cancel();
                        return false;
                    }
                }
                catch
                {
                    Cancel();
                    throw;
                }
            }
            if (closeShot)
            {
                currentTarget(verb) = target;
                Executing = true;
                try
                {
                    closeMeleeHit = RimKataCombatMath.RollCloseRangedNonMiss(pawn, verb, target);
                    closeDefensePrecheck = RimKataDefenseUtility.PrecheckCloseGunfire(
                        pawn, target.Thing, verb, closeMeleeHit);
                }
                catch
                {
                    Executing = false;
                    Cancel();
                    throw;
                }
                finally { Executing = false; }
                if (closeDefensePrecheck == RimKataCloseDefensePrecheck.ResponseSucceeded)
                {
                    HasFired = true;
                    CompleteRequest(true);
                    return false;
                }
                if (closeDefensePrecheck == RimKataCloseDefensePrecheck.ResponseSucceededWithAccidentalShot)
                    closeMeleeHit = false;
                if (cancelled)
                {
                    CompleteRequest(true);
                    return false;
                }
                if (!closeMeleeHit && !(verb is Verb_LaunchProjectile) && target.Thing != null)
                {
                    IntVec3 cell = RimKataProjectileUtility.FindCloseMissCell(pawn, target.Thing, pawn.Map);
                    if (cell.IsValid) currentTarget(verb) = new LocalTargetInfo(cell);
                }
            }
            if (kickAttack == null) RimKataReactiveMotion.AttackStarting(this);
            return Pending && !cancelled;
        }

        internal static bool TryBeginNativeCast(Verb verb, out RimKataNativeAttack scope)
        {
            scope = null;
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing)
            {
                return true;
            }
            if (request.OriginalHandoff)
            {
                // Original code started first; withdraw only our unsubmitted opportunity.
                request.Cancel();
                return true;
            }
            request.Started = true;
            if (!request.BeginExecution()) return false;
            scope = request;
            return true;
        }

        internal static bool TryBeginBurstShot(Verb verb, out RimKataNativeAttack scope)
        {
            scope = null;
            if (!bindings.TryGetValue(verb, out VerbBinding binding)
                || binding.request is not RimKataNativeAttack request
                || !request.Pending || request.Executing)
            {
                // The first shot is already inside its WarmupComplete scope.
                return true;
            }
            if (request.OriginalHandoff)
            {
                request.Cancel();
                return true;
            }

            if (!request.CanContinue())
            {
                request.cancelled = true;
                request.CompleteRequest(true);
                return false;
            }
            if (!request.PrepareShot())
            {
                return false;
            }

            request.Started = true;
            if (!request.BeginExecution()) return false;
            scope = request;
            return true;
        }

        private bool BeginExecution()
        {
            Executing = true;
            executionScopes = 0;
            try
            {
                previousStats = new RimKataWeaponEffectStats.Scope(verb, verb);
                executionScopes |= StatsScope;
                previousAim = pawn.stances?.curStance as Stance_RimKataAim;
                previousSpecialStance = huntingSession != null || reactiveMotion != null || subdueState != null || kickAttack != null
                    ? pawn.stances?.curStance : null;
                if (subdueState != null) previousSubdueRotation = pawn.Rotation;
                pawn.rotationTracker.FaceCell(verb.CurrentTarget.Cell);
                movingShot = !verb.IsMeleeAttack && !closeShot && pawn.pather?.MovingNow == true;
                previousContext = RimKataFireContext.Begin(
                    verb, pawn, closeShot ? target.Thing : null,
                    movingShot, closeShot, interceptionShot,
                    interceptionTarget, closeMeleeResolution,
                    closeMeleeHit, closeDefensePrecheck);
                executionScopes |= FireScope;
                previousSlidingOrigin = RimKataSlidingAttackOrigin.Begin(this);
                executionScopes |= SlidingScope;
                previousFlyingKickOrigin = RimKataFlyingKickRender.BeginShot(this);
                executionScopes |= FlyingScope;
                previousKickOrigin = RimKataKickRender.BeginShot(this);
                executionScopes |= KickScope;
                if (subdueState != null)
                {
                    previousSubdueExecution = RimKataSubdueCombat.BeginNativeExecution(subdueState);
                    executionScopes |= SubdueScope;
                }
                previousDirectionalExecution = RimKataDirectionalFire.BeginNativeExecution(this);
                executionScopes |= DirectionScope;
                return true;
            }
            catch (Exception exception)
            {
                Exception cleanup = EndExecutionScopes();
                if (cleanup != null) exception = new AggregateException(exception, cleanup);
                FailExecution(exception, pawn, cycle, verb, target.Thing);
                return false;
            }
        }

        private Exception EndExecutionScopes()
        {
            byte acquired = executionScopes;
            executionScopes = 0;
            Exception failure = null;
            // No per-shot delegates or collections. Restore only completed acquisitions,
            // in reverse order, and keep unwinding if one cleanup callback fails.
            for (int scope = DirectionScope; scope != 0; scope >>= 1)
            {
                if ((acquired & scope) == 0) continue;
                try
                {
                    switch (scope)
                    {
                        case DirectionScope: RimKataDirectionalFire.EndNativeExecution(previousDirectionalExecution); break;
                        case SubdueScope: RimKataSubdueCombat.EndNativeExecution(previousSubdueExecution); break;
                        case KickScope: RimKataKickRender.EndShot(previousKickOrigin); break;
                        case FlyingScope: RimKataFlyingKickRender.EndShot(previousFlyingKickOrigin); break;
                        case SlidingScope: RimKataSlidingAttackOrigin.End(previousSlidingOrigin); break;
                        case FireScope:
                            if (RimKataFireContext.ActiveVerb == verb) HasFired |= RimKataFireContext.ShotFired;
                            RimKataFireContext.End(verb, previousContext);
                            break;
                        case StatsScope: previousStats.Dispose(); break;
                    }
                }
                catch (Exception exception) { failure ??= exception; }
            }
            previousDirectionalExecution = default;
            previousSubdueExecution = default;
            previousKickOrigin = default;
            previousFlyingKickOrigin = default;
            previousSlidingOrigin = default;
            previousContext = default;
            previousStats = default;
            Executing = false;
            return failure;
        }

        private void FailExecution(Exception exception, Pawn owner, RimKataWeaponCycleState failedCycle,
            Verb failedVerb, Thing failedTarget)
        {
            cancelled = true;
            bool fallback = false;
            try { fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(failedVerb, exception); }
            catch (Exception failure) { exception = new AggregateException(exception, failure); }
            if (Pending)
            {
                try { RestoreAimAfterShot(); }
                catch (Exception failure) { exception = new AggregateException(exception, failure); }
                try { CompleteRequest(true); }
                catch (Exception failure) { exception = new AggregateException(exception, failure); }
            }
            RimKataDualWeaponController.NotifyWeaponExecutionFailed(owner, failedCycle,
                failedVerb, failedTarget, exception, fallback);
        }

        internal static bool OwnsActiveMelee(Verb verb)
            => bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request?.Executing == true;

        internal static void NotifyMeleeAttackStarting(
            RimKataResponseVisualParticipantCache.BodyVisualEntry entry, RimWorld.Verb_MeleeAttack verb)
        {
            if (verb is Verb_RimKataFlyingKick || verb is Verb_RimKataKick) return;
            if (bindings.TryGetValue(verb, out VerbBinding binding) && binding.request?.Executing == true)
                binding.request.NotifyKickAttackStarting();
            else
                RimKataKick.NativeMeleeStarting(entry, verb);
        }

        private void NotifyKickAttackStarting()
        {
            if (kickOpportunityNotified || kickAttack != null
                || reactiveMotion != null || subdueState != null || huntingSession != null) return;
            kickOpportunityNotified = true;
            RimKataKick.AttackStarting(this);
        }

        internal void FinishNativeCast(Exception exception)
            => FinishNativeCastCore(exception, false);

        internal void FinishObservedShot(Exception exception)
            => FinishNativeCastCore(exception, true);

        private void FinishNativeCastCore(Exception exception, bool deferCompletion)
        {
            Pawn owner = pawn;
            RimKataWeaponCycleState failedCycle = cycle;
            Verb failedVerb = verb;
            Thing failedTarget = target.Thing;
            bool rangedShot = exception == null && verb?.IsMeleeAttack == false && RimKataFireContext.ShotFired;
            Exception cleanup = EndExecutionScopes();
            if (cleanup != null) exception = exception == null ? cleanup : new AggregateException(exception, cleanup);
            if (exception != null)
            {
                FailExecution(exception, owner, failedCycle, failedVerb, failedTarget);
                return;
            }
            try
            {
                if (deferCompletion && !cancelled)
                {
                    RestoreAimAfterShot();
                    if (rangedShot) NotifyKickAttackStarting();
                }
                else CompleteNativeCast(null, rangedShot);
            }
            catch (Exception failure) { FailExecution(failure, owner, failedCycle, failedVerb, failedTarget); }
        }

        internal void CompleteNativeCast(Exception exception, bool rangedShot)
        {
            RestoreAimAfterShot();
            if (rangedShot) NotifyKickAttackStarting();
            if (exception != null)
            {
                cancelled = true;
            }
            // Only a prepared native/compatibility contract enters this owned request.
            // Analysis coverage is neither runtime progress nor a future callback.
            if (cancelled || nativeCompleted || verb.state != VerbState.Bursting)
            {
                Pawn owner = pawn;
                RimKataWeaponCycleState failedCycle = cycle;
                Verb failedVerb = verb;
                Thing failedTarget = target.Thing;
                bool fallback = exception != null && RimKataWeaponExecutionRuntime.MarkExecutionFailed(verb, exception);
                try { CompleteRequest(cancelled); }
                finally
                {
                    if (exception != null)
                        RimKataDualWeaponController.NotifyWeaponExecutionFailed(owner, failedCycle,
                            failedVerb, failedTarget, exception, fallback);
                }
            }
        }

        private void NativeCompleted()
        {
            nativeCompleted = true;
            previousCompletion?.Invoke();
        }

        internal void ReconcileNativeTick(Exception exception)
        {
            if (!Pending || Executing || (!Started && exception == null)) return;
            if (OriginalHandoff)
            {
                // An owner-tick exception before submission is a rejected handoff,
                // not a failed RK shot and not permission to reset the original Verb.
                Detach();
                try { RimKataDualWeaponController.CompleteOriginalCastHandoff(this, false); }
                finally { ReleaseReferences(); }
                return;
            }
            if (exception != null || nativeCompleted
                || verb.state != VerbState.Bursting)
                CompleteNativeCast(exception, false);
        }

        internal void RestoreAimAfterShot()
        {
            bool restoreSubdue = subdueState != null
                && RimKataSubdueUtility.Get(pawn) == subdueState && pawn.CurJob == job;
            if (pawn.stances?.curStance is Stance_Busy busy && busy.verb == verb)
            {
                if (huntingSession != null || reactiveMotion != null || subdueState != null || kickAttack != null)
                {
                    if (pawn.CurJob == job && previousSpecialStance != null
                        && (subdueState == null || restoreSubdue))
                        pawn.stances.curStance = previousSpecialStance;
                }
                else if (previousAim != null)
                {
                    pawn.stances.curStance = previousAim;
                }
                else
                {
                    RimKataAutomaticCastSuppressionState suppression = RimKataAutomaticCastSuppression.Push(pawn);
                    try { pawn.stances.SetStance(new Stance_Mobile()); }
                    finally { RimKataAutomaticCastSuppression.Pop(suppression); }
                }
            }
            if (restoreSubdue) pawn.Rotation = previousSubdueRotation;
            previousAim = null;
            previousSpecialStance = null;
        }

        private void CompleteRequest(bool resetNative)
        {
            Detach();
            try
            {
                // A reset failure must not bypass request cleanup or the other slot.
                if (resetNative)
                    try { verb.Reset(); }
                    catch (Exception exception)
                    {
                        bool fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(verb, exception);
                        RimKataDualWeaponController.NotifyWeaponExecutionFailed(pawn, cycle,
                            verb, target.Thing, exception, fallback);
                    }
                nonInterruptingSelfCast(verb) = false;
                if (kickAttack != null) kickAttack.Complete(cancelled);
                else if (reactiveMotion != null) RimKataReactiveMotion.AttackCompleted(this);
                else if (subdueState != null) RimKataSubdueCombat.AttackCompleted(this);
                else RimKataDualWeaponController.CompleteNativeAttack(this, HasFired, cancelled);
            }
            finally
            {
                try
                {
                    if (cancelled || (kickAttack == null
                        && (subdueState != null ? subdueState.weaponVerb != verb : cycle?.boundVerb != verb)))
                    {
                        try { RimKataPreparedWeaponData.Restore(verb); }
                        finally { bindingVerb = null; nativeBinding = null; }
                    }
                }
                finally { ReleaseReferences(); }
            }
        }

        internal void Cancel()
        {
            if (!Pending) return;
            cancelled = true;
            // Damage callbacks can cancel the cycle while its native cast is still unwinding.
            if (Executing) return;
            if (OriginalHandoff)
            {
                Detach();
                ReleaseReferences();
                return;
            }
            Verb pendingVerb = verb;
            Detach();
            try { pendingVerb.Reset(); }
            catch (Exception exception)
            {
                bool fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(pendingVerb, exception);
                RimKataDualWeaponController.NotifyWeaponExecutionFailed(pawn, cycle,
                    pendingVerb, target.Thing, exception, fallback);
            }
            finally
            {
                nonInterruptingSelfCast(pendingVerb) = false;
                ReleaseReferences();
            }
        }

        internal bool AbortAfterFailure(Exception exception)
        {
            bool fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(verb ?? bindingVerb, exception);
            try { Cancel(); }
            finally { if (!Executing) ReleaseReferences(); }
            return fallback;
        }

        internal void ForgetBinding()
        {
            Cancel();
            if (Executing) return;
            bindingVerb = null;
            nativeBinding = null;
        }

        internal static void NotifyReset(Verb verb)
        {
            if (!bindings.TryGetValue(verb, out VerbBinding binding)) return;
            ClearOriginal(verb, binding);
            if (!binding.resettingAfterLoad)
                binding.interruptedBurstAfterLoad = false;
            if (binding.request is RimKataNativeAttack request)
            {
                request.cancelled = true;
                if (request.Executing) return;
                request.Detach();
                nonInterruptingSelfCast(verb) = false;
                request.ReleaseReferences();
            }
        }

        internal static void NotifyEquipmentLost(Verb verb)
        {
            if (bindings.TryGetValue(verb, out VerbBinding originalBinding)) ClearOriginal(verb, originalBinding);
            if (bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request is RimKataNativeAttack request)
            {
                request.Cancel();
                if (request.Executing) return;
            }
            RimKataPreparedWeaponData.Restore(verb);
        }

        internal static Exception FinishOwnerTick(Verb verb, OriginalScope scope, Exception exception)
        {
            exception = FinishOriginalTick(verb, scope, exception);
            if (exception == null) return null;
            RimKataNativeAttack request = PendingRequest(verb);
            if (request == null) return exception;
            Pawn owner = request.pawn;
            RimKataWeaponCycleState failedCycle = request.cycle;
            Thing failedTarget = request.target.Thing;
            bool fallback = RimKataWeaponExecutionRuntime.MarkExecutionFailed(verb, exception);
            try
            {
                if (request.Executing) request.FinishNativeCast(exception);
                else request.ReconcileNativeTick(exception);
            }
            catch (Exception cleanup)
            {
                exception = new AggregateException(exception, cleanup);
                try { request.Cancel(); }
                catch (Exception failure) { exception = new AggregateException(exception, failure); }
            }
            finally
            {
                RimKataDualWeaponController.NotifyWeaponExecutionFailed(owner, failedCycle,
                    verb, failedTarget, exception, fallback);
            }
            return null;
        }

        internal static void ExposeData(Verb verb)
        {
            bool queued = WaitingForNativeTick(verb) || IsOriginalCastBusy(verb);
            bool interruptedBurst = queued
                && bindings.TryGetValue(verb, out VerbBinding current)
                && ((current.request?.Started == true && current.request.HasFired)
                    || (current.original != null && verb.state == VerbState.Bursting));
            Scribe_Values.Look(ref queued, "rimKataQueuedCast");
            Scribe_Values.Look(ref interruptedBurst, "rimKataInterruptedNativeBurst");
            if (Scribe.mode == LoadSaveMode.LoadingVars && queued)
            {
                VerbBinding loaded = bindings.GetOrCreateValue(verb);
                loaded.cancelAfterLoad = true;
                loaded.interruptedBurstAfterLoad = interruptedBurst;
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit
                && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.cancelAfterLoad)
            {
                binding.cancelAfterLoad = false;
                binding.resettingAfterLoad = true;
                try { verb.Reset(); }
                finally { binding.resettingAfterLoad = false; }
                nonInterruptingSelfCast(verb) = false;
            }
        }

        internal static bool ConsumeInterruptedBurstAfterLoad(Verb verb)
        {
            if (verb == null || !bindings.TryGetValue(verb, out VerbBinding binding))
            {
                return false;
            }
            bool interrupted = binding.interruptedBurstAfterLoad;
            binding.interruptedBurstAfterLoad = false;
            return interrupted;
        }

        private void Detach()
        {
            if (verb != null && completionCallback != null && castCompleteCallback(verb) == completionCallback)
                castCompleteCallback(verb) = previousCompletion;
            previousCompletion = null;
            if (verb != null && bindings.TryGetValue(verb, out VerbBinding binding)
                && binding.request == this) binding.request = null;
            Pending = false;
            Executing = false;
            if (singleShotOverride)
            {
                cachedBurstShotCount(verb) = previousBurstShotCount;
                singleShotOverride = false;
                if (reactiveMotion != null) reactiveRequestCount--;
            }
        }

        private void ReleaseReferences()
        {
            try
            {
                if (kickAttack != null && !kickAttack.Completed) kickAttack.Complete(true);
            }
            finally
            {
                try { if (reactiveMotion != null) RimKataReactiveMotion.ReleaseAttack(this); }
                finally { ClearReferences(); }
            }
        }

        private void ClearReferences()
        {
            pawn = null;
            state = null;
            cycle = null;
            huntingSession = null;
            reactiveMotion = null;
            subdueState = null;
            kickAttack = null;
            reactiveOpeningAttack = false;
            notBeforeTick = 0;
            weapon = null;
            verb = null;
            cycleVerb = null;
            closeFireMelee = false;
            job = null;
            assignedTarget = null;
            firedTarget = null;
            target = LocalTargetInfo.Invalid;
            playerForced = false;
            killIncappedTarget = false;
            targetSelectionLocked = false;
            interceptionTarget = null;
            previousContext = default;
            previousAim = null;
            previousSlidingOrigin = default;
            previousSpecialStance = null;
            previousSubdueExecution = default;
            previousDirectionalExecution = default;
            previousFlyingKickOrigin = default;
            previousKickOrigin = default;
            previousStats = default;
            executionScopes = 0;
            Started = false;
            HasFired = false;
            OriginalHandoff = false;
            kickOpportunityNotified = false;
        }

        internal void ClearCompletedReferences() => ReleaseReferences();
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.VerbTick))]
    internal static class Patch_VerbTick_RimKataNativeAttack
    {
        private static void Prefix(Verb __instance, out RimKataNativeAttack.OriginalScope __state)
            => RimKataNativeAttack.BeginOriginalTick(__instance, out __state);

        private static Exception Finalizer(Verb __instance, Exception __exception, RimKataNativeAttack.OriginalScope __state)
            => RimKataNativeAttack.FinishOwnerTick(__instance, __state, __exception);

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            MethodInfo warmup = AccessTools.Method(typeof(RimKataWeaponExecutionRuntime), nameof(RimKataWeaponExecutionRuntime.WarmupComplete));
            MethodInfo ready = AccessTools.Method(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.CanBeginNativeTick));
            Label regular = generator.DefineLabel();
            FieldInfo stateField = AccessTools.Field(typeof(Verb), nameof(Verb.state));
            Label? afterBurst = null;
            for (int i = 0; i + 2 < codes.Count; i++)
            {
                if (codes[i].LoadsField(stateField)
                    && codes[i + 1].opcode == OpCodes.Ldc_I4_1
                    && (codes[i + 2].opcode == OpCodes.Bne_Un
                        || codes[i + 2].opcode == OpCodes.Bne_Un_S)
                    && codes[i + 2].operand is Label target)
                {
                    afterBurst = target;
                    break;
                }
            }
            if (!afterBurst.HasValue)
            {
                Log.Error("[RimKata] Could not locate the native burst tick boundary.");
                foreach (CodeInstruction instruction in codes) yield return instruction;
                yield break;
            }

            // A new burst already fired this tick; resume at native effecter maintenance.
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, ready);
            yield return new CodeInstruction(OpCodes.Brfalse, regular);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, warmup);
            yield return new CodeInstruction(OpCodes.Br, afterBurst.Value);
            CodeInstruction originalEntry = new CodeInstruction(OpCodes.Nop);
            originalEntry.labels.Add(regular);
            yield return originalEntry;
            foreach (CodeInstruction instruction in codes) yield return instruction;
        }
    }

    [HarmonyPatch(typeof(Verb), "TryCastNextBurstShot")]
    internal static class Patch_VerbNextBurstShot_RimKataNativeAttack
    {
        private static bool Prefix(Verb __instance, out RimKataNativeAttack __state)
            => RimKataNativeAttack.TryBeginBurstShot(__instance, out __state);

        private static Exception Finalizer(Exception __exception, RimKataNativeAttack __state)
        {
            __state?.FinishNativeCast(__exception);
            return __state != null && !__state.Pending && !__state.Executing ? null : __exception;
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.Reset))]
    internal static class Patch_VerbReset_RimKataNativeAttack
    {
        private static void Prefix(Verb __instance) => RimKataNativeAttack.NotifyReset(__instance);
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.ExposeData))]
    internal static class Patch_VerbExposeData_RimKataNativeAttack
    {
        private static void Postfix(Verb __instance)
        {
            // Cast context is transient; loading must not repeat a partially fired burst.
            RimKataNativeAttack.ExposeData(__instance);
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.Notify_EquipmentLost))]
    internal static class Patch_VerbEquipmentLost_RimKataNativeAttack
    {
        private static void Prefix(Verb __instance) => RimKataNativeAttack.NotifyEquipmentLost(__instance);
    }
}
