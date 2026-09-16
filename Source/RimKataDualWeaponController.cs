using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace KRWF.RimKata
{
    public enum RimKataWeaponSlot
    {
        Primary,
        Secondary
    }

    public enum RimKataCounterattackOpeningResult
    {
        NotHandled,
        Absorbed,
        Converted
    }

    public sealed class RimKataWeaponCycleState : IExposable
    {
        public ThingWithComps weapon;
        public int cooldownTicksRemaining;
        public int warmupTicksRemaining = -1;
        public int warmupTotalTicks;
        public int openingWarmupBonusTicks;
        public bool openingWarmupPending;
        public bool openingSupportDelayConsumed;
        public Thing cachedCandidateTarget;
        public bool cachedCandidateInterception;
        public List<Thing> automaticCandidates = new List<Thing>();
        private HashSet<int> automaticCandidateIds;
        private int nextAutomaticCandidateValidationIndex;
        public bool automaticCandidateCollectionClosed;
        public int pendingCandidateLimitOverride;
        public int activeCandidateLimitOverride;
        public Thing lastFiredTarget;
        public bool firedInCurrentOpening;
        public bool cooldownFromVanillaOpening;
        internal bool NativeAttackPending => nativeAttack?.Pending == true;
        internal bool completeInterruptedAttackAfterLoad;
        public Thing focusedTarget;
        public bool focusedTargetFromAttackGizmo;
        public Thing plannedTarget;
        public bool plannedInterception;
        public bool plannedCloseAttack;
        public bool plannedCloseContext;
        public Verb plannedActionVerb;
        public Thing visualTarget;
        public int visualAimTicksRemaining;
        private int lastTimerTick = -1;
        private int responseCooldownAppliedTick = -1;
        internal Verb boundVerb;
        internal RimKataNativeAttack nativeAttack;
        internal bool ordinaryWeaponEnabled;
        internal int lastDrivenTick = -1;

        internal bool ResponseCooldownAppliedThisTick => responseCooldownAppliedTick >= 0
            && responseCooldownAppliedTick == (Find.TickManager?.TicksGame ?? -1);

        public bool HasPlan => plannedTarget != null;
        public bool IsWarming => warmupTicksRemaining > 0;
        public bool Active => weapon != null
            && (cooldownTicksRemaining > 0
            || warmupTicksRemaining > 0
            || openingWarmupPending
             || cachedCandidateTarget != null
             || HasAutomaticCandidates
            || focusedTarget != null
            || HasPlan
            || visualAimTicksRemaining > 0);

        public bool CombatActive => weapon != null
            && (openingWarmupPending
             || cachedCandidateTarget != null
             || HasAutomaticCandidates
            || focusedTarget != null
            || HasPlan);

        public bool DedicatedActive => weapon != null
            && (cachedCandidateTarget != null
                 || HasAutomaticCandidates
                || focusedTarget != null
                || HasPlan);

        public void ExposeData()
        {
            Scribe_References.Look(ref weapon, "weapon");
            Scribe_Values.Look(ref cooldownTicksRemaining, "cooldownTicksRemaining");
            Scribe_Values.Look(ref responseCooldownAppliedTick, "responseCooldownAppliedTick", -1);
            Scribe_Values.Look(ref warmupTicksRemaining, "warmupTicksRemaining", -1);
            Scribe_Values.Look(ref warmupTotalTicks, "warmupTotalTicks");
            Scribe_Values.Look(ref openingWarmupBonusTicks, "openingWarmupBonusTicks");
            Scribe_Values.Look(ref openingWarmupPending, "openingWarmupPending");
            Scribe_Values.Look(
                ref openingSupportDelayConsumed,
                "openingSupportDelayConsumed");
            Scribe_References.Look(ref cachedCandidateTarget, "cachedCandidateTarget");
            Scribe_Collections.Look(
                ref automaticCandidates,
                "automaticCandidates",
                LookMode.Reference);
            Scribe_Values.Look(
                ref nextAutomaticCandidateValidationIndex,
                "nextAutomaticCandidateValidationIndex",
                0);
            Scribe_Values.Look(
                ref automaticCandidateCollectionClosed,
                "automaticCandidateCollectionClosed");
            Scribe_Values.Look(
                ref pendingCandidateLimitOverride,
                "pendingCandidateLimitOverride");
            Scribe_Values.Look(
                ref activeCandidateLimitOverride,
                "activeCandidateLimitOverride");
            Scribe_Values.Look(
                ref cachedCandidateInterception,
                "cachedCandidateInterception");
            Scribe_References.Look(ref lastFiredTarget, "lastFiredTarget");
            Scribe_Values.Look(ref firedInCurrentOpening, "firedInCurrentOpening");
            Scribe_Values.Look(
                ref cooldownFromVanillaOpening,
                "cooldownFromVanillaOpening");
            // Old saves may contain manual burst counters. Finish those partial
            // attacks on the first binding, before any new plan can be queued.
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                int legacyBurstShots = 0;
                Scribe_Values.Look(ref legacyBurstShots, "burstShotsRemaining");
                completeInterruptedAttackAfterLoad = legacyBurstShots > 0;
            }
            Scribe_References.Look(ref focusedTarget, "focusedTarget");
            Scribe_Values.Look(
                ref focusedTargetFromAttackGizmo,
                "focusedTargetFromAttackGizmo");
            Scribe_References.Look(ref plannedTarget, "plannedTarget");
            Scribe_Values.Look(ref plannedInterception, "plannedInterception");
            Scribe_Values.Look(ref plannedCloseAttack, "plannedCloseAttack");
            Scribe_Values.Look(ref plannedCloseContext, "plannedCloseContext");
            Scribe_References.Look(ref visualTarget, "visualTarget");
            Scribe_Values.Look(ref visualAimTicksRemaining, "visualAimTicksRemaining");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                automaticCandidates ??= new List<Thing>();
                automaticCandidateIds = automaticCandidates.Count == 0
                    ? null : new HashSet<int>();
                for (int i = automaticCandidates.Count - 1; i >= 0; i--)
                {
                    if (!(automaticCandidates[i] is Pawn candidate))
                    {
                        RemoveAutomaticCandidateAt(i);
                    }
                    else
                    {
                        automaticCandidateIds.Add(candidate.thingIDNumber);
                    }
                }
                NormalizeAutomaticCandidateValidationIndex();
                lastTimerTick = -1;
                plannedActionVerb = null;
                cooldownTicksRemaining = Mathf.Max(0, cooldownTicksRemaining);
                pendingCandidateLimitOverride = Mathf.Max(
                    0,
                    pendingCandidateLimitOverride);
                activeCandidateLimitOverride = Mathf.Max(
                    0,
                    activeCandidateLimitOverride);
                if (HasPlan && warmupTicksRemaining <= 0)
                {
                    warmupTicksRemaining = 1;
                    warmupTotalTicks = Mathf.Max(1, warmupTotalTicks);
                }

                if (plannedInterception
                    && !(plannedTarget is Projectile))
                {
                    ClearPlan(false);
                    warmupTicksRemaining = -1;
                    warmupTotalTicks = 0;
                    openingWarmupBonusTicks = 0;
                    openingWarmupPending = false;
                }

                if (cachedCandidateInterception
                    && !(cachedCandidateTarget is Projectile))
                {
                    cachedCandidateTarget = null;
                    cachedCandidateInterception = false;
                }
            }
        }

        public bool Bind(ThingWithComps newWeapon)
        {
            if (weapon == newWeapon)
            {
                return false;
            }

            Reset();
            weapon = newWeapon;
            return true;
        }

        internal void ClearInvalidVisualTarget(Pawn pawn)
        {
            if (visualTarget != null
                && ((!HasPlan && cooldownTicksRemaining <= 0
                        && visualAimTicksRemaining <= 0)
                    || !RimKataDualWeaponController.IsLiveVisualTarget(
                        pawn,
                        visualTarget)))
            {
                visualTarget = null;
            }
        }

        internal void ApplyResponseCooldown(int ticks)
        {
            cooldownTicksRemaining = ticks;
            responseCooldownAppliedTick = Find.TickManager?.TicksGame ?? -1;
        }

        internal void StampNativeActionTick()
        {
            lastTimerTick = Find.TickManager?.TicksGame ?? -1;
            lastDrivenTick = lastTimerTick;
        }

        public void TickTimers()
        {
            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (currentTick >= 0
                && (lastTimerTick == currentTick || responseCooldownAppliedTick == currentTick))
            {
                return;
            }

            lastTimerTick = currentTick;
            responseCooldownAppliedTick = -1;
            if (cooldownTicksRemaining > 0)
            {
                cooldownTicksRemaining--;
                if (cooldownTicksRemaining <= 0
                    && !NativeAttackPending)
                {
                    cooldownFromVanillaOpening = false;
                }
            }
            else if (!NativeAttackPending
                && !openingWarmupPending)
            {
                cooldownFromVanillaOpening = false;
            }

            if (warmupTicksRemaining > 0)
            {
                warmupTicksRemaining--;
            }

            if (visualAimTicksRemaining > 0 && !NativeAttackPending)
            {
                visualAimTicksRemaining--;
                if (visualAimTicksRemaining <= 0 && !HasPlan)
                {
                    visualTarget = null;
                }
            }
        }

        public void ClearPlan(bool resetWarmup = true)
        {
            nativeAttack?.Cancel();
            plannedTarget = null;
            plannedInterception = false;
            plannedCloseAttack = false;
            plannedCloseContext = false;
            plannedActionVerb = null;
            if (resetWarmup
                && !openingWarmupPending)
            {
                warmupTicksRemaining = -1;
                warmupTotalTicks = 0;
            }
        }

        public bool HasAutomaticCandidates => automaticCandidates != null
            && automaticCandidates.Count > 0;

        internal bool ContainsAutomaticCandidate(Thing target)
        {
            return target is Pawn
                && automaticCandidateIds?.Contains(target.thingIDNumber) == true;
        }

        public bool AddAutomaticCandidate(Thing target)
        {
            if (!(target is Pawn))
            {
                return false;
            }

            automaticCandidates ??= new List<Thing>();
            automaticCandidateIds ??= new HashSet<int>();
            if (!automaticCandidateIds.Add(target.thingIDNumber))
            {
                return false;
            }

            automaticCandidates.Add(target);
            return true;
        }

        public bool RemoveAutomaticCandidate(Thing target)
        {
            int index = automaticCandidates?.IndexOf(target) ?? -1;
            bool removed = RemoveAutomaticCandidateAt(index);
            if (cachedCandidateTarget == target)
            {
                cachedCandidateTarget = null;
                cachedCandidateInterception = false;
            }
            return removed;
        }

        internal bool TryGetNextAutomaticCandidateForValidation(out Thing target)
        {
            target = null;
            NormalizeAutomaticCandidateValidationIndex();
            int count = automaticCandidates?.Count ?? 0;
            if (count == 0)
            {
                return false;
            }

            target = automaticCandidates[nextAutomaticCandidateValidationIndex];
            nextAutomaticCandidateValidationIndex++;
            if (nextAutomaticCandidateValidationIndex >= count)
            {
                nextAutomaticCandidateValidationIndex = 0;
            }
            return true;
        }

        internal bool RemoveAutomaticCandidateAt(int index)
        {
            if (automaticCandidates == null
                || (uint)index >= (uint)automaticCandidates.Count)
            {
                return false;
            }

            // Keep the next surviving entry in turn when earlier entries shift left.
            if (index < nextAutomaticCandidateValidationIndex)
            {
                nextAutomaticCandidateValidationIndex--;
            }
            Thing removedTarget = automaticCandidates[index];
            if (removedTarget != null)
            {
                automaticCandidateIds?.Remove(removedTarget.thingIDNumber);
            }
            automaticCandidates.RemoveAt(index);
            NormalizeAutomaticCandidateValidationIndex();
            return true;
        }

        internal void ClearStoredAutomaticCandidates()
        {
            automaticCandidates?.Clear();
            automaticCandidateIds?.Clear();
            nextAutomaticCandidateValidationIndex = 0;
        }

        private void NormalizeAutomaticCandidateValidationIndex()
        {
            int count = automaticCandidates?.Count ?? 0;
            if (count == 0 || nextAutomaticCandidateValidationIndex < 0
                || nextAutomaticCandidateValidationIndex >= count)
            {
                nextAutomaticCandidateValidationIndex = 0;
            }
        }

        public void ClearAutomaticCandidates()
        {
            ClearStoredAutomaticCandidates();
            automaticCandidateCollectionClosed = false;
            pendingCandidateLimitOverride = 0;
            activeCandidateLimitOverride = 0;
            if (!(cachedCandidateTarget is Projectile))
            {
                cachedCandidateTarget = null;
                cachedCandidateInterception = false;
            }
        }

        public void Reset()
        {
            completeInterruptedAttackAfterLoad = false;
            nativeAttack?.ForgetBinding();
            if (nativeAttack?.Executing != true)
                RimKataPreparedWeaponData.Restore(boundVerb);
            weapon = null;
            boundVerb = null;
            ordinaryWeaponEnabled = false;
            lastDrivenTick = -1;

            cooldownTicksRemaining = 0;

            openingWarmupBonusTicks = 0;
            openingWarmupPending = false;
            openingSupportDelayConsumed = false;

            cachedCandidateTarget = null;
            cachedCandidateInterception = false;
            ClearStoredAutomaticCandidates();
            automaticCandidateCollectionClosed = false;
            pendingCandidateLimitOverride = 0;
            activeCandidateLimitOverride = 0;
            focusedTarget = null;
            focusedTargetFromAttackGizmo = false;
            lastFiredTarget = null;
            firedInCurrentOpening = false;
            cooldownFromVanillaOpening = false;

            visualTarget = null;
            visualAimTicksRemaining = 0;
            lastTimerTick = -1;
            responseCooldownAppliedTick = -1;

            ClearPlan();
        }

        // !!! Debug HUD !!!
        public char DebugState
        {
            get
            {
                if (weapon == null)
                {
                    return 'W';
                }

                if (NativeAttackPending)
                {
                    return 'F';
                }

                if (warmupTicksRemaining > 0)
                {
                    return 'A';
                }

                if (cooldownTicksRemaining > 0)
                {
                    return 'C';
                }

                return 'W';
            }
        }
    }

    public struct RimKataWeaponVisualData
    {
        public ThingWithComps weapon;
        public LocalTargetInfo target;
        public bool warming;
        public int warmupTicksRemaining;
        public int warmupTotalTicks;
        public int cooldownTicksRemaining;
    }

    internal struct RimKataCombatIndicatorWeaponFrame
    {
        public Thing focusedTarget;
        public bool focusedTargetFromAttackGizmo;
        public RimKataWeaponVisualData visual;
        public Verb verb;
        public bool visible;
        public int remainingTicks;
    }

    internal struct RimKataCombatIndicatorFrame
    {
        public Thing closeTarget;
        public RimKataCombatIndicatorWeaponFrame primary;
        public RimKataCombatIndicatorWeaponFrame secondary;
        public bool pauseFireForDodge;
    }

    public struct RimKataVanillaOpeningAttempt
    {
        public bool prepared;
        public ThingWithComps weapon;
        public Thing target;
    }

    public static class RimKataDualWeaponController
    {
        [ThreadStatic] private static Verb pendingVanillaOpeningVerb;

        private readonly struct CombatTickPermissions
        {
            public readonly bool allowCurrentJob;
            public readonly bool allowAutomaticRangedFire;
            public readonly bool allowMovementSearchWithoutWork;

            public CombatTickPermissions(Pawn pawn, Job job)
            {
                bool drafted = pawn.Drafted;
                allowCurrentJob = job?.def == RimKataDefOf.RimKata_Attack
                    || RimKataDraftedFireController.IsAutomaticFireJob(job?.def);
                allowAutomaticRangedFire = AutomaticRangedFireAllowed(pawn);
                allowMovementSearchWithoutWork = drafted && allowAutomaticRangedFire;
            }

            public bool AllowsMovementSearch(bool hasCombatWork, bool dedicatedJob)
            {
                return allowAutomaticRangedFire
                    && (allowMovementSearchWithoutWork || hasCombatWork || dedicatedJob);
            }
        }

        private static bool HasCombatTickWork(RimKataPawnCombatState state)
        {
            // Presence only; the shared weapon pass owns target validity.
            return state != null
                && (state.dualEngagementActive
                    || state.dualCloseCombatActive
                    || state.DraftedFireActive
                    || HasMovementFireCombatWork(state)
                    || state.sharedTargetSearch?.KeepsCombatAlive == true
                    || state.closeAttackRequestTarget != null
                    || state.incomingThreatSource != null
                    || state.DraftedMovementSearchTriggerPending
                    || state.idleProjectileSearchTriggerPending
                    || state.dedicatedFollowupJobPending
                    || state.DodgeMovementActive);
        }

        internal static void TickCombat(Pawn pawn, bool fromJobTracker)
        {
            if (pawn == null)
            {
                return;
            }

            if (fromJobTracker
                && pawn.jobs?.curDriver is JobDriver_RimKataAttack tickedJob
                && tickedJob.TryTakePostTickCombatState(out RimKataPawnCombatState tickedState))
            {
                // Keep handoffs in the original post-tick phase, using the Job's state.
                TryConsumePendingDedicatedFollowupJob(pawn, tickedState);
                return;
            }

            bool hasOwner = RimKataCombatStatePresenceCache.TryGetOwner(
                pawn, out RimKataMapComponent component);
            JobDriver_RimKataAttack combatJob = fromJobTracker
                ? null
                : pawn.jobs?.curDriver as JobDriver_RimKataAttack;
            bool movementSearchAdmitted = false;
            if (!hasOwner && combatJob == null)
            {
                // Untracked ordinary pawns stop before Job or mental-state work.
                // Drafting is only the prerequisite for a new movement search.
                if (!pawn.Drafted || !CanRequestMovementSearch(pawn, null))
                {
                    return;
                }
                movementSearchAdmitted = true;
            }

            if (pawn.InMentalState)
            {
                combatJob?.EndRimKataJobWith(JobCondition.InterruptForced);
                return;
            }

            RimKataPawnCombatState state = component?.GetState(pawn, false);
            Job currentJob = pawn.CurJob;
            if (fromJobTracker)
            {
                bool wasDedicatedJob = currentJob?.def == RimKataDefOf.RimKata_Attack;
                bool hadPendingFollowup = state?.dedicatedFollowupJobPending == true;
                if (hadPendingFollowup)
                {
                    TryConsumePendingDedicatedFollowupJob(pawn, state);
                    currentJob = pawn.CurJob;
                }

                // The Job owns its tick timing, even if a pending handoff changed it.
                if (wasDedicatedJob
                    || currentJob?.def == RimKataDefOf.RimKata_Attack)
                {
                    return;
                }

                if (hadPendingFollowup)
                {
                    // Job callbacks may replace the state or move the pawn.
                    hasOwner = RimKataCombatStatePresenceCache.TryGetOwner(
                        pawn, out component);
                    state = component?.GetState(pawn, false);
                    movementSearchAdmitted = false;
                }
            }

            CombatTickPermissions permissions = new CombatTickPermissions(pawn, currentJob);
            bool allowAutomaticRangedFire = permissions.allowAutomaticRangedFire;

            if (!hasOwner)
            {
                if (combatJob == null && !movementSearchAdmitted)
                {
                    // Only the combat-condition trigger may create idle search work.
                    // Ordinary movement does not need a map or state lookup.
                    if (!pawn.Drafted || !CanRequestMovementSearch(pawn, null))
                    {
                        return;
                    }
                    movementSearchAdmitted = true;
                }

                Map map = pawn.Map;
                if (map == null)
                {
                    combatJob?.EndRimKataJobWith(JobCondition.Succeeded);
                    return;
                }
                component = map.GetComponent<RimKataMapComponent>();
                state = component?.GetState(pawn, false);
            }

            if (!permissions.allowCurrentJob)
            {
                state?.ClearDraftedMovementSearchTracking();
                if (state?.dedicatedFollowupJobPending != true
                    || !state.dedicatedFollowupJobPlayerForced)
                {
                    ReleaseCombatForCurrentJob(pawn, state);
                }
                return;
            }

            bool hasCombatWork = HasCombatTickWork(state);
            bool allowMovementSearch = permissions.AllowsMovementSearch(
                hasCombatWork, combatJob != null);
            if (!allowMovementSearch)
            {
                state?.ClearDraftedMovementSearchTracking();
            }

            if (combatJob != null
                && ConsumeLoadoutInvalidatedCombatJob(pawn, currentJob, state))
            {
                combatJob.EndRimKataJobWith(JobCondition.InterruptForced);
                return;
            }

            if (pawn.IsBurning())
            {
                state?.ClearDraftedMovementSearchTracking();
                if (combatJob != null)
                {
                    combatJob.CancelForFire(state);
                    combatJob.EndRimKataJobWith(JobCondition.InterruptForced);
                }
                else
                {
                    CancelOffenseForFire(pawn, state);
                }
                return;
            }

            if (combatJob != null && RimKataTemporaryInactivity.IsInactive(pawn))
            {
                combatJob.EndRimKataJobWith(JobCondition.InterruptForced);
                return;
            }

            if (combatJob == null
                && !hasCombatWork
                && !movementSearchAdmitted
                && !CanRequestMovementSearch(pawn, state))
            {
                if (state != null)
                {
                    state.draftedMovementSearchAllowed = false;
                }
                return;
            }

            if (combatJob == null
                && state?.dualLastDrivenTick == Find.TickManager.TicksGame)
            {
                return;
            }

            Thing assignedTarget = null;
            bool assignedTargetValid = false;
            bool weaponScopedFocusJob = false;
            bool playerForced = false;
            bool killIncappedTarget = false;
            if (combatJob != null)
            {
                assignedTarget = combatJob.PrepareAssignedTarget(
                    state, out assignedTargetValid, out weaponScopedFocusJob);
            }
            else if (state?.closeAttackRequestTarget != null)
            {
                state.TryGetForcedAttackRequestContext(
                    state.closeAttackRequestTarget,
                    out playerForced,
                    out killIncappedTarget);
            }

            if (!PrepareWeaponCycleTick(pawn, ref state, allowMovementSearch))
            {
                if (combatJob != null)
                {
                    combatJob.EndRimKataJobWith(JobCondition.Succeeded);
                }
                else
                {
                    CancelOffenseForMentalState(pawn, state);
                }
                return;
            }

            if (combatJob != null)
            {
                combatJob.ShareCombatStateWithPostTick(state);
                combatJob.TickPreparedCombat(
                    state, assignedTarget, assignedTargetValid,
                    weaponScopedFocusJob, allowAutomaticRangedFire);
                return;
            }

            TickPreparedWeaponCycles(
                pawn, state, null, playerForced, killIncappedTarget,
                null, false, allowAutomaticRangedFire);
        }

        private static void ReleaseCombatForCurrentJob(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (state == null
                || (!state.DraftedFireActive
                    && !state.WeaponCyclesActive
                    && !(pawn.stances?.curStance is Stance_RimKataAim)))
            {
                return;
            }

            state.CancelDraftedFire(false);
            DeactivateNonJobCycleWork(pawn, state);
            if (pawn.stances?.curStance is Stance_RimKataAim)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }
        }

        internal static void CancelOffenseForFire(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            state?.CancelOffenseForFire();
            if (pawn?.stances?.curStance is Stance_RimKataAim)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }
        }

        public static void Tick(
            Pawn pawn,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool closeCombatContext,
            bool closeTargetResolved = false,
            bool allowAutomaticRangedFire = true)
        {
            if (pawn?.InMentalState == true)
            {
                return;
            }
            if (pawn?.Map == null)
            {
                Reset(pawn, true);
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state?.dualLastDrivenTick == Find.TickManager.TicksGame)
            {
                return;
            }
            CombatTickPermissions permissions = new CombatTickPermissions(pawn, pawn.CurJob);
            allowAutomaticRangedFire &= permissions.allowAutomaticRangedFire;
            bool allowMovementSearch = permissions.AllowsMovementSearch(
                assignedTarget != null || HasCombatTickWork(state),
                pawn.CurJobDef == RimKataDefOf.RimKata_Attack);
            if (!allowMovementSearch)
            {
                state?.ClearDraftedMovementSearchTracking();
            }
            if (pawn.IsBurning()
                || !PrepareWeaponCycleTick(pawn, ref state, allowMovementSearch))
            {
                CancelOffenseForMentalState(pawn, state);
                return;
            }

            Thing resolvedCloseTarget = closeTargetResolved && closeCombatContext
                ? assignedTarget
                : null;
            TickPreparedWeaponCycles(
                pawn, state, assignedTarget, playerForced, killIncappedTarget,
                resolvedCloseTarget, closeTargetResolved, allowAutomaticRangedFire);
        }

        internal static void TickPreparedWeaponCycles(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            Thing resolvedCloseTarget,
            bool closeTargetResolutionKnown,
            bool allowAutomaticRangedFire)
        {
            int currentTick = Find.TickManager.TicksGame;
            if (state.dualLastDrivenTick == currentTick)
            {
                return;
            }
            bool randomAttackEnabled =
                RimKataTargetAccess.SettingsFor(pawn)?.randomAttackEnabled != false;

            BindCurrentWeapons(pawn, state, true);
            bool ordinaryAttackAllowed = state.primaryWeaponCycle.ordinaryWeaponEnabled
                || state.secondaryWeaponCycle.ordinaryWeaponEnabled;

            if (NormalizeInvalidInterceptionState(pawn, state)
                && allowAutomaticRangedFire)
            {
                state.QueueIdleProjectileSearchTrigger();
            }

            if (state.primaryWeaponCycle.weapon != null)
            {
                state.primaryWeaponCycle.ClearInvalidVisualTarget(pawn);
            }
            if (state.secondaryWeaponCycle.weapon != null)
            {
                state.secondaryWeaponCycle.ClearInvalidVisualTarget(pawn);
            }

            Thing closeTarget = ResolveTickCloseTarget(
                pawn,
                state,
                assignedTarget,
                playerForced,
                killIncappedTarget,
                ordinaryAttackAllowed,
                resolvedCloseTarget,
                closeTargetResolutionKnown);
            bool closeCombatContext = closeTarget != null;
            if (closeCombatContext)
            {
                assignedTarget = closeTarget;
            }
            HandleCloseCombatTransition(
                pawn,
                state,
                closeCombatContext,
                closeTarget,
                !playerForced);

            CycleVerbAvailability primaryAvailability = default;
            CycleVerbAvailability secondaryAvailability = default;
            if ((state.primaryWeaponCycle.weapon != null
                && NormalizeUnavailableCycleWork(
                    pawn, state, state.primaryWeaponCycle, randomAttackEnabled,
                    ref primaryAvailability))
                | (state.secondaryWeaponCycle.weapon != null
                && NormalizeUnavailableCycleWork(
                    pawn, state, state.secondaryWeaponCycle, randomAttackEnabled,
                    ref secondaryAvailability)))
            {
                state.ResetCandidateSaturationExpansion(true);
            }

            bool candidateWorkAdvanced = false;
            if (state.idleProjectileSearchTriggerPending)
            {
                if (allowAutomaticRangedFire && state.primaryWeaponCycle.weapon != null)
                {
                    TryCacheSharedCandidate(
                        pawn,
                        state,
                        state.primaryWeaponCycle,
                        assignedTarget,
                        randomAttackEnabled);
                }
                if (allowAutomaticRangedFire && state.secondaryWeaponCycle.weapon != null)
                {
                    TryCacheSharedCandidate(
                        pawn,
                        state,
                        state.secondaryWeaponCycle,
                        assignedTarget,
                        randomAttackEnabled);
                }
                state.ConsumeIdleProjectileSearchTrigger();
                candidateWorkAdvanced = true;
            }

            if (state.sharedTargetSearch?.scanActive == true
                || RimKataSharedTargetSearch.HasPendingCandidates(state))
            {
                AdvanceSharedTargetSearch(
                    pawn, state, assignedTarget,
                    ref primaryAvailability, ref secondaryAvailability);
                candidateWorkAdvanced = true;
            }

            bool firePausedForDodge = ShouldPauseFireForDodge(pawn);
            if (!firePausedForDodge || candidateWorkAdvanced)
            {
                RefreshDualEngagementState(pawn, state, randomAttackEnabled,
                    ref primaryAvailability, ref secondaryAvailability);
            }
            if (!firePausedForDodge && !state.dualEngagementActive)
            {
                state.CancelDraftedFire(false);
                CancelUnfiredWarmupForDraftChange(state.primaryWeaponCycle);
                CancelUnfiredWarmupForDraftChange(state.secondaryWeaponCycle);
                state.ResetCandidateSaturationExpansion(true);
                RearmOpeningOwnerIfBothWaiting(state);
                if (pawn.pather?.Moving != true)
                {
                    state.ClearDraftedMovementSearchTracking();
                }
                UpdateBodyAimStance(pawn, state);
                return;
            }

            state.dualLastDrivenTick = currentTick;

            ImportLegacyDraftedState(state);

            if (state.primaryWeaponCycle.weapon != null)
            {
                state.primaryWeaponCycle.TickTimers();
            }
            if (state.secondaryWeaponCycle.weapon != null)
            {
                state.secondaryWeaponCycle.TickTimers();
            }
            RearmOpeningOwnerIfBothWaiting(state);
            if (firePausedForDodge)
            {
                return;
            }
            if (MovementBlocksFire(pawn, state))
            {
                if (state.primaryWeaponCycle.weapon != null)
                {
                    InterruptCycleForMovement(pawn, state.primaryWeaponCycle);
                }
                if (state.secondaryWeaponCycle.weapon != null)
                {
                    InterruptCycleForMovement(pawn, state.secondaryWeaponCycle);
                }
                return;
            }

            bool blockedByStance = StanceBlocksRimKata(pawn);
            ThingWithComps weaponScopedFocusJobWeapon =
                ResolveWeaponScopedFocusJobWeapon(
                    pawn,
                    state,
                    assignedTarget,
                    playerForced,
                    killIncappedTarget);
            Job drivenJob = pawn.CurJob;
            RimKataWeaponCycleState firstCycle = state.primaryWeaponCycle.weapon != null
                ? state.primaryWeaponCycle
                : state.secondaryWeaponCycle.weapon != null
                    ? state.secondaryWeaponCycle
                    : null;
            Thing firstPromotionTarget = null;
            if (firstCycle != null)
            {
                TickWeaponCycle(
                    pawn, state, firstCycle, assignedTarget, playerForced,
                    killIncappedTarget, closeCombatContext, blockedByStance,
                    out firstPromotionTarget, allowAutomaticRangedFire,
                    randomAttackEnabled, currentTick, weaponScopedFocusJobWeapon,
                    ref (firstCycle == state.primaryWeaponCycle
                        ? ref primaryAvailability : ref secondaryAvailability));
            }

            if (state.weaponBindingsDirty
                || state.weaponConfigurationRevision
                    != RimKataEquipmentUtility.WeaponConfigurationRevision)
            {
                BindCurrentWeapons(pawn, state);
                primaryAvailability = default;
                secondaryAvailability = default;
            }
            // Slot ownership can change when bindings are refreshed.
            RimKataWeaponCycleState secondCycle = state.primaryWeaponCycle != firstCycle
                && state.primaryWeaponCycle.weapon != null
                    ? state.primaryWeaponCycle
                    : state.secondaryWeaponCycle != firstCycle
                        && state.secondaryWeaponCycle.weapon != null
                            ? state.secondaryWeaponCycle
                            : null;
            Thing secondPromotionTarget = null;
            if (secondCycle != null)
            {
                TickWeaponCycle(
                    pawn, state, secondCycle, assignedTarget, playerForced,
                    killIncappedTarget, closeCombatContext, StanceBlocksRimKata(pawn),
                    out secondPromotionTarget, allowAutomaticRangedFire,
                    randomAttackEnabled, currentTick, weaponScopedFocusJobWeapon,
                    ref (secondCycle == state.primaryWeaponCycle
                        ? ref primaryAvailability : ref secondaryAvailability));
            }
            if ((firstPromotionTarget != null || secondPromotionTarget != null)
                && pawn.CurJob == drivenJob)
            {
                TryPromoteAutomaticJobTarget(
                    pawn, state, assignedTarget, playerForced,
                    state.primaryWeaponCycle == firstCycle
                        ? firstPromotionTarget : secondPromotionTarget,
                    state.secondaryWeaponCycle == firstCycle
                        ? firstPromotionTarget : secondPromotionTarget,
                    randomAttackEnabled, out Thing _);
            }
            if (pawn.CurJob != drivenJob || state.weaponBindingsDirty)
            {
                primaryAvailability = default;
                secondaryAvailability = default;
            }
            RefreshDualEngagementState(pawn, state, randomAttackEnabled,
                ref primaryAvailability, ref secondaryAvailability);
            UpdateBodyAimStance(pawn, state);
        }

        private static ThingWithComps ResolveWeaponScopedFocusJobWeapon(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget)
        {
            Job job = pawn?.CurJob;
            if (pawn?.Drafted == true
                || state == null
                || assignedTarget == null
                || !playerForced
                || killIncappedTarget
                || job?.def != RimKataDefOf.RimKata_Attack
                || job.targetA.Thing != assignedTarget)
            {
                return null;
            }

            ThingWithComps weapon =
                job.verbToUse?.EquipmentSource as ThingWithComps;
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            return cycle != null
                && cycle.focusedTarget == assignedTarget
                && cycle.focusedTargetFromAttackGizmo
                    ? weapon
                    : null;
        }

        internal static bool IsWeaponScopedFocusJob(
            Pawn pawn,
            Thing assignedTarget)
        {
            return IsWeaponScopedFocusJob(pawn, StateFor(pawn, false), assignedTarget);
        }

        internal static bool IsWeaponScopedFocusJob(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget)
        {
            Job job = pawn?.CurJob;
            return ResolveWeaponScopedFocusJobWeapon(
                    pawn,
                    state,
                    assignedTarget,
                    job?.playerForced == true,
                    job?.killIncappedTarget == true)
                != null;
        }
        // !!! Debug HUD !!!
        public static bool TryGetDebugState(
            Pawn pawn,
            out char primaryState,
            out char secondaryState,
            out bool dualEngagementActive,
            out bool combatActive)
        {
            primaryState = 'W';
            secondaryState = 'W';
            dualEngagementActive = false;

            RimKataPawnCombatState state = StateFor(pawn, false);

            if (state != null)
            {
                primaryState = state.primaryWeaponCycle?.DebugState ?? 'W';
                secondaryState = state.secondaryWeaponCycle?.DebugState ?? 'W';
                dualEngagementActive = state.dualEngagementActive;
            }

            combatActive = state?.dualEngagementActive == true;

            return state != null || combatActive;
        }

        internal static bool DebugTryGetExistingUsingState(
            Pawn pawn,
            out bool usingRimKata)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            usingRimKata = state?.dualEngagementActive == true;
            return state != null;
        }

        public static void GetDebugWeaponState(
            Pawn pawn,
            ThingWithComps weapon,
            out char debugState,
            out bool vanillaOpeningState)
        {
            debugState = 'W';
            vanillaOpeningState = false;
            if (pawn == null || weapon == null)
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(pawn, weapon);
            if (verb == null)
            {
                return;
            }

            if (verb.Bursting)
            {
                debugState = 'F';
                vanillaOpeningState = RimKataFireContext.ActiveVerb != verb;
                return;
            }

            if (pawn.stances?.curStance is Stance_Warmup warmup
                && warmup.verb == verb)
            {
                debugState = 'A';
                vanillaOpeningState = true;
                return;
            }

            if (pawn.stances?.curStance is Stance_Cooldown cooldown
                && cooldown.verb == verb)
            {
                debugState = 'C';
                vanillaOpeningState = true;
                return;
            }

            debugState = cycle?.DebugState ?? 'W';
            vanillaOpeningState = cycle?.openingWarmupPending == true
                || (cycle?.cooldownFromVanillaOpening == true
                    && (cycle.cooldownTicksRemaining > 0
                        || cycle.NativeAttackPending));
        }

        public static bool DebugSharedSearchActive(Pawn pawn)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);

            return state?.sharedTargetSearch?.scanActive == true;
        }

        public static bool NotifyPlayerWeaponTarget(
            Pawn pawn,
            Verb verb,
            Thing target,
            bool fromAttackGizmo = false)
        {
            if (!CanUsePlayerWeaponCommand(pawn, verb)
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || (target is Pawn targetPawn
                    && !RimKataTargeting.IsPawnTargetStateValid(targetPawn)))
            {
                return false;
            }

            ThingWithComps weapon = verb.EquipmentSource as ThingWithComps;
            RimKataPawnCombatState state = StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            if (cycle == null)
            {
                return false;
            }

            cycle.focusedTarget = target;
            cycle.focusedTargetFromAttackGizmo = fromAttackGizmo;

            cycle.visualTarget = target;
            cycle.visualAimTicksRemaining = Mathf.Max(
                cycle.visualAimTicksRemaining,
                2);
            state.engagementOwnerWeapon = weapon;
            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;

            return true;
        }

        public static bool CanUsePlayerWeaponCommand(Pawn pawn, Verb verb)
        {
            if (pawn?.Map == null
                || verb == null
                || verb.IsMeleeAttack
                || !pawn.IsPlayerControlled
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return false;
            }

            ThingWithComps weapon = verb.EquipmentSource as ThingWithComps;
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(
                pawn,
                primary,
                true)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                : null;
            return weapon != null
                && (weapon == primary || weapon == secondary)
                && verb.CasterPawn == pawn;
        }

        private static bool TryGetFocusedWeaponTarget(
            Pawn pawn,
            RimKataPawnCombatState state,
            ThingWithComps weapon,
            out Thing target,
            out bool fromAttackGizmo)
        {
            target = null;
            fromAttackGizmo = false;
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            if (!IsLiveFocusedTarget(pawn, cycle))
            {
                return false;
            }

            target = cycle.focusedTarget;
            fromAttackGizmo = cycle.focusedTargetFromAttackGizmo;
            return true;
        }

        public static bool TryNotifyPlayerMeleeCloseTarget(
            Pawn pawn,
            Thing target,
            bool fromAttackGizmo)
        {
            if (!CanNotifyPlayerMeleeCloseTarget(pawn, target))
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            state.RequestCloseAttack(target, fromAttackGizmo);
            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;

            return state.CloseAttackRequestActive;
        }

        public static bool CanNotifyPlayerMeleeCloseTarget(
            Pawn pawn,
            Thing target)
        {
            return pawn?.Map != null
                && pawn.IsPlayerControlled
                && RimKataEligibility.CanBeginGunKataAttack(pawn)
                && HasUsableWeapon(pawn, true, true)
                && target != null
                && target != pawn
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn.Map
                && RimKataTargeting.IsAutomaticEnemy(pawn, target)
                && target is Pawn targetPawn
                && RimKataTargeting.IsPawnTargetStateValid(targetPawn)
                && pawn.CanReachImmediate(target, PathEndMode.Touch);
        }

        private static bool TryGetAttackGizmoCloseTarget(
            RimKataPawnCombatState state,
            out Thing target)
        {
            target = null;
            if (state?.closeAttackRequestFromAttackGizmo != true
                || !state.CloseAttackRequestActive)
            {
                return false;
            }

            target = state.closeAttackRequestTarget;
            return target != null;
        }

        private static bool IsLiveFocusedTarget(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            Thing target = cycle?.focusedTarget;
            return pawn?.Map != null
                && target != null
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn.Map
                && (!(target is Pawn targetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(targetPawn));
        }

        public static bool CanOrderRangedCloseAttack(
            Pawn pawn,
            Verb verb,
            Thing target)
        {
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || pawn?.Map == null
                || pawn.InMentalState
                || !RimKataEligibility.CanBeginGunKataAttack(pawn)
                || verb == null
                || verb.IsMeleeAttack
                || target == null
                || target == pawn
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || !target.HostileTo(pawn)
                || target is Pawn targetPawn
                    && !RimKataTargeting.IsPawnTargetStateValid(
                        targetPawn,
                        true)
                || !pawn.CanReachImmediate(target, PathEndMode.Touch)
                || !VerbUsable(pawn, verb, true))
            {
                return false;
            }

            ThingWithComps weapon = verb.EquipmentSource as ThingWithComps;
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            return weapon != null
                && (weapon == primary || weapon == secondary);
        }

        public static bool BeginPlayerRangedCloseAttack(
            Pawn pawn,
            Verb verb,
            Thing target)
        {
            if (pawn?.InMentalState == true
                || !CanOrderRangedCloseAttack(pawn, verb, target)
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            RimKataWeaponCycleState cycle = CycleForWeapon(
                state,
                verb.EquipmentSource as ThingWithComps);
            if (cycle == null)
            {
                return false;
            }

            state.RequestCloseAttack(target);
            HandleCloseCombatTransition(
                pawn,
                state,
                true,
                target,
                false);
            cycle.cachedCandidateTarget = null;
            cycle.cachedCandidateInterception = false;
            SetCandidate(cycle, target, false, true, true, true);
            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;

            return true;
        }

        internal static bool CanReceiveDormantMovingHostiles(Pawn pawn)
        {
            return pawn?.Drafted == true
                && pawn.Spawned && !pawn.Dead && !pawn.Downed
                && pawn.pather?.Moving == true
                && RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                && pawn.drafter?.FireAtWill == true
                && pawn.IsPlayerControlled
                && RimKataTargetAccess.SettingsFor(pawn)?.randomAttackEnabled != false
                && MovingFireEnabledForPawn(pawn)
                && RimKataDraftedFireController.IsAutomaticFireJob(pawn.CurJobDef)
                && !pawn.InMentalState
                && !RimKataTemporaryInactivity.IsInactive(pawn)
                && pawn.Awake()
                && !pawn.WorkTagIsDisabled(WorkTags.Violent);
        }

        internal static bool TryReceiveDormantMovingHostiles(
            Pawn pawn,
            IReadOnlyList<Pawn> movingHostiles)
        {
            if (!CanReceiveDormantMovingHostiles(pawn)
                || pawn.Map == null
                || movingHostiles == null
                || movingHostiles.Count == 0
                || Find.TickManager?.slower?.ForcedNormalSpeed != false)
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state?.sharedTargetSearch?.scanActive == true
                || HasMovementFireCombatWork(state))
            {
                return false;
            }

            if (state != null)
            {
                BindCurrentWeapons(pawn, state, true);
            }
            ThingWithComps primary = state != null
                ? state.primaryWeaponCycle.weapon
                : RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary =
                state != null ? state.secondaryWeaponCycle.weapon
                : RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    pawn,
                    primary,
                    true)
                    ? RimKataWeaponSlotUtility
                        .SecondaryWeaponWithVerifiedAccess(pawn)
                    : null;
            Verb primaryVerb = state != null ? state.primaryWeaponCycle.boundVerb
                : RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            Verb secondaryVerb = state != null ? state.secondaryWeaponCycle.boundVerb
                : RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            float primaryRadius = DormantCandidateRadius(pawn, primary, primaryVerb);
            float secondaryRadius = DormantCandidateRadius(pawn, secondary, secondaryVerb);
            bool? primaryUsable = null;
            bool? secondaryUsable = null;
            bool accepted = false;

            for (int i = 0; i < movingHostiles.Count; i++)
            {
                Pawn target = movingHostiles[i];
                if (target == null || target.Map != pawn.Map
                    || target.pather?.Moving != true)
                {
                    continue;
                }
                int distanceSquared = pawn.Position.DistanceToSquared(target.Position);
                bool primaryInRange = primaryRadius > 0f
                    && distanceSquared <= primaryRadius * primaryRadius;
                bool secondaryInRange = secondaryRadius > 0f
                    && distanceSquared <= secondaryRadius * secondaryRadius;
                if (!primaryInRange && !secondaryInRange)
                {
                    continue;
                }
                if (primaryInRange && !primaryUsable.HasValue)
                    primaryUsable = RimKataEquipmentUtility.IsWeaponEnabled(primary.def)
                        && VerbUsable(pawn, primaryVerb, false);
                if (secondaryInRange && !secondaryUsable.HasValue)
                    secondaryUsable = RimKataEquipmentUtility.IsWeaponEnabled(secondary.def)
                        && VerbUsable(pawn, secondaryVerb, false);
                primaryInRange &= primaryUsable == true;
                secondaryInRange &= secondaryUsable == true;
                if (!primaryInRange && !secondaryInRange)
                {
                    continue;
                }
                if (state == null)
                {
                    state = StateFor(pawn, true);
                    BindCurrentWeapons(pawn, state, true);
                }
                accepted |= RimKataSharedTargetSearch.EnqueueDormantMovingTarget(
                    pawn, state, target, primaryInRange, secondaryInRange);
            }
            // Pending validation schedules the existing state. It neither
            // starts a geometric scan nor declares a weapon action/combat icon.
            return accepted;
        }

        private static float DormantCandidateRadius(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb)
        {
            return weapon != null && verb != null && !verb.IsMeleeAttack
                ? Mathf.Max(0f, RimKataRangeUtility.ResolveCandidateCellRadius(
                    pawn, weapon, verb))
                : 0f;
        }

        private static bool PrepareMovementSearch(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            IntVec3 currentCell = pawn.Position;
            IntVec3 previousCell = state.draftedMovementSearchCell;
            bool movingFireEnabled = MovingFireEnabledForPawn(pawn);
            bool movedToAnotherCell = previousCell.IsValid
                && previousCell != currentCell;
            state.draftedMovementSearchCell = currentCell;
            if (movingFireEnabled
                && (pawn.pather?.MovingNow == true || movedToAnotherCell)
                && HasMovementFireCombatWork(state))
            {
                state.RefreshMovementFireContinuity();
            }

            // Preserve movement continuity above even when new search is not allowed.
            // Common preparation has already admitted the Pawn and supplied its state.
            bool movementSearchAllowed = CanRequestMovementSearch(pawn, state, true);
            bool movementSearchRequested = movementSearchAllowed
                && (!state.draftedMovementSearchAllowed
                    || movedToAnotherCell);
            state.draftedMovementSearchAllowed = movementSearchAllowed;
            if (!movementSearchAllowed)
            {
                state.ConsumeDraftedMovementSearchTrigger();
                return false;
            }

            bool searchInProgress = MovementSearchInProgress(state);
            if (state.DraftedMovementSearchTriggerPending)
            {
                if (searchInProgress)
                {
                    return true;
                }

                if (TryBeginMovementSearch(pawn, state, currentCell, true))
                {
                    state.ConsumeDraftedMovementSearchTrigger();
                    return true;
                }

                if (LongestAutomaticCandidateCellRadiusVerb(pawn) == null)
                {
                    state.ConsumeDraftedMovementSearchTrigger();
                    return false;
                }

                return true;
            }

            if (!movementSearchRequested)
            {
                return false;
            }

            if (searchInProgress)
            {
                state.QueueDraftedMovementSearchTrigger();
                return true;
            }

            return TryBeginMovementSearch(pawn, state, currentCell, true);
        }

        private static bool HasMovementSearchCandidates(RimKataPawnCombatState state)
        {
            // Candidate presence is sufficient; admission already owns validation.
            return state?.primaryWeaponCycle?.HasAutomaticCandidates == true
                || state?.secondaryWeaponCycle?.HasAutomaticCandidates == true;
        }

        private static bool CanRequestMovementSearch(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool attackEligibilityVerified = false)
        {
            return pawn?.Drafted == true
                && (attackEligibilityVerified
                    || RimKataEligibility.HasActiveRimKataAccess(pawn))
                && pawn.drafter?.FireAtWill == true
                && RimKataTargetAccess.SettingsFor(pawn)?.randomAttackEnabled != false
                && MovingFireEnabledForPawn(pawn)
                && (pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                    || RimKataDraftedFireController.IsAutomaticFireJob(pawn.CurJobDef))
                && Find.TickManager?.slower?.ForcedNormalSpeed == true
                && !HasMovementSearchCandidates(state)
                && pawn.pather?.Moving == true;
        }

        private static bool HasMovementFireCombatWork(
            RimKataPawnCombatState state)
        {
            return state?.primaryWeaponCycle?.CombatActive == true
                || state?.secondaryWeaponCycle?.CombatActive == true;
        }

        internal static bool CanReceiveProjectileWake(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (pawn.IsPlayerControlled)
            {
                return true;
            }

            // Use the AI's issued combat/pursuit work, not a new detection radius.
            ThinkNode jobGiver = pawn.CurJob?.jobGiver;
            if (jobGiver is JobGiver_AIFightEnemy
                || jobGiver is JobGiver_AIGotoTarget
                || jobGiver is JobGiver_AIGotoNearestHostile
                || pawn.mindState?.duty?.def == DutyDefOf.AssaultColony)
            {
                return true;
            }

            // Sapper/breacher escorts have a different duty during the same assault.
            LordToil assault = pawn.GetLord()?.CurLordToil;
            return assault is LordToil_AssaultColonySappers
                || assault is LordToil_AssaultColonyBreaching;
        }

        private static bool HasBusyAttackStance(Pawn pawn)
        {
            return pawn?.stances?.curStance is Stance_Busy busy
                && busy.verb != null && busy.ticksLeft > 0;
        }

        internal static bool CanReceiveIdleProjectileWakeNow(Pawn pawn)
        {
            if (pawn?.Map == null
                || !pawn.Spawned
                || pawn.Dead
                || pawn.Downed
                || !pawn.Awake()
                || pawn.InMentalState
                || pawn.IsBurning()
                || !CanReceiveProjectileWake(pawn)
                || HasBusyAttackStance(pawn)
                || !RimKataEligibility.CanUseProjectileInterception(pawn))
            {
                return false;
            }

            bool moving = pawn.pather?.MovingNow == true;
            Job currentJob = pawn.CurJob;
            if (pawn.Drafted)
            {
                if (pawn.drafter?.FireAtWill != true || moving)
                {
                    return false;
                }
            }
            else if (currentJob?.playerForced == true
                || pawn.carryTracker?.CarriedThing != null)
            {
                return false;
            }

            JobDef jobDef = pawn.CurJobDef;
            return moving
                || jobDef == JobDefOf.Wait
                || jobDef == JobDefOf.Wait_Combat
                || jobDef?.defName == "Wait_MaintainPosture"
                || jobDef?.defName == "GotoWander"
                || jobDef?.defName == "Wait_Wander"
                || (!pawn.IsPlayerControlled
                    && (jobDef == JobDefOf.Goto
                        || jobDef == JobDefOf.AttackStatic
                        || jobDef == JobDefOf.AttackMelee));
        }

        internal static float ProjectileWakeRange(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb)
        {
            // The allowed-equipment list limits ordinary RimKata attacks, not
            // explosive-projectile interception.  Keep that established
            // boundary here as well as in the exact candidate selection path.
            if (weapon == null
                || !(verb is Verb_LaunchProjectile)
                || !VerbUsable(pawn, verb, false))
            {
                return 0f;
            }

            return Mathf.Max(
                0f,
                RimKataRangeUtility.ResolveEffectiveRange(
                    pawn,
                    weapon,
                    verb));
        }

        public static void QueueIdleProjectileSearch(Pawn pawn)
        {
            RimKataMapComponent mapComponent =
                pawn?.Map?.GetComponent<RimKataMapComponent>();
            if (mapComponent?.HasActiveExplosiveProjectiles != true
                || !CanReceiveIdleProjectileWakeNow(pawn))
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state != null)
            {
                NormalizeInvalidInterceptionState(pawn, state);
            }
            if (HasCombatContinuity(pawn, state))
            {
                return;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary =
                RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    pawn,
                    primary,
                    true)
                    ? RimKataWeaponSlotUtility
                        .SecondaryWeaponWithVerifiedAccess(pawn)
                    : null;
            Verb primaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            Verb secondaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            if (primaryVerb?.Bursting == true || secondaryVerb?.Bursting == true)
            {
                return;
            }
            if (!(primaryVerb is Verb_LaunchProjectile)
                && !(secondaryVerb is Verb_LaunchProjectile))
            {
                return;
            }

            Projectile primaryProjectile = null;
            float primaryRange = ProjectileWakeRange(
                pawn,
                primary,
                primaryVerb);
            if (primaryRange > 0f)
            {
                mapComponent.TryGetValidHostileProjectile(
                    pawn,
                    primaryVerb,
                    primaryRange * primaryRange,
                    out primaryProjectile);
            }

            Projectile secondaryProjectile = null;
            float secondaryRange = ProjectileWakeRange(
                pawn,
                secondary,
                secondaryVerb);
            if (secondaryRange > 0f)
            {
                mapComponent.TryGetValidHostileProjectile(
                    pawn,
                    secondaryVerb,
                    secondaryRange * secondaryRange,
                    out secondaryProjectile);
            }
            if (primaryProjectile == null && secondaryProjectile == null)
            {
                return;
            }

            state ??= StateFor(pawn, true);
            BindCurrentWeapons(pawn, state, true);
            state.QueueIdleProjectileSearchTrigger();
            bool cachedProjectile = TrySeedIdleProjectileCandidate(
                    state.primaryWeaponCycle,
                    primary,
                    primaryProjectile)
                | TrySeedIdleProjectileCandidate(
                    state.secondaryWeaponCycle,
                    secondary,
                    secondaryProjectile);
            if (!cachedProjectile)
            {
                state.ConsumeIdleProjectileSearchTrigger();
                RefreshDualEngagementState(pawn, state);
                return;
            }

            RefreshDualEngagementState(pawn, state);
            if (pawn.Drafted)
            {
                return;
            }

            state.projectileWakeResumeJob = pawn.CurJob;
            QueueDedicatedFollowupJob(pawn, null);
            if (!state.dedicatedFollowupJobPending)
            {
                state.projectileWakeResumeJob = null;
            }
        }

        private static bool TrySeedIdleProjectileCandidate(
            RimKataWeaponCycleState cycle,
            ThingWithComps expectedWeapon,
            Projectile projectile)
        {
            if (cycle == null
                || cycle.weapon != expectedWeapon
                || projectile == null
                || cycle.cachedCandidateTarget != null
                || cycle.HasPlan
                || cycle.openingWarmupPending
                || cycle.NativeAttackPending)
            {
                return false;
            }

            cycle.cachedCandidateTarget = projectile;
            cycle.cachedCandidateInterception = true;
            return true;
        }

        private static bool MovementSearchInProgress(RimKataPawnCombatState state)
        {
            return state?.sharedTargetSearch?.scanActive == true;
        }

        private static bool TryBeginMovementSearch(
            Pawn pawn,
            RimKataPawnCombatState state,
            IntVec3 origin,
            bool movementSearchAdmitted = false)
        {
            if (!movementSearchAdmitted && !CanRequestMovementSearch(pawn, state))
            {
                state.ConsumeDraftedMovementSearchTrigger();
                state.draftedMovementSearchAllowed = false;
                return false;
            }
            BindCurrentWeapons(pawn, state);
            if (!RimKataSharedTargetSearch.Begin(pawn, state, origin))
            {
                return false;
            }
            return true;
        }

        public static void ClearDraftedMovementTracking(Pawn pawn)
        {
            StateFor(pawn, false)?.ClearDraftedMovementSearchTracking();
        }

        internal static bool AutomaticRangedFireAllowed(Pawn pawn)
        {
            return pawn?.Drafted != true || pawn.drafter?.FireAtWill == true;
        }

        internal static void NotifyFireAtWillChanged(Pawn pawn)
        {
            if (pawn?.Drafted != true || pawn.Map == null)
            {
                return;
            }

            bool allowed = AutomaticRangedFireAllowed(pawn);
            bool combatJobAllowed = pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                || RimKataDraftedFireController.IsAutomaticFireJob(pawn.CurJobDef);
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state == null)
            {
                if (!allowed || !combatJobAllowed
                    || !RimKataEligibility.CanBeginGunKataAttack(pawn))
                {
                    return;
                }
                state = StateFor(pawn, true);
                if (state == null)
                {
                    return;
                }
            }

            BindCurrentWeapons(pawn, state);
            if (state.primaryWeaponCycle.boundVerb?.IsMeleeAttack != false
                && state.secondaryWeaponCycle.boundVerb?.IsMeleeAttack != false)
            {
                return;
            }

            state.CancelDraftedFire(false);
            state.ClearDraftedMovementSearchTracking();
            state.ResetCandidateSaturationExpansion(true);
            state.sharedTargetSearch?.Reset();

            if (!allowed)
            {
                ClearRangedTargetingForHoldFire(pawn, state.primaryWeaponCycle);
                ClearRangedTargetingForHoldFire(pawn, state.secondaryWeaponCycle);
                state.ClearDedicatedFollowupJobRequest();
                state.dedicatedContinuityTarget = null;
                state.dedicatedContinuityUntilTick = -1;
                Thing closeTarget = ResolveCloseTarget(
                    pawn, state, pawn.CurJob?.targetA.Thing, false, false);

                // Drafted pawns have no automatic rush authority. End the old
                // attack Job instead of retaining a target with no ranged plan.
                if (pawn.jobs?.curDriver is JobDriver_RimKataAttack driver)
                {
                    driver.EndRimKataJobWith(JobCondition.InterruptForced);
                }
                if (closeTarget != null)
                {
                    state.RequestCloseAttack(closeTarget);
                    HandleCloseCombatTransition(pawn, state, true, closeTarget);
                    // An unchanged close context must refill the cleared gun
                    // slot too, so it can start a physical melee action.
                    RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                        pawn, state, closeTarget);
                }
            }
            else if (combatJobAllowed && !pawn.IsBurning()
                && RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                // Permission changes must also wake stationary, empty slots.
                RimKataSharedTargetSearch.Begin(pawn, state, pawn.Position);
                if (state.ownerComponent?.HasActiveExplosiveProjectiles == true)
                {
                    state.QueueIdleProjectileSearchTrigger();
                }
            }

            RearmOpeningOwnerIfBothWaiting(state);
            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;
            UpdateBodyAimStance(pawn, state);
        }

        private static void ClearRangedTargetingForHoldFire(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            bool preserveFocusedTarget = false)
        {
            Verb verb = cycle?.boundVerb;
            if (cycle?.weapon == null || verb == null || verb.IsMeleeAttack)
            {
                return;
            }

            cycle.ClearAutomaticCandidates();
            cycle.cachedCandidateTarget = null;
            cycle.cachedCandidateInterception = false;
            if (preserveFocusedTarget && IsLiveFocusedTarget(pawn, cycle))
            {
                return;
            }

            ApplyInterruptedBurstCooldown(pawn, cycle, verb);
            ClearTargetPreservingCycle(cycle);
            cycle.focusedTarget = null;
            cycle.focusedTargetFromAttackGizmo = false;
            cycle.lastFiredTarget = null;
            cycle.firedInCurrentOpening = false;
            cycle.openingWarmupPending = false;
            cycle.openingWarmupBonusTicks = 0;
            cycle.visualTarget = null;
            cycle.visualAimTicksRemaining = 0;
        }

        private static bool MovingFireEnabledForPawn(Pawn pawn)
        {
            return RimKataTargetAccess.SettingsFor(pawn)?.movingFireEnabled != false;
        }

        internal static bool CounterattackControlEnabled(Pawn pawn)
        {
            // Entry uses combat access; individual features gate their own work.
            return RimKataEligibility.CanBeginGunKataAttack(pawn);
        }

        internal static bool ShouldPauseFireForDodge(Pawn pawn)
        {
            return RimKataTargetAccess.SettingsFor(pawn)?.movingFireEnabled == false
                && RimKataDodgeMovementUtility.IsVisualLocked(pawn);
        }

        internal static bool UsesVanillaAutomaticTarget(
            Pawn pawn,
            Thing target,
            RimKataPawnCombatState knownState = null)
        {
            if (target == null
                || target is Pawn
                || target is Projectile
                || pawn == null)
            {
                return false;
            }

            Job job = pawn.CurJob;
            if (job?.playerForced == true && job.targetA.Thing == target)
            {
                return false;
            }

            if (knownState == null
                && RimKataCombatStatePresenceCache.TryGetOwner(
                    pawn, out RimKataMapComponent owner))
            {
                knownState = owner.GetState(pawn, false);
            }

            return knownState?.primaryWeaponCycle?.focusedTarget != target
                && knownState?.secondaryWeaponCycle?.focusedTarget != target;
        }

        public static bool ShouldSuppressVanillaCast(
            Pawn pawn,
            Verb verb,
            LocalTargetInfo target)
        {
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || pawn?.InMentalState == true)
            {
                return false;
            }

            if (RimKataAutomaticCastSuppression.ActiveFor(pawn))
            {
                return true;
            }

            if (pawn?.Map == null
                || verb == null
                || !target.IsValid
                || !target.HasThing
                || RimKataFireContext.ActiveVerb != null)
            {
                return false;
            }

            ThingWithComps weapon = verb.EquipmentSource as ThingWithComps;
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility
                .CanUseSecondarySlot(pawn)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                : null;
            if (weapon == null || (weapon != primary && weapon != secondary))
            {
                return false;
            }

            if (!CounterattackControlEnabled(pawn)
                && IsConfigurableCounterattackOpening(pawn, pawn.CurJob))
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            if (cycle?.cooldownTicksRemaining > 0)
            {
                return true;
            }

            if (UsesVanillaAutomaticTarget(pawn, target.Thing, state))
            {
                return false;
            }

            if (IsDedicatedFollowupActive(pawn))
            {
                return true;
            }

            return !verb.IsMeleeAttack
                && MovingFireEnabledForPawn(pawn)
                && (pawn.pather?.MovingNow == true
                    || state?.MovementFireContinuityActive == true);
        }

        public static void RequestWeaponSwap(Pawn pawn)
        {
            if (pawn?.Map == null)
            {
                return;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility.SecondaryWeapon(pawn);

            if (primary == null || secondary == null)
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            if (IsWeaponSwapBlocked(pawn))
            {
                return;
            }

            state.weaponSwapPending = false;
            RimKataWeaponSlotUtility.TrySwapPrimarySecondary(pawn);
        }

        public static bool IsWeaponSwapBlocked(Pawn pawn)
        {
            if (pawn?.Map == null)
            {
                return true;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            bool cycleBusy = state?.primaryWeaponCycle?.Active == true
                || state?.secondaryWeaponCycle?.Active == true;
            bool matchingBusyStance =
                pawn.stances?.curStance is Stance_Busy busy
                && busy.verb?.EquipmentSource is ThingWithComps busyWeapon
                && (busyWeapon == RimKataWeaponSlotUtility.PrimaryWeapon(pawn)
                    || busyWeapon == RimKataWeaponSlotUtility.SecondaryWeapon(pawn));
            return cycleBusy
                || state?.dedicatedFollowupJobPending == true
                || state?.ResponsePoseActive == true
                || pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                || matchingBusyStance;
        }

        public static bool TryApplyResponseCooldown(
            Pawn pawn,
            ThingWithComps weapon,
            Verb verb,
            LocalTargetInfo focus)
        {
            if (pawn?.Map == null
                || weapon == null
                || verb == null
                || !RimKataEquipmentUtility.IsWeaponEnabled(weapon.def))
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor( pawn, true);
            BindCurrentWeapons(pawn, state);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            Verb boundVerb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                weapon);
            if (cycle == null
                || boundVerb == null
                || boundVerb.EquipmentSource != weapon
                || verb.EquipmentSource != weapon)
            {
                return false;
            }

            verb = boundVerb;

            cycle.ApplyResponseCooldown(
                RimKataCombatMath.CooldownTicksForSingleShot(verb, pawn, true));
            cycle.cooldownFromVanillaOpening = false;

            cycle.openingWarmupBonusTicks = 0;
            cycle.openingWarmupPending = false;


            cycle.ClearPlan();

            bool responseTargetQueued = false;
            if (focus.HasThing
                && cycle.ContainsAutomaticCandidate(focus.Thing))
            {
                cycle.cachedCandidateTarget = focus.Thing;
                cycle.cachedCandidateInterception = false;
                responseTargetQueued = true;
            }

            if (responseTargetQueued)
            {
                cycle.visualTarget = focus.Thing;
                cycle.lastFiredTarget = focus.Thing;
                cycle.visualAimTicksRemaining = Mathf.Max(
                    1,
                    cycle.cooldownTicksRemaining);
            }
            else
            {
                cycle.visualTarget = null;
                cycle.visualAimTicksRemaining = 0;
            }

            RefreshDualEngagementState(pawn, state);

            return true;
        }

        public static bool IsResponseTargetQueued(
            Pawn pawn,
            ThingWithComps weapon,
            LocalTargetInfo focus)
        {
            if (pawn?.Map == null
                || weapon == null
                || !focus.HasThing)
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            return cycle?.cachedCandidateTarget == focus.Thing
                && cycle.ContainsAutomaticCandidate(focus.Thing)
                && !cycle.cachedCandidateInterception;
        }

        public static RimKataVanillaOpeningAttempt PrepareVanillaOpening(
            Pawn pawn,
            Verb verb,
            LocalTargetInfo castTarget)
        {
            RimKataVanillaOpeningAttempt attempt = default(RimKataVanillaOpeningAttempt);
            if (pawn?.Map == null
                || pawn.InMentalState
                || pawn.jobs?.curDriver is JobDriver_Hunt
                || verb == null
                || !castTarget.IsValid
                || !castTarget.HasThing
                || RimKataFireContext.ActiveVerb != null
                || UsesVanillaAutomaticTarget(pawn, castTarget.Thing)
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return attempt;
            }

            ThingWithComps primaryWeapon = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps firedWeapon = verb.EquipmentSource as ThingWithComps;
            ThingWithComps secondaryWeapon = RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                : null;
            if (primaryWeapon == null
                || firedWeapon == null
                || (firedWeapon != primaryWeapon && firedWeapon != secondaryWeapon)
                || verb.CasterPawn != pawn
                || verb.EquipmentSource != firedWeapon)
            {
                return attempt;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (IsDedicatedFollowupActive(pawn))
            {
                return attempt;
            }

            Thing currentTarget = castTarget.Thing;
            if (verb.IsMeleeAttack && pawn.CurJobDef == JobDefOf.AttackMelee)
                currentTarget = pawn.CurJob.targetA.Thing ?? currentTarget;
            bool playerForced = pawn.CurJob?.playerForced == true;
            bool killIncappedTarget = pawn.CurJob?.killIncappedTarget == true;
            bool closeContext = pawn.CanReachImmediate(
                currentTarget,
                PathEndMode.Touch);
            if (!ValidOpeningTarget(
                pawn,
                currentTarget,
                playerForced,
                killIncappedTarget,
                closeContext))
            {
                return attempt;
            }

            attempt.prepared = true;
            attempt.weapon = firedWeapon;
            attempt.target = currentTarget;
            pendingVanillaOpeningVerb = verb;
            return attempt;
        }

        internal static void PrepareVanillaShotData(
            Pawn pawn,
            Verb verb,
            LocalTargetInfo castTarget,
            ref RimKataVanillaOpeningAttempt attempt)
        {
            if (RimKataTargetAccess.SettingsFor(pawn)?.singleShotConversionEnabled != true
                || verb == null
                || verb.IsMeleeAttack
                || RimKataFireContext.ActiveVerb != null
                || (!attempt.prepared
                    && !IsQualifiedNativeSingleShot(
                        pawn,
                        verb,
                        castTarget)))
            {
                // Excluded native attacks must not retain a former conversion.
                // Eligible attacks keep their already prepared data below.
                RimKataPreparedWeaponData.Restore(verb);
                return;
            }

            RimKataNativeAttack.Bind(verb);
        }


        public static void CommitVanillaOpening(
            Pawn pawn,
            Verb verb,
            RimKataVanillaOpeningAttempt attempt)
        {
            if (!attempt.prepared
                || pawn?.Map == null
                || pawn.InMentalState
                || verb == null
                || attempt.weapon == null
                || attempt.target == null)
            {
                return;
            }

            Thing target = attempt.target;
            bool playerForced = pawn.CurJob?.playerForced == true;
            bool killIncappedTarget = pawn.CurJob?.killIncappedTarget == true;
            bool closeContext = pawn.CanReachImmediate(target, PathEndMode.Touch);
            if (verb.EquipmentSource != attempt.weapon
                || !ValidOpeningTarget(
                    pawn,
                    target,
                    playerForced,
                    killIncappedTarget,
                    closeContext))
            {
                return;
            }

            Stance_Warmup warmup = pawn.stances?.curStance as Stance_Warmup;
            bool matchingWarmup = warmup?.verb == verb
                && warmup.focusTarg.HasThing
                && warmup.focusTarg.Thing == target;
            Stance_Cooldown cooldown = pawn.stances?.curStance as Stance_Cooldown;
            bool matchingCooldown = cooldown?.verb == verb
                && cooldown.focusTarg.HasThing
                && cooldown.focusTarg.Thing == target;
            if (!matchingWarmup && !matchingCooldown)
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            RimKataWeaponCycleState openingCycle = CycleForWeapon(
                state,
                attempt.weapon);
            if (openingCycle == null)
            {
                return;
            }

            state.engagementOwnerWeapon = attempt.weapon;

            RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                pawn,
                state,
                target);
            RimKataSharedTargetSearch.Begin(
                pawn,
                state,
                pawn.Position);

            if (matchingWarmup)
            {
                SetCandidate(
                    openingCycle,
                    target,
                    false,
                    closeContext,
                    closeContext,
                    true);
                openingCycle.plannedActionVerb = verb;
                openingCycle.warmupTotalTicks = Mathf.Max(0, warmup.ticksLeft);
                openingCycle.warmupTicksRemaining =
                    openingCycle.warmupTotalTicks;
            }
            else
            {
                int cooldownTicks = Mathf.Max(1, cooldown.ticksLeft);
                openingCycle.cooldownTicksRemaining = cooldownTicks;
                openingCycle.cooldownFromVanillaOpening = true;
                openingCycle.firedInCurrentOpening = true;
                openingCycle.lastFiredTarget = target;
                openingCycle.visualTarget = target;
                openingCycle.visualAimTicksRemaining = cooldownTicks;
                RecordFirstFiredWeapon(state, attempt.weapon);
            }

            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = Find.TickManager.TicksGame;
            pawn.stances.SetStance(new Stance_Mobile());
            if (verb.IsMeleeAttack)
            {
                UpdateBodyAimStance(pawn, state);
            }
            QueueDedicatedFollowupJob(pawn, target);
        }

        public static void FinishVanillaOpeningAttempt(Verb verb)
        {
            if (pendingVanillaOpeningVerb == verb)
            {
                pendingVanillaOpeningVerb = null;
            }
        }

        private static bool ValidOpeningTarget(
            Pawn pawn,
            Thing target,
            bool playerForced,
            bool killIncappedTarget,
            bool closeContext)
        {
            if (pawn?.Map == null
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || (!playerForced
                    && !RimKataTargeting.IsAutomaticEnemy(pawn, target)))
            {
                return false;
            }

            if (target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    playerForced && killIncappedTarget))
            {
                return false;
            }

            if (closeContext)
            {
                return pawn.CanReachImmediate(target, PathEndMode.Touch);
            }

            return RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                pawn,
                target);
        }

        private static bool TargetWithinAutomaticCandidateCellRadius(
            Pawn pawn,
            Thing target)
        {
            if (pawn?.Map == null
                || target == null
                || !target.Spawned
                || target.Map != pawn.Map)
            {
                return false;
            }

            float candidateCellRadius =
                RimKataTargeting.MaximumAutomaticCandidateCellRadius(pawn);
            return candidateCellRadius > 0f
                && pawn.Position.DistanceToSquared(target.Position)
                    <= candidateCellRadius * candidateCellRadius;
        }

        public static bool ShouldConvertVanillaOpeningToSingleShot(Verb verb)
        {
            Pawn pawn = verb?.CasterPawn;
            ThingWithComps weapon = verb?.EquipmentSource as ThingWithComps;
            if (RimKataTargetAccess.SettingsFor(pawn)?.singleShotConversionEnabled != true
                || pawn?.Map == null
                || weapon == null
                || verb.IsMeleeAttack
                || RimKataFireContext.ActiveVerb != null)
            {
                return false;
            }

            return (pendingVanillaOpeningVerb == verb
                    || IsQualifiedNativeSingleShot(
                        pawn,
                        verb,
                        verb.CurrentTarget))
                && RimKataPreparedWeaponData.GetOriginalBurstCount(verb) > 1;
        }

        private static bool IsQualifiedNativeSingleShot(
            Pawn pawn,
            Verb verb,
            LocalTargetInfo castTarget)
        {
            if (pawn?.Map == null
                || verb == null
                || !castTarget.IsValid
                || !castTarget.HasThing)
            {
                return false;
            }

            Job job = pawn.CurJob;
            bool qualifiedHunt = pawn.jobs?.curDriver is JobDriver_Hunt
                && job?.def == JobDefOf.Hunt
                && job.verbToUse == verb
                && castTarget.Thing is Pawn prey
                && job.targetA.Thing == prey
                && !prey.Dead
                && prey.Spawned
                && prey.Map == pawn.Map;
            Thing target = castTarget.Thing;
            if (!qualifiedHunt
                && (target.Destroyed
                    || !target.Spawned
                    || target.Map != pawn.Map
                    || !UsesVanillaAutomaticTarget(pawn, target)))
            {
                return false;
            }

            ThingWithComps firedWeapon =
                verb.EquipmentSource as ThingWithComps;
            if (firedWeapon == null
                || verb.CasterPawn != pawn
                || verb.EquipmentSource != firedWeapon
                || !RimKataEligibility.CanBeginGunKataAttack(pawn)
                || !RimKataEquipmentUtility.IsWeaponEnabled(
                    firedWeapon.def))
            {
                return false;
            }

            ThingWithComps primaryWeapon =
                RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            if (firedWeapon == primaryWeapon)
            {
                return true;
            }

            return RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    pawn,
                    primaryWeapon,
                    true)
                && firedWeapon
                    == RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
        }

        private static void AdvanceSharedTargetSearch(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing currentTarget,
            ref CycleVerbAvailability primaryAvailability,
            ref CycleVerbAvailability secondaryAvailability)
        {
            if (pawn?.Map == null
                || (state?.sharedTargetSearch?.sessionActive != true
                    && !RimKataSharedTargetSearch.HasPendingCandidates(state)))
            {
                return;
            }

            RimKataSharedTargetSearch.Advance(pawn, state, currentTarget);

            if (state.primaryWeaponCycle.weapon != null)
            {
                TryCacheSharedCandidate(
                    pawn, state, state.primaryWeaponCycle, currentTarget,
                    null, null, ref primaryAvailability);
            }
            if (state.secondaryWeaponCycle.weapon != null)
            {
                TryCacheSharedCandidate(
                    pawn, state, state.secondaryWeaponCycle, currentTarget,
                    null, null, ref secondaryAvailability);
            }
            CancelProjectileWakeResumeForCombat(pawn, state);
        }

        private static void CancelProjectileWakeResumeForCombat(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            Job resumeJob = state?.projectileWakeResumeJob;
            if (pawn?.jobs?.jobQueue == null
                || resumeJob == null
                || !HasNonProjectileSearchDemand(pawn, state))
            {
                return;
            }

            pawn.jobs.jobQueue.RemoveAll(
                pawn,
                queuedJob => queuedJob == resumeJob);
            state.projectileWakeResumeJob = null;
        }

        private static bool TryCacheSharedCandidate(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Thing preferredTarget,
            bool? randomAttackEnabled = null,
            Verb preparedVerb = null)
        {
            CycleVerbAvailability availability = default;
            return TryCacheSharedCandidate(
                pawn, state, cycle, preferredTarget,
                randomAttackEnabled, preparedVerb, ref availability);
        }

        private static bool TryCacheSharedCandidate(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Thing preferredTarget,
            bool? randomAttackEnabled,
            Verb preparedVerb,
            ref CycleVerbAvailability availability)
        {
            if (pawn?.Map == null
                || state == null
                || cycle == null
                || cycle.ResponseCooldownAppliedThisTick
                || cycle.HasPlan
                || cycle.openingWarmupPending
                || cycle.NativeAttackPending)
            {
                return false;
            }

            bool closeContext = cycle.plannedCloseContext
                || state?.dualCloseCombatActive == true;
            bool bindingsCurrent = !state.weaponBindingsDirty
                && state.weaponConfigurationRevision
                    == RimKataEquipmentUtility.WeaponConfigurationRevision;
            Verb verb = preparedVerb ?? (bindingsCurrent
                ? BoundCombatVerb(pawn, cycle)
                : RimKataWeaponSlotUtility.CombatVerb(pawn, cycle.weapon));
            if (FocusedTargetUsableNow(
                pawn,
                cycle,
                verb,
                closeContext,
                ref availability))
            {
                return false;
            }

            Thing cachedTarget = cycle.cachedCandidateTarget;
            if (cachedTarget != null)
            {
                return false;
            }

            bool ordinaryWeaponEnabled = bindingsCurrent
                ? cycle.ordinaryWeaponEnabled
                : RimKataEquipmentUtility.IsWeaponEnabled(cycle.weapon?.def);
            bool randomAttack = randomAttackEnabled
                ?? RimKataEligibility.RandomAttackEnabledForPawn(pawn);
            RimKataMapComponent component = state.ownerComponent;
            bool noAutomaticSources = randomAttack
                && !cycle.HasAutomaticCandidates
                && component != null && component.map == pawn.Map
                && !component.HasActiveExplosiveProjectiles;
            if (availability.selectionEmpty && noAutomaticSources)
            {
                return false;
            }
            Thing retainedTarget = cycle.lastFiredTarget;
            if (retainedTarget is Pawn
                && ordinaryWeaponEnabled
                && !randomAttack)
            {
                if (ValidCurrentTargetForVerb(
                        pawn,
                        verb,
                        retainedTarget,
                        false,
                        false,
                        closeContext,
                        ref availability))
                {
                    cycle.cachedCandidateTarget = retainedTarget;
                    cycle.cachedCandidateInterception = false;
                    return true;
                }

                cycle.lastFiredTarget = null;
            }

            Thing candidate;
            bool interception;
            bool selected = bindingsCurrent
                ? RimKataSharedTargetSearch.TrySelectCandidate(
                    pawn, state, cycle, verb, preferredTarget,
                    randomAttack, ordinaryWeaponEnabled,
                    out candidate, out interception, component)
                : RimKataSharedTargetSearch.TrySelectCandidate(
                    pawn, state, verb, preferredTarget,
                    out candidate, out interception);
            if (!selected)
            {
                availability.selectionEmpty = randomAttack
                    && !cycle.HasAutomaticCandidates
                    && component != null && component.map == pawn.Map
                    && !component.HasActiveExplosiveProjectiles;
                return false;
            }

            availability.selectionEmpty = false;
            if (randomAttack && candidate is Pawn)
            {
                availability.checkedTarget = candidate;
                availability.checkedVerb = verb;
                availability.checkedCloseContext = verb.IsMeleeAttack
                    || state.dualCloseCombatActive;
            }
            cycle.cachedCandidateTarget = candidate;
            cycle.cachedCandidateInterception = interception;
            return true;
        }

        private static Verb LongestAutomaticCandidateCellRadiusVerb(Pawn pawn)
        {
            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary = RimKataWeaponSlotUtility
                .CanUseSecondarySlot(pawn)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                : null;
            Verb primaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            Verb secondaryVerb = RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            float primaryCandidateCellRadius =
                primaryVerb != null
                && !primaryVerb.IsMeleeAttack
                ? RimKataRangeUtility.ResolveCandidateCellRadius(
                    pawn,
                    primary,
                    primaryVerb)
                : -1f;
            float secondaryCandidateCellRadius =
                secondaryVerb != null
                && !secondaryVerb.IsMeleeAttack
                ? RimKataRangeUtility.ResolveCandidateCellRadius(
                    pawn,
                    secondary,
                    secondaryVerb)
                : -1f;
            return secondaryCandidateCellRadius > primaryCandidateCellRadius
                ? secondaryVerb
                : primaryCandidateCellRadius >= 0f
                    ? primaryVerb
                    : null;
        }

        public static void NotifyDefensiveCombatEvent(Pawn pawn, Thing attacker)
        {
            if (pawn?.Map == null
                || pawn.InMentalState
                || attacker == null
                || attacker == pawn
                || UsesVanillaAutomaticTarget(pawn, attacker)
                || !RimKataEligibility.CanBeginGunKataAttack(pawn)
                || attacker.Destroyed
                || !attacker.Spawned
                || attacker.Map != pawn.Map
                || !RimKataTargeting.IsAutomaticEnemy(pawn, attacker)
                || (attacker is Pawn attackerPawn
                    && !RimKataTargeting.IsPawnTargetStateValid(attackerPawn)))
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            if (attacker is Pawn incomingPawn)
            {
                state.NotifyIncomingThreat(incomingPawn);
            }
            bool closeContext = pawn.CanReachImmediate(attacker, PathEndMode.Touch);
            if (closeContext)
            {
                state.EnterCloseCombat(attacker);
                state.RequestCloseAttack(attacker);
            }

            bool randomAttackEnabled = RimKataEligibility.RandomAttackEnabledForPawn(pawn);
            if (randomAttackEnabled)
            {
                RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                    pawn,
                    state,
                    attacker,
                    true);
            }
            RefreshDualEngagementState(pawn, state, randomAttackEnabled);

            TryCacheSharedCandidate(
                pawn,
                state,
                state.primaryWeaponCycle,
                attacker,
                randomAttackEnabled);
            TryCacheSharedCandidate(
                pawn,
                state,
                state.secondaryWeaponCycle,
                attacker,
                randomAttackEnabled);
            RefreshDualEngagementState(pawn, state, randomAttackEnabled);
            if (!state.dualEngagementActive)
            {
                return;
            }

            // Live shared work requests continuation regardless of entry mode.
            QueueDedicatedFollowupJob(pawn, attacker);
        }

        public static bool IsDedicatedFollowupActive(Pawn pawn)
        {
            return pawn?.InMentalState != true
                && HasCombatContinuity(pawn);
        }

        internal static bool IsCombatActiveForPortrait(Pawn pawn, JobDef jobDef)
        {
            Map map = pawn?.Map;
            if (map == null
                || pawn.InMentalState
                || (jobDef != RimKataDefOf.RimKata_Attack
                    && !RimKataDraftedFireController.IsAutomaticFireJob(jobDef)))
            {
                return false;
            }

            RimKataPawnCombatState state = RimKataCombatStatePresenceCache.Contains(pawn, map)
                ? StateFor(pawn, false)
                : null;
            return IsActualCombatActive(pawn, jobDef, state);
        }

        internal static bool IsActualCombatActive(
            Pawn pawn,
            JobDef jobDef,
            RimKataPawnCombatState state)
        {
            Map map = pawn?.Map;
            if (map == null
                || pawn.InMentalState
                || (jobDef != RimKataDefOf.RimKata_Attack
                    && !RimKataDraftedFireController.IsAutomaticFireJob(jobDef)))
            {
                return false;
            }

            if (IsWeaponCycleRunningForPortrait(state?.primaryWeaponCycle)
                || IsWeaponCycleRunningForPortrait(state?.secondaryWeaponCycle))
            {
                return true;
            }

            Thing target = pawn.CurJob?.targetA.Thing;
            if (target?.Spawned == true
                && target.Map == map
                && IsConvertedMeleeCounterattackRushJob(pawn, target)
                && (!(target is Pawn targetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(targetPawn)))
            {
                return true;
            }

            return false;
        }

        private static bool IsWeaponCycleRunningForPortrait(
            RimKataWeaponCycleState cycle)
        {
            // Search, stored candidates and visual retention do not mean that
            // a weapon has begun its aim/fire/cooldown sequence.
            return cycle?.weapon != null
                && (cycle.warmupTicksRemaining > 0
                    || cycle.NativeAttackPending
                    || cycle.cooldownTicksRemaining > 0
                    || ReadyToAct(cycle));
        }

        internal static bool CanContinueWeaponCycles(
            Pawn pawn,
            RimKataPawnCombatState state = null)
        {
            if (RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return true;
            }

            return CanContinueProjectileInterception(
                pawn, state ?? StateFor(pawn, false));
        }

        private static bool PrepareWeaponCycleTick(
            Pawn pawn,
            ref RimKataPawnCombatState state,
            bool allowMovementSearch)
        {
            if (!RimKataEligibility.CanOperateCombatWeapon(pawn))
            {
                return false;
            }

            if (state == null)
            {
                if (!RimKataEquipmentUtility.IsPrimaryWeaponEnabled(pawn))
                {
                    return false;
                }
                state = StateFor(pawn, true);
            }
            BindCurrentWeapons(pawn, state, true);
            bool ordinaryAttackAllowed = state.primaryWeaponCycle.ordinaryWeaponEnabled
                || state.secondaryWeaponCycle.ordinaryWeaponEnabled;
            if (!ordinaryAttackAllowed)
            {
                RimKataWeaponCycleState interceptionCycle = state.primaryWeaponCycle;
                if (RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled == false
                    || interceptionCycle.weapon == null
                    || !(BoundCombatVerb(pawn, interceptionCycle) is Verb_LaunchProjectile)
                    || !HasActiveInterceptionWork(pawn, interceptionCycle))
                {
                    return false;
                }
            }
            if (allowMovementSearch
                && state.primaryWeaponCycle.ordinaryWeaponEnabled)
            {
                // Movement feeds the shared search after common admission.
                PrepareMovementSearch(pawn, state);
            }

            return true;
        }

        internal static bool CanContinueProjectileInterception(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            RimKataWeaponCycleState cycle = state?.primaryWeaponCycle;
            // Unlisted guns may keep an actual interception, never ordinary combat.
            return cycle?.weapon != null
                && cycle.weapon == RimKataWeaponSlotUtility.PrimaryWeapon(pawn)
                && HasActiveInterceptionWork(pawn, cycle)
                && RimKataEligibility.CanUseProjectileInterception(pawn)
                && RimKataWeaponSlotUtility.CombatVerb(pawn, cycle.weapon)
                    is Verb_LaunchProjectile;
        }

        internal static bool ReconcileCloseCombatBeforeContinuityCheck(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            out Thing resolvedCloseTarget,
            bool assignedTargetValidated = false,
            bool assignedTargetInTouchRange = false)
        {
            resolvedCloseTarget = null;
            if (pawn?.Map == null
                || state?.dualCloseCombatActive != true)
            {
                return false;
            }

            resolvedCloseTarget = ResolveCloseTarget(
                pawn,
                state,
                assignedTarget,
                playerForced,
                killIncappedTarget,
                assignedTargetValidated,
                assignedTargetInTouchRange);
            HandleCloseCombatTransition(
                pawn,
                state,
                resolvedCloseTarget != null,
                resolvedCloseTarget,
                !playerForced);
            return true;
        }

        private static bool HasCycleTargetWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            bool? randomAttackEnabled,
            ref CycleVerbAvailability availability)
        {
            // DedicatedActive is the cheap structural prerequisite for every
            // target-bearing branch below.  Cooldown/visual-only retained
            // cycles must not resolve a Verb on every pawn tick.
            if (pawn?.Map == null || cycle?.DedicatedActive != true)
            {
                return false;
            }

            bool closeContext = cycle.plannedCloseContext
                || state?.dualCloseCombatActive == true;
            bool bindingCurrent = state != null && !state.weaponBindingsDirty
                && state.weaponConfigurationRevision
                    == RimKataEquipmentUtility.WeaponConfigurationRevision;
            Verb verb = bindingCurrent ? BoundCombatVerb(pawn, cycle)
                : RimKataWeaponSlotUtility.CombatVerb(pawn, cycle.weapon);
            bool ordinaryWeaponEnabled = bindingCurrent
                ? cycle.ordinaryWeaponEnabled
                : RimKataEquipmentUtility.IsWeaponEnabled(cycle.weapon.def);
            if ((!ordinaryWeaponEnabled
                    && (!(verb is Verb_LaunchProjectile)
                        || !HasActiveInterceptionWork(pawn, cycle)))
                || verb == null
                || !IsCycleVerbUsable(pawn, verb, closeContext, ref availability))
            {
                return false;
            }

            if (ordinaryWeaponEnabled && cycle.HasAutomaticCandidates
                && (randomAttackEnabled
                    ?? RimKataEligibility.RandomAttackEnabledForPawn(pawn)))
            {
                return true;
            }

            if (ordinaryWeaponEnabled && FocusedTargetUsableNow(
                pawn,
                cycle,
                verb,
                closeContext,
                ref availability))
            {
                return true;
            }

            if (cycle.cachedCandidateTarget != null
                && (ordinaryWeaponEnabled || cycle.cachedCandidateInterception))
            {
                return true;
            }

            Thing plannedTarget = cycle.plannedTarget;
            if (plannedTarget == null)
            {
                return false;
            }

            if (cycle.plannedInterception)
            {
                return RimKataSharedTargetSearch.IsValidForVerb(
                    pawn,
                    verb,
                    plannedTarget);
            }

            if (!ordinaryWeaponEnabled)
            {
                return false;
            }

            bool playerForced = cycle.focusedTarget == plannedTarget
                || (pawn.CurJob?.playerForced == true
                    && pawn.CurJob.targetA.Thing == plannedTarget);
            return ValidCurrentTargetForVerb(
                pawn,
                verb,
                plannedTarget,
                playerForced,
                pawn.CurJob?.killIncappedTarget == true,
                cycle.plannedCloseContext,
                ref availability);
        }

        private static bool NormalizeInvalidInterceptionState(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            return (state?.primaryWeaponCycle?.weapon != null
                && NormalizeInvalidInterceptionCycle(
                    pawn,
                    state.primaryWeaponCycle))
                | (state?.secondaryWeaponCycle?.weapon != null
                && NormalizeInvalidInterceptionCycle(
                    pawn,
                    state.secondaryWeaponCycle));
        }

        private static bool NormalizeInvalidInterceptionCycle(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            if (pawn?.Map == null || cycle == null)
            {
                return false;
            }

            bool changed = false;
            bool invalidPlannedInterception = cycle.plannedInterception
                && !IsActiveInterceptionTarget(
                    pawn,
                    cycle.plannedTarget as Projectile);
            if (invalidPlannedInterception)
            {
                Verb verb = RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    cycle.weapon);
                ApplyInterruptedBurstCooldown(pawn, cycle, verb);
                ClearTargetPreservingCycle(cycle);

                changed = true;
            }

            if (cycle.cachedCandidateInterception
                && !IsActiveInterceptionTarget(
                    pawn,
                    cycle.cachedCandidateTarget as Projectile))
            {
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
                changed = true;
            }

            if (cycle.visualTarget is Projectile visualProjectile
                && !IsActiveInterceptionTarget(pawn, visualProjectile))
            {
                cycle.visualTarget = null;
                cycle.visualAimTicksRemaining = 0;
                changed = true;
            }

            return changed;
        }

        private static bool IsActiveInterceptionTarget(
            Pawn pawn,
            Projectile projectile)
        {
            return projectile?.Map == pawn?.Map
                && RimKataTargeting.IsInterceptionTargetActive(projectile);
        }

        private static bool HasAnyCycleTargetWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool? randomAttackEnabled = null)
        {
            CycleVerbAvailability primaryAvailability = default;
            CycleVerbAvailability secondaryAvailability = default;
            return HasAnyCycleTargetWork(pawn, state, randomAttackEnabled,
                ref primaryAvailability, ref secondaryAvailability);
        }

        private static bool HasAnyCycleTargetWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool? randomAttackEnabled,
            ref CycleVerbAvailability primaryAvailability,
            ref CycleVerbAvailability secondaryAvailability)
        {
            RimKataWeaponCycleState primary = state?.primaryWeaponCycle;
            RimKataWeaponCycleState secondary = state?.secondaryWeaponCycle;
            if (!randomAttackEnabled.HasValue
                && (primary?.HasAutomaticCandidates == true
                    || secondary?.HasAutomaticCandidates == true))
            {
                randomAttackEnabled =
                    RimKataEligibility.RandomAttackEnabledForPawn(pawn);
            }

            return HasCycleTargetWork(
                    pawn,
                    state,
                    primary,
                    randomAttackEnabled,
                    ref primaryAvailability)
                || HasCycleTargetWork(
                    pawn,
                    state,
                    secondary,
                    randomAttackEnabled,
                    ref secondaryAvailability);
        }

        private static void RefreshDualEngagementState(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool? randomAttackEnabled = null)
        {
            CycleVerbAvailability primaryAvailability = default;
            CycleVerbAvailability secondaryAvailability = default;
            RefreshDualEngagementState(pawn, state, randomAttackEnabled,
                ref primaryAvailability, ref secondaryAvailability);
        }

        private static void RefreshDualEngagementState(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool? randomAttackEnabled,
            ref CycleVerbAvailability primaryAvailability,
            ref CycleVerbAvailability secondaryAvailability)
        {
            if (state == null)
            {
                return;
            }

            bool wasActive = state.dualEngagementActive;
            state.dualEngagementActive = EvaluateCombatContinuity(
                pawn,
                state,
                randomAttackEnabled,
                ref primaryAvailability,
                ref secondaryAvailability);
            if (wasActive && !state.dualEngagementActive)
            {
                state.ResetCandidateSaturationExpansion(true);
            }
        }

        public static bool HasCombatContinuity(Pawn pawn)
        {
            return HasCombatContinuity(pawn, StateFor(pawn, false));
        }

        internal static bool HasCombatContinuity(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool? randomAttackEnabled = null)
        {
            if (pawn?.InMentalState == true || state == null)
            {
                return false;
            }

            RefreshDualEngagementState(pawn, state, randomAttackEnabled);
            return state.dualEngagementActive;
        }

        private static bool EvaluateCombatContinuity(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool? randomAttackEnabled,
            ref CycleVerbAvailability primaryAvailability,
            ref CycleVerbAvailability secondaryAvailability)
        {
            if (pawn?.Map == null || pawn.InMentalState || state == null)
            {
                return false;
            }

            // Existing work can establish continuity without rechecking a
            // close target's hostility, live state and Touch reachability.
            return state.sharedTargetSearch?.KeepsCombatAlive == true
                || (state.dualEngagementActive && state.MovementFireContinuityActive)
                || state.DodgeMovementActive
                || state.DraftedMovementSearchTriggerPending
                || state.idleProjectileSearchTriggerPending
                || state.dedicatedFollowupJobPending
                || (state.dualCloseCombatActive
                    && IsImmediateCloseTarget(
                        pawn,
                        state.dualCloseTarget,
                        pawn.CurJob?.playerForced == true,
                        pawn.CurJob?.killIncappedTarget == true))
                || HasDedicatedTargetContinuity(pawn, state)
                || HasAnyCycleTargetWork(pawn, state, randomAttackEnabled,
                    ref primaryAvailability, ref secondaryAvailability)
                || state.CloseAttackRequestActive;
        }

        private static bool HasDedicatedTargetContinuity(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            int currentTick = Find.TickManager?.TicksGame ?? -1;
            Job currentJob = pawn?.CurJob;
            Thing target = state?.dedicatedContinuityTarget;
            return state != null
                && currentTick >= 0
                && currentTick <= state.dedicatedContinuityUntilTick
                && currentJob?.def == RimKataDefOf.RimKata_Attack
                && currentJob.targetA.Thing == target
                && !PermanentlyInvalidCycleTarget(
                    pawn,
                    target,
                    target,
                    currentJob.playerForced,
                    currentJob.killIncappedTarget,
                    false);
        }

        public static void RefreshDedicatedTargetContinuity(
            Pawn pawn,
            Thing target)
        {
            RefreshDedicatedTargetContinuity(pawn, StateFor(pawn, false), target);
        }

        internal static void RefreshDedicatedTargetContinuity(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing target)
        {
            if (state == null
                || pawn?.CurJobDef != RimKataDefOf.RimKata_Attack
                || target == null)
            {
                return;
            }

            if (!HasAnyCycleTargetWork(pawn, state)
                && !RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                    pawn,
                    target)
                && !CanMaintainRushContinuity(pawn, target))
            {
                return;
            }

            state.dedicatedContinuityTarget = target;
            state.dedicatedContinuityUntilTick =
                (Find.TickManager?.TicksGame ?? 0) + 3;
            RefreshDualEngagementState(pawn, state);
        }

        private static bool CanMaintainRushContinuity(Pawn pawn, Thing target)
        {
            if (!CanRushTarget(pawn, target))
            {
                return false;
            }

            if (pawn.CanReachImmediate(target, PathEndMode.Touch)
                || (pawn.pather?.Moving == true
                    && pawn.pather.Destination.Thing == target))
            {
                return true;
            }

            return pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly);
        }

        public static void NotifyDraftStatusChanged(Pawn pawn)
        {
            RimKataDormantHostileMovementRegistry.NotifyDraftStatusChanged(
                pawn);
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state == null)
            {
                return;
            }

            state.CancelDraftedFire(false);
            state.ClearDraftedMovementSearchTracking();
            CancelUnfiredWarmupForDraftChange(state.primaryWeaponCycle);
            CancelUnfiredWarmupForDraftChange(state.secondaryWeaponCycle);
            state.ClearDedicatedFollowupJobRequest();
            RearmOpeningOwnerIfBothWaiting(state);
            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;
        }

        private static void CancelUnfiredWarmupForDraftChange(
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null || cycle.NativeAttackPending)
            {
                return;
            }

            cycle.plannedTarget = null;
            cycle.plannedInterception = false;
            cycle.plannedCloseAttack = false;
            cycle.plannedCloseContext = false;
            cycle.plannedActionVerb = null;
            cycle.warmupTicksRemaining = -1;
            cycle.warmupTotalTicks = 0;
            cycle.openingWarmupBonusTicks = 0;
            cycle.openingWarmupPending = false;
            cycle.focusedTarget = null;
            cycle.focusedTargetFromAttackGizmo = false;
        }

        public static void QueueDedicatedFollowupJob(Pawn pawn, Thing target)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (pawn?.Map == null
                || pawn.InMentalState
                || state == null
                || state.dedicatedFollowupJobStartInProgress
                || IsProtectedPlayerForcedJob(pawn.CurJob)
                || !IsDedicatedFollowupActive(pawn))
            {
                return;
            }

            state.QueueDedicatedFollowupJob(target, pawn.CurJob);
        }

        public static void NotifyDedicatedCombatJobFinished(Pawn pawn)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state != null)
            {
                state.projectileWakeResumeJob = null;
            }
        }

        public static void TryConsumePendingDedicatedFollowupJob(Pawn pawn)
        {
            TryConsumePendingDedicatedFollowupJob(
                pawn,
                StateFor(pawn, false));
        }

        internal static bool PendingFollowupCanReplaceCurrentNonForcedGoto(
            Pawn pawn)
        {
            Job currentJob = pawn?.CurJob;
            if (currentJob?.def != JobDefOf.Goto
                || currentJob.playerForced
                || pawn.Map == null
                || pawn.InMentalState)
            {
                return false;
            }

            int currentTick = Find.TickManager?.TicksGame ?? -1;
            RimKataPawnCombatState state = StateFor(pawn, false);
            return state?.dedicatedFollowupJobStartInProgress != true
                && state?.dedicatedFollowupJobLastStartTick != currentTick
                && CanConsumePendingDedicatedFollowupRequest(
                    pawn,
                    state,
                    currentTick);
        }

        internal static void TryConsumePendingDedicatedFollowupJob(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (pawn?.InMentalState == true)
            {
                return;
            }

            if (state?.dedicatedFollowupJobPending != true)
            {
                return;
            }

            int currentTick = Find.TickManager?.TicksGame ?? -1;
            int requestedTick = state.dedicatedFollowupJobRequestedTick;
            if (currentTick < 0 || requestedTick > currentTick)
            {
                return;
            }

            bool requestReady = CanConsumePendingDedicatedFollowupRequest(
                pawn,
                state,
                currentTick);
            Thing target = state.dedicatedFollowupJobTarget;
            Job sourceJob = state.dedicatedFollowupJobSourceJob;
            ThinkNode sourceJobGiver = sourceJob?.jobGiver;
            ThinkTreeDef sourceJobGiverThinkTree = sourceJob?.jobGiverThinkTree;
            Thing sourceTarget = sourceJob?.targetA.Thing;
            if (!(sourceJobGiver is JobGiver_ConfigurableHostilityResponse)
                && !(sourceJobGiver is JobGiver_ReactToCloseMeleeThreat))
            {
                sourceJobGiver = null;
            }
            bool playerForced = state.dedicatedFollowupJobPlayerForced;
            bool killIncappedTarget = state.dedicatedFollowupJobKillIncappedTarget;
            state.ClearDedicatedFollowupJobRequest();
            if (!requestReady)
            {
                if (state.projectileWakeResumeJob != null
                    && state.projectileWakeResumeJob == sourceJob)
                {
                    state.ConsumeIdleProjectileSearchTrigger();
                    ClearQueuedInterceptionCandidate(state.primaryWeaponCycle);
                    ClearQueuedInterceptionCandidate(state.secondaryWeaponCycle);
                    state.projectileWakeResumeJob = null;
                    RefreshDualEngagementState(pawn, state);
                }
                return;
            }

            TryEnterDedicatedFollowupJob(
                pawn,
                target,
                playerForced,
                killIncappedTarget,
                sourceJobGiver,
                sourceJobGiverThinkTree,
                sourceTarget);
        }

        private static bool CanStartQueuedProjectileWake(Pawn pawn)
        {
            if (!CanReceiveProjectileWake(pawn) || HasBusyAttackStance(pawn))
            {
                return false;
            }

            Verb primary = RimKataWeaponSlotUtility.CombatVerb(
                pawn, RimKataWeaponSlotUtility.PrimaryWeapon(pawn));
            Verb secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn)
                ? RimKataWeaponSlotUtility.CombatVerb(
                    pawn, RimKataWeaponSlotUtility.SecondaryWeapon(pawn))
                : null;
            return primary?.Bursting != true && secondary?.Bursting != true;
        }

        private static void ClearQueuedInterceptionCandidate(
            RimKataWeaponCycleState cycle)
        {
            if (cycle?.cachedCandidateInterception == true)
            {
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
            }
        }

        private static bool CanConsumePendingDedicatedFollowupRequest(
            Pawn pawn,
            RimKataPawnCombatState state,
            int currentTick)
        {
            if (pawn == null
                || state?.dedicatedFollowupJobPending != true
                || currentTick < 0
                || state.dedicatedFollowupJobRequestedTick < 0
                || state.dedicatedFollowupJobRequestedTick > currentTick
                || currentTick > state.dedicatedFollowupJobRequestedTick + 1
                || (state.projectileWakeResumeJob != null
                    && state.projectileWakeResumeJob == state.dedicatedFollowupJobSourceJob
                    && !CanStartQueuedProjectileWake(pawn))
                || !CanConsumeDedicatedFollowupRequest(
                    pawn,
                    state.dedicatedFollowupJobSourceJob,
                    state.dedicatedFollowupJobTarget))
            {
                return false;
            }

            return !pawn.Drafted
                || state.dedicatedFollowupJobPlayerForced
                || pawn.drafter?.FireAtWill == true;
        }

        private static bool CanConsumeDedicatedFollowupRequest(
            Pawn pawn,
            Job sourceJob,
            Thing target)
        {
            Job currentJob = pawn?.CurJob;
            if (IsProtectedPlayerForcedJob(currentJob))
            {
                return false;
            }

            if (currentJob == sourceJob || currentJob == null)
            {
                return true;
            }

            JobDef currentDef = currentJob.def;
            if (currentDef == RimKataDefOf.RimKata_Attack)
            {
                return true;
            }

            if (currentDef == JobDefOf.AttackStatic
                || currentDef == JobDefOf.AttackMelee)
            {
                return target != null
                    && currentJob.targetA.Thing == target;
            }

            return currentDef == JobDefOf.Goto
                || currentDef == JobDefOf.Wait
                || currentDef == JobDefOf.Wait_Combat
                || currentDef == JobDefOf.Wait_MaintainPosture;
        }

        public static void RefreshPendingDedicatedFollowupAim(
            Pawn pawn,
            Verb sourceVerb,
            LocalTargetInfo sourceTarget)
        {
            if (pawn?.InMentalState == true)
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state?.dedicatedFollowupJobPending != true
                || state.dedicatedFollowupJobStartInProgress
                || sourceVerb == null
                || !sourceTarget.HasThing
                || state.dedicatedFollowupJobTarget != sourceTarget.Thing
                || !IsDedicatedFollowupActive(pawn)
                || pawn?.stances?.FullBodyBusy == true)
            {
                return;
            }

            RimKataWeaponCycleState sourceCycle = CycleForWeapon(
                state,
                sourceVerb.EquipmentSource as ThingWithComps);
            if (sourceCycle?.lastFiredTarget != sourceTarget.Thing
                || RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    sourceCycle.weapon) != sourceVerb)
            {
                return;
            }

            UpdateBodyAimStance(pawn, state);
        }

        private static void TryEnterDedicatedFollowupJob(
            Pawn pawn,
            Thing target,
            bool? playerForcedOverride,
            bool? killIncappedTargetOverride,
            ThinkNode counterattackJobGiver,
            ThinkTreeDef counterattackJobGiverThinkTree,
            Thing counterattackSourceTarget)
        {
            if (pawn?.InMentalState == true)
            {
                return;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (state?.dedicatedFollowupJobStartInProgress == true
                || (currentTick >= 0
                    && state?.dedicatedFollowupJobLastStartTick == currentTick))
            {
                return;
            }

            Job currentJob = pawn?.CurJob;
            if (IsProtectedPlayerForcedJob(currentJob))
            {
                state?.ClearDedicatedFollowupJobRequest();
                if (state?.projectileWakeResumeJob == currentJob)
                {
                    state.projectileWakeResumeJob = null;
                }
                return;
            }

            bool resumeCurrentJobAfterProjectile = pawn?.Drafted != true
                && currentJob != null
                && state?.projectileWakeResumeJob == currentJob;

            bool playerForced = playerForcedOverride
                ?? currentJob?.playerForced == true;
            bool killIncappedTarget = killIncappedTargetOverride
                ?? currentJob?.killIncappedTarget == true;
            bool validTarget = !PermanentlyInvalidCycleTarget(
                pawn,
                target,
                target,
                playerForced,
                killIncappedTarget,
                false);
            if (!validTarget)
            {
                TryGetContinuationTarget(
                    pawn,
                    playerForced,
                    killIncappedTarget,
                    out target);
            }

            validTarget = !PermanentlyInvalidCycleTarget(
                pawn,
                target,
                target,
                playerForced,
                killIncappedTarget,
                false);
            bool searchOnly = pawn?.Map != null
                && HasContinuationSearchWork(pawn);
            if (pawn?.Map == null
                || (!validTarget && !searchOnly)
                || pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                || !IsDedicatedFollowupActive(pawn))
            {
                if (pawn?.CurJobDef == RimKataDefOf.RimKata_Attack)
                {
                    state?.ClearDedicatedFollowupJobRequest();
                }

                return;
            }

            Job job = validTarget
                ? JobMaker.MakeJob(RimKataDefOf.RimKata_Attack, target)
                : JobMaker.MakeJob(RimKataDefOf.RimKata_Attack);
            job.playerForced = playerForced;
            job.killIncappedTarget = validTarget
                ? playerForced
                    && killIncappedTarget
                    && RimKataTargeting.IsIncapacitatedTarget(
                        target as Pawn)
                : killIncappedTarget;
            job.verbToUse = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                state?.engagementOwnerWeapon)
                ?? RimKataWeaponSlotUtility.BestRangedCombatVerb(
                    pawn,
                    validTarget ? target : null)
                ?? RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    RimKataWeaponSlotUtility.PrimaryWeapon(pawn));
            bool counterattackTargetChanged = counterattackJobGiver != null
                && validTarget
                && counterattackSourceTarget != target;
            if (counterattackTargetChanged)
            {
                job.jobGiver = counterattackJobGiver;
            }
            state.ClearDedicatedFollowupJobRequest();
            state.dedicatedFollowupJobStartInProgress = true;
            state.dedicatedFollowupJobLastStartTick = currentTick;
            try
            {
                if (job.playerForced)
                {
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
                else
                {
                    pawn.jobs.StartJob(
                        job,
                        JobCondition.InterruptForced,
                        null,
                        resumeCurrentJobAfterProjectile,
                        true,
                        null,
                        JobTag.Misc,
                        false,
                        false,
                        null,
                        false,
                        true,
                        false);

                    if (pawn.CurJob == job
                        && counterattackJobGiver != null)
                    {
                        // StartJob overwrites provenance. Publish it after setup,
                        // retaining both fields so automatic rush survives loading.
                        job.jobGiver = counterattackJobGiver;
                        job.jobGiverThinkTree = counterattackJobGiverThinkTree;
                    }

                    if (resumeCurrentJobAfterProjectile
                        && pawn.CurJob != job)
                    {
                        state.projectileWakeResumeJob = null;
                    }
                }
            }
            finally
            {
                state.dedicatedFollowupJobStartInProgress = false;
            }
        }

        private static bool IsProtectedPlayerForcedJob(Job job)
        {
            return job?.playerForced == true;
        }

        public static RimKataCounterattackOpeningResult
        HandleCounterattackOpening(
            Pawn pawn,
            Job sourceJob,
            ThinkNode jobGiver,
            out Job convertedJob)
        {
            convertedJob = sourceJob;
            if ((!(jobGiver is JobGiver_ConfigurableHostilityResponse)
                    && !(jobGiver is JobGiver_ReactToCloseMeleeThreat))
                || !CounterattackControlEnabled(pawn))
            {
                return RimKataCounterattackOpeningResult.NotHandled;
            }

            if (!IsConfigurableCounterattackOpening(pawn, sourceJob))
            {
                return RimKataCounterattackOpeningResult.NotHandled;
            }

            Thing target = sourceJob.targetA.Thing;
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (UsesVanillaAutomaticTarget(pawn, target, state))
            {
                return RimKataCounterattackOpeningResult.NotHandled;
            }

            Job currentJob = pawn?.CurJob;
            if (currentJob?.def == RimKataDefOf.RimKata_Attack
                && !currentJob.playerForced
                && pawn.jobs?.curDriver is JobDriver_RimKataAttack driver
                && driver.CanAbsorbAutomaticAttackJob)
            {
                bool randomAttackEnabled =
                    RimKataEligibility.RandomAttackEnabledForPawn(pawn);
                bool immediateMeleeThreat = sourceJob.def == JobDefOf.AttackMelee
                    && pawn.CanReachImmediate(target, PathEndMode.Touch);
                if (randomAttackEnabled || immediateMeleeThreat)
                {
                    state ??= StateFor(pawn, true);
                    if (randomAttackEnabled)
                    {
                        BindCurrentWeapons(pawn, state);
                        RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                            pawn,
                            state,
                            target);
                    }
                    // A melee reaction is not random candidate collection.
                    // Publish it without replacing the existing Job target.
                    if (immediateMeleeThreat)
                    {
                        state.RequestCloseAttack(target);
                    }

                    RefreshDualEngagementState(pawn, state);
                }

                return RimKataCounterattackOpeningResult.Absorbed;
            }

            Thing openingJobTarget = ResolveCloseTarget(
                    pawn,
                    state,
                    target,
                    false,
                    false)
                ?? target;
            bool meleeOnlyCounterattackRush =
                openingJobTarget == target
                && IsMeleeOnlyCounterattackRushOpening(
                    pawn,
                    sourceJob,
                    jobGiver);
            if (!meleeOnlyCounterattackRush
                && !TargetWithinAutomaticCandidateCellRadius(
                    pawn,
                    openingJobTarget)
                && !RimKataWeaponSlotUtility.CanAttackTargetWithoutRushing(
                    pawn,
                    openingJobTarget))
            {
                return RimKataCounterattackOpeningResult.NotHandled;
            }

            state ??= StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            Verb ownerVerb =
                RimKataWeaponSlotUtility.BestRangedCombatVerb(
                    pawn,
                    openingJobTarget)
                ?? sourceJob.verbToUse;
            ThingWithComps ownerWeapon =
                ownerVerb?.EquipmentSource as ThingWithComps;
            if (CycleForWeapon(state, ownerWeapon) == null)
            {
                ownerWeapon = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
                ownerVerb = RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    ownerWeapon);
            }

            state.engagementOwnerWeapon = ownerWeapon;
            RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                pawn,
                state,
                target);
            if (openingJobTarget != target)
            {
                RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                    pawn,
                    state,
                    openingJobTarget);
            }
            RimKataSharedTargetSearch.Begin(
                pawn,
                state,
                pawn.Position);
            if (sourceJob.def == JobDefOf.AttackMelee
                && pawn.CanReachImmediate(
                    openingJobTarget,
                    PathEndMode.Touch))
            {
                state.RequestCloseAttack(openingJobTarget);
            }
            RefreshDualEngagementState(pawn, state);
            state.dualLastDrivenTick = -1;

            Job rimKataJob = JobMaker.MakeJob(
                RimKataDefOf.RimKata_Attack,
                openingJobTarget);
            rimKataJob.playerForced = false;
            rimKataJob.killIncappedTarget = false;
            rimKataJob.jobGiver = jobGiver;
            rimKataJob.verbToUse = ownerVerb
                ?? RimKataWeaponSlotUtility.CombatVerb(
                    pawn,
                    RimKataWeaponSlotUtility.PrimaryWeapon(pawn));
            convertedJob = rimKataJob;
            return RimKataCounterattackOpeningResult.Converted;
        }

        private static bool IsConfigurableCounterattackOpening(
            Pawn pawn,
            Job job)
        {
            Thing target = job?.targetA.Thing;
            return pawn?.Map != null
                && !pawn.InMentalState
                && pawn.Drafted != true
                && pawn.playerSettings?.UsesConfigurableHostilityResponse == true
                && pawn.playerSettings.hostilityResponse
                    == HostilityResponseMode.Attack
                && job.playerForced != true
                && (job.def == JobDefOf.AttackStatic
                    || job.def == JobDefOf.AttackMelee)
                && target != null
                && target.Spawned
                && !target.Destroyed
                && target.Map == pawn.Map
                && RimKataEligibility.CanBeginGunKataAttack(pawn)
                && RimKataTargeting.IsAutomaticEnemy(pawn, target)
                && (!(target is Pawn targetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(targetPawn));
        }

        private static bool IsMeleeOnlyCounterattackRushOpening(
            Pawn pawn,
            Job sourceJob,
            ThinkNode jobGiver)
        {
            return RimKataTargetAccess.SettingsFor(pawn)?.targetRushEnabled != false
                && sourceJob?.def == JobDefOf.AttackMelee
                && sourceJob.playerForced != true
                && IsCounterattackJobGiver(jobGiver)
                && HasOnlyMeleeCombatWeapons(pawn);
        }

        private static bool IsConvertedMeleeCounterattackRushJob(
            Pawn pawn,
            Thing target)
        {
            Job job = pawn?.CurJob;
            return RimKataTargetAccess.SettingsFor(pawn)?.targetRushEnabled != false
                && job?.def == RimKataDefOf.RimKata_Attack
                && job.playerForced != true
                && job.targetA.Thing == target
                && job.verbToUse?.IsMeleeAttack == true
                && IsCounterattackJobGiver(job.jobGiver)
                && pawn.Drafted != true
                && pawn.playerSettings?.UsesConfigurableHostilityResponse == true
                && pawn.playerSettings.hostilityResponse
                    == HostilityResponseMode.Attack;
        }

        private static bool IsCounterattackJobGiver(ThinkNode jobGiver)
        {
            return jobGiver is JobGiver_ConfigurableHostilityResponse
                || jobGiver is JobGiver_ReactToCloseMeleeThreat;
        }

        private static bool HasOnlyMeleeCombatWeapons(Pawn pawn)
        {
            ThingWithComps primary =
                RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            Verb primaryVerb =
                RimKataWeaponSlotUtility.CombatVerb(pawn, primary);
            if (primary == null || primaryVerb?.IsMeleeAttack != true)
            {
                return false;
            }

            if (!RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn))
            {
                return true;
            }

            ThingWithComps secondary =
                RimKataWeaponSlotUtility.SecondaryWeapon(pawn);
            Verb secondaryVerb =
                RimKataWeaponSlotUtility.CombatVerb(pawn, secondary);
            return secondaryVerb == null || secondaryVerb.IsMeleeAttack;
        }

        public static bool CanRushTarget(Pawn pawn, Thing target)
        {
            if (pawn?.Map == null
                || pawn.Drafted
                || pawn.InMentalState
                || pawn.CurJobDef != RimKataDefOf.RimKata_Attack
                || pawn.CurJob.targetA.Thing != target
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map)
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, false);
            bool playerRushRequest =
                state?.IsPlayerRushRequestFor(target) == true;
            bool allowIncapacitated = playerRushRequest
                && pawn.CurJob?.killIncappedTarget == true;
            if (target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    allowIncapacitated))
            {
                return false;
            }

            if (playerRushRequest)
            {
                return true;
            }

            return RimKataTargetAccess.SettingsFor(pawn)?.targetRushEnabled != false
                && pawn?.CurJob?.playerForced != true
                && IsCounterattackJobGiver(pawn.CurJob.jobGiver)
                && (TargetWithinAutomaticCandidateCellRadius(pawn, target)
                    || IsConvertedMeleeCounterattackRushJob(pawn, target))
                && RimKataTargeting.IsAutomaticEnemy(pawn, target)
                && (!(target is Pawn automaticTargetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(
                        automaticTargetPawn));
        }

        public static bool TryConvertSecondaryMeleeAttackOrder(
            Pawn pawn,
            Job job,
            Verb meleeVerb)
        {
            Thing target = job?.targetA.Thing;
            ThingWithComps weapon =
                meleeVerb?.EquipmentSource as ThingWithComps;
            if (pawn?.Map == null
                || pawn.Drafted
                || pawn.InMentalState
                || pawn.IsBurning()
                || !pawn.IsPlayerControlled
                || job?.def != JobDefOf.AttackMelee
                || !job.playerForced
                || target == null
                || target == pawn
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || meleeVerb?.IsMeleeAttack != true
                || meleeVerb.CasterPawn != pawn
                || weapon == null
                || !meleeVerb.Available()
                || !RimKataEligibility.CanBeginGunKataAttack(pawn))
            {
                return false;
            }

            ThingWithComps primary =
                RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            ThingWithComps secondary =
                RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    pawn,
                    primary,
                    true)
                        ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                        : null;
            if (weapon != secondary)
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            state.RequestPlayerRush(target);
            job.def = RimKataDefOf.RimKata_Attack;
            job.playerForced = true;
            job.verbToUse = meleeVerb;
            job.killIncappedTarget =
                RimKataTargeting.IsIncapacitatedTarget(job.targetA.Pawn);
            return true;
        }

        public static bool TryConvertPlayerRushOrder(Pawn pawn, Job job)
        {
            Thing target = job?.targetA.Thing;
            if (pawn?.Map == null
                || pawn.Drafted
                || pawn.InMentalState
                || pawn.IsBurning()
                || !pawn.IsPlayerControlled
                || job?.def != JobDefOf.AttackMelee
                || target == null
                || target == pawn
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || !RimKataEligibility.CanBeginGunKataAttack(pawn)
                || !HasUsableWeapon(pawn, true, true))
            {
                return false;
            }

            Verb meleeVerb = pawn.meleeVerbs?.TryGetMeleeVerb(target);
            if (meleeVerb == null)
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            state.RequestPlayerRush(target);
            job.def = RimKataDefOf.RimKata_Attack;
            job.playerForced = true;
            job.verbToUse = meleeVerb;
            job.killIncappedTarget =
                RimKataTargeting.IsIncapacitatedTarget(job.targetA.Pawn);
            return true;
        }

        private static void ResetUnfiredOpeningTimer(
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null)
            {
                return;
            }

            cycle.ClearPlan(false);
            cycle.warmupTicksRemaining = -1;
            cycle.warmupTotalTicks = 0;
            cycle.cachedCandidateTarget = null;
            cycle.cachedCandidateInterception = false;
            cycle.visualTarget = null;
            cycle.visualAimTicksRemaining = 0;
        }

        private static void ClearTargetPreservingCycle(
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null)
            {
                return;
            }

            Thing previousTarget = cycle.plannedTarget;
            cycle.ClearPlan(false);
            if (cycle.openingWarmupPending
                && !cycle.firedInCurrentOpening)
            {
                cycle.openingWarmupBonusTicks = 0;
                cycle.openingWarmupPending = false;
            }

            if (cycle.visualTarget == previousTarget)
            {
                cycle.visualTarget = null;
                cycle.visualAimTicksRemaining = 0;
            }
        }

        private static void RecordFirstFiredWeapon(
            RimKataPawnCombatState state,
            ThingWithComps weapon)
        {
            if (state != null
                && state.engagementOwnerWeapon == null
                && weapon != null)
            {
                state.engagementOwnerWeapon = weapon;
            }
        }

        private static bool ValidCurrentTargetForVerb(
            Pawn pawn,
            Verb verb,
            Thing target,
            bool playerForced,
            bool killIncappedTarget,
            bool closeContext)
        {
            CycleVerbAvailability availability = default;
            return ValidCurrentTargetForVerb(
                pawn, verb, target, playerForced, killIncappedTarget,
                closeContext, ref availability);
        }

        private static bool ValidCurrentTargetForVerb(
            Pawn pawn,
            Verb verb,
            Thing target,
            bool playerForced,
            bool killIncappedTarget,
            bool closeContext,
            ref CycleVerbAvailability availability)
        {
            if (pawn?.Map == null
                || verb == null
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || (!playerForced
                    && !RimKataTargeting.IsAutomaticEnemy(pawn, target))
                || !IsCycleVerbUsable(pawn, verb, closeContext, ref availability))
            {
                return false;
            }

            if (target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    playerForced && killIncappedTarget))
            {
                return false;
            }

            return CanHitTargetForCombatContext(
                pawn,
                verb,
                target,
                closeContext,
                ref availability);
        }

        private static bool CanHitTargetForCombatContext(
            Pawn pawn, Verb verb, Thing target, bool closeCombatContext,
            ref CycleVerbAvailability availability)
        {
            if (target != null && availability.checkedTarget == target
                && availability.checkedVerb == verb
                && availability.checkedCloseContext == closeCombatContext)
            {
                return true;
            }
            if (!CanHitTargetForCombatContext(pawn, verb, target, closeCombatContext))
            {
                availability.checkedTarget = null;
                return false;
            }
            availability.checkedTarget = target;
            availability.checkedVerb = verb;
            availability.checkedCloseContext = closeCombatContext;
            return true;
        }

        private static bool CanHitTargetForCombatContext(
            Pawn pawn,
            Verb verb,
            Thing target,
            bool closeCombatContext)
        {
            if (verb == null || target == null)
            {
                return false;
            }

            if (verb.IsMeleeAttack)
            {
                return verb.CanHitTarget(target);
            }

            return closeCombatContext
                ? pawn?.CanReachImmediate(target, PathEndMode.Touch) == true
                : verb.CanHitTarget(target);
        }

        public static bool TryTakeVanillaMeleeCooldown(
            Pawn pawn,
            Verb verb,
            LocalTargetInfo focus)
        {
            if (pawn?.Map == null
                || pawn.InMentalState
                || verb == null
                || !verb.IsMeleeAttack
                || !RimKataEligibility.HasActiveRimKataAccess(pawn)
                || !RimKataEquipmentUtility.HasEnabledArmor(pawn))
            {
                return false;
            }

            if (RimKataFireContext.ActiveVerb == verb
                && RimKataNativeAttack.OwnsActiveMelee(verb))
            {
                return true;
            }

            if (UsesVanillaAutomaticTarget(pawn, focus.Thing))
            {
                return false;
            }

            ThingWithComps weapon = verb.EquipmentSource as ThingWithComps;
            if (weapon == null
                || weapon.Destroyed
                || pawn.equipment?.AllEquipmentListForReading?.Contains(weapon) != true
                || !RimKataEquipmentUtility.IsWeaponEnabled(weapon.def))
            {
                return false;
            }

            RimKataPawnCombatState state = StateFor(pawn, true);
            BindCurrentWeapons(pawn, state);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            if (cycle == null)
            {
                return false;
            }

            int cooldown = RimKataCombatMath.CooldownTicksForSingleShot(verb, pawn, false);

            cycle.cooldownTicksRemaining = Mathf.Max(cycle.cooldownTicksRemaining, cooldown);
            cycle.cooldownFromVanillaOpening = false;
            cycle.lastFiredTarget = focus.HasThing ? focus.Thing : null;
            cycle.visualTarget = cycle.lastFiredTarget;
            cycle.visualAimTicksRemaining = cooldown;
            RimKataSharedTargetSearch.Begin(
                pawn,
                state,
                pawn.Position);

            return true;
        }

        public static bool HasUsableWeapon(Pawn pawn, bool closeCombatContext)
        {
            return HasUsableWeapon(pawn, closeCombatContext, false);
        }

        internal static bool HasUsableWeapon(
            Pawn pawn,
            bool closeCombatContext,
            bool attackEligibilityVerified)
        {
            if (pawn == null
                || (!attackEligibilityVerified
                    && !RimKataEligibility.CanBeginGunKataAttack(pawn)))
            {
                return false;
            }

            ThingWithComps primary = RimKataWeaponSlotUtility.PrimaryWeapon(pawn);
            Verb primaryVerb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                primary);
            if (primaryVerb != null && VerbUsable(pawn, primaryVerb, closeCombatContext))
            {
                return true;
            }

            ThingWithComps secondary = RimKataWeaponSlotUtility
                .CanUseSecondarySlot(
                    pawn,
                    primary,
                    attackEligibilityVerified)
                ? RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                : null;
            Verb secondaryVerb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                secondary);
            return secondaryVerb != null && VerbUsable(pawn, secondaryVerb, closeCombatContext);
        }

        public static Thing ResolveImmediateCloseTarget(
            Pawn pawn,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget)
        {
            return ResolveImmediateCloseTarget(
                pawn,
                StateFor(pawn, false),
                assignedTarget,
                playerForced,
                killIncappedTarget);
        }

        internal static Thing ResolveImmediateCloseTarget(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool assignedTargetValidated = false,
            bool assignedTargetInTouchRange = false)
        {
            return ResolveCloseTarget(
                pawn,
                state,
                assignedTarget,
                playerForced,
                killIncappedTarget,
                assignedTargetValidated,
                assignedTargetInTouchRange);
        }

        public static bool TryGetContinuationTarget(
            Pawn pawn,
            bool playerForced,
            bool killIncappedTarget,
            out Thing target)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (TryGetContinuationTarget(pawn, state?.primaryWeaponCycle, playerForced, killIncappedTarget, out target)
                || TryGetContinuationTarget(pawn, state?.secondaryWeaponCycle, playerForced, killIncappedTarget, out target))
            {
                return true;
            }

            target = null;
            return false;
        }

        public static bool HasContinuationSearchWork(Pawn pawn)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            return ResolveCloseTarget(
                    pawn,
                    state,
                    null,
                    false,
                    false) != null
                || state?.DraftedMovementSearchTriggerPending == true
                || state?.sharedTargetSearch?.KeepsCombatAlive == true
                || CycleHasContinuationSearchWork(
                    pawn,
                    state?.primaryWeaponCycle)
                || CycleHasContinuationSearchWork(
                    pawn,
                    state?.secondaryWeaponCycle);
        }

        public static bool EnsureContinuationSearchBeforeExit(Pawn pawn)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (pawn?.Map == null || state == null)
            {
                return false;
            }

            if (state.dualCloseCombatActive)
            {
                Thing liveCloseTarget = ResolveCloseTarget(
                    pawn,
                    state,
                    null,
                    false,
                    false);
                if (liveCloseTarget != null)
                {
                    return true;
                }

                HandleCloseCombatTransition(
                    pawn,
                    state,
                    false,
                    null);
            }

            if (MovementSearchInProgress(state))
            {
                return true;
            }

            if (state.DraftedMovementSearchTriggerPending)
            {
                if (TryBeginMovementSearch(pawn, state, pawn.Position))
                {
                    state.ConsumeDraftedMovementSearchTrigger();
                    return true;
                }
            }

            BindCurrentWeapons(pawn, state);
            if (RimKataEligibility.RandomAttackEnabledForPawn(pawn)
                && (state.primaryWeaponCycle?.HasAutomaticCandidates == true
                    || state.secondaryWeaponCycle?.HasAutomaticCandidates == true))
            {
                return true;
            }

            if (HasProjectileOnlySearchWork(pawn, state))
            {
                return true;
            }

            if (state.projectileWakeResumeJob != null
                && !HasNonProjectileSearchDemand(pawn, state))
            {
                return false;
            }

            return state.sharedTargetSearch?.scanActive == true
                || RimKataSharedTargetSearch.Begin(
                    pawn,
                    state,
                    pawn.Position);
        }

        public static void ImportLegacyPrimaryState(
            Pawn pawn,
            int cooldownTicks,
            int warmupTicks,
            Thing plannedTarget,
            bool interception,
            bool closeAttack,
            bool closeContext)
        {
            RimKataPawnCombatState state = StateFor(pawn, true);
            if (state == null)
            {
                return;
            }

            BindCurrentWeapons(pawn, state);
            RimKataWeaponCycleState cycle = state.primaryWeaponCycle;
            cycle.cooldownTicksRemaining = Mathf.Max(cycle.cooldownTicksRemaining, Mathf.Max(0, cooldownTicks));
            if (cycle.plannedTarget == null && plannedTarget != null)
            {
                cycle.plannedTarget = plannedTarget;
                cycle.visualTarget = plannedTarget;
                cycle.plannedInterception = interception;
                cycle.plannedCloseAttack = closeAttack;
                cycle.plannedCloseContext = closeContext;
                cycle.plannedActionVerb = null;
                cycle.warmupTicksRemaining = Mathf.Max(1, warmupTicks);
                cycle.warmupTotalTicks = cycle.warmupTicksRemaining;
            }
        }

        public static bool TryGetVisualData(
            Pawn pawn,
            ThingWithComps weapon,
            out RimKataWeaponVisualData data)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            return TryGetVisualData(pawn, cycle, weapon, out data);
        }

        private static bool TryGetVisualData(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            ThingWithComps weapon,
            out RimKataWeaponVisualData data)
        {
            data = default(RimKataWeaponVisualData);
            if (cycle == null || cycle.weapon != weapon)
            {
                return false;
            }

            Thing liveVisualTarget = (cycle.HasPlan
                    || cycle.cooldownTicksRemaining > 0
                    || cycle.visualAimTicksRemaining > 0)
                && IsLiveVisualTarget(
                    pawn,
                    cycle.visualTarget)
                ? cycle.visualTarget
                : null;

            Thing livePlannedTarget = IsLiveVisualTarget(
                    pawn,
                    cycle.plannedTarget)
                ? cycle.plannedTarget
                : null;
            Thing targetThing = cycle.cooldownTicksRemaining > 0
                && liveVisualTarget != null
                ? liveVisualTarget
                : livePlannedTarget ?? liveVisualTarget;
            LocalTargetInfo target = targetThing != null && targetThing.Spawned
                ? new LocalTargetInfo(targetThing)
                : LocalTargetInfo.Invalid;
            data = new RimKataWeaponVisualData
            {
                weapon = weapon,
                target = target,
                warming = cycle.IsWarming,
                warmupTicksRemaining = Mathf.Max(0, cycle.warmupTicksRemaining),
                warmupTotalTicks = Mathf.Max(0, cycle.warmupTotalTicks),
                cooldownTicksRemaining = Mathf.Max(0, cycle.cooldownTicksRemaining)
            };
            return target.IsValid || cycle.cooldownTicksRemaining > 0;
        }

        internal static bool IsLiveVisualTarget(Pawn pawn, Thing target)
        {
            return pawn?.Map != null
                && target != null
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn.Map
                && (!(target is Projectile projectile)
                    || RimKataTargeting
                        .IsInterceptionTargetActive(projectile))
                && (!(target is Pawn targetPawn) || !targetPawn.Dead);
        }

        public static bool TryGetIndicatorVisualData(
            Pawn pawn,
            ThingWithComps weapon,
            out RimKataWeaponVisualData data,
            out bool claimsVanillaRangedCooldown)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            TryGetPotentialVanillaRangedCooldown(
                pawn,
                weapon,
                out Stance_Cooldown vanillaCooldown);
            return TryGetIndicatorVisualData(
                pawn,
                state,
                weapon,
                vanillaCooldown,
                out data,
                out claimsVanillaRangedCooldown,
                out Verb _);
        }

        internal static bool MayNeedCombatIndicatorFrame(Pawn pawn)
        {
            return pawn != null
                && (RimKataCombatStatePresenceCache.Contains(
                        pawn,
                        pawn.Map)
                    || TryGetPotentialVanillaRangedCooldown(
                        pawn,
                        null,
                        out Stance_Cooldown _));
        }

        internal static RimKataCombatIndicatorFrame
            GetCombatIndicatorFrameData(
                Pawn pawn,
                ThingWithComps primary,
                ThingWithComps secondary)
        {
            RimKataCombatIndicatorFrame frame =
                default(RimKataCombatIndicatorFrame);
            RimKataPawnCombatState state = StateFor(pawn, false);
            frame.pauseFireForDodge =
                RimKataTargetAccess.SettingsFor(pawn)?.movingFireEnabled == false
                && state?.DodgeVisualLocked == true;
            TryGetPotentialVanillaRangedCooldown(
                pawn,
                null,
                out Stance_Cooldown vanillaCooldown);
            if (TryGetAttackGizmoCloseTarget(
                    state,
                    out Thing closeTarget))
            {
                frame.closeTarget = closeTarget;
            }

            bool includeFocusedTargets = frame.closeTarget == null;
            frame.primary = GetCombatIndicatorWeaponFrame(
                pawn,
                state,
                primary,
                vanillaCooldown,
                includeFocusedTargets);
            frame.secondary = GetCombatIndicatorWeaponFrame(
                pawn,
                state,
                secondary,
                vanillaCooldown,
                includeFocusedTargets);
            return frame;
        }

        private static RimKataCombatIndicatorWeaponFrame
            GetCombatIndicatorWeaponFrame(
                Pawn pawn,
                RimKataPawnCombatState state,
                ThingWithComps weapon,
                Stance_Cooldown vanillaCooldown,
                bool includeFocusedTarget)
        {
            RimKataCombatIndicatorWeaponFrame frame =
                default(RimKataCombatIndicatorWeaponFrame);
            if (weapon == null)
            {
                return frame;
            }

            if (includeFocusedTarget
                && TryGetFocusedWeaponTarget(
                    pawn,
                    state,
                    weapon,
                    out Thing focusedTarget,
                    out bool fromAttackGizmo))
            {
                frame.focusedTarget = focusedTarget;
                frame.focusedTargetFromAttackGizmo = fromAttackGizmo;
            }

            if (!TryGetIndicatorVisualData(
                    pawn,
                    state,
                    weapon,
                    vanillaCooldown,
                    out RimKataWeaponVisualData visual,
                    out bool _,
                    out Verb verb))
            {
                return frame;
            }

            bool warming = visual.warming
                && visual.warmupTicksRemaining > 0
                && visual.warmupTotalTicks > 0;
            bool cooling = visual.cooldownTicksRemaining > 0;
            if ((!warming && !cooling) || verb == null)
            {
                return frame;
            }

            frame.visual = visual;
            frame.verb = verb;
            frame.visible = true;
            frame.remainingTicks = warming
                ? visual.warmupTicksRemaining
                : visual.cooldownTicksRemaining;
            return frame;
        }

        private static bool TryGetIndicatorVisualData(
            Pawn pawn,
            RimKataPawnCombatState state,
            ThingWithComps weapon,
            Stance_Cooldown vanillaCooldown,
            out RimKataWeaponVisualData data,
            out bool claimsVanillaRangedCooldown,
            out Verb verb)
        {
            RimKataWeaponCycleState cycle = CycleForWeapon(state, weapon);
            bool hasInternal = TryGetVisualData(
                pawn,
                cycle,
                weapon,
                out data);
            claimsVanillaRangedCooldown = false;
            verb = null;
            bool internalIndicator = hasInternal
                && ((data.warming
                        && data.warmupTicksRemaining > 0
                        && data.warmupTotalTicks > 0)
                    || data.cooldownTicksRemaining > 0);
            bool potentialVanillaCooldown = vanillaCooldown != null
                && vanillaCooldown.verb?.EquipmentSource == weapon;
            if (!internalIndicator && !potentialVanillaCooldown)
            {
                return hasInternal;
            }

            verb = RimKataWeaponSlotUtility.CombatVerb(pawn, weapon);
            if (!potentialVanillaCooldown
                || vanillaCooldown.verb != verb)
            {
                return hasInternal;
            }

            data.weapon = weapon;
            data.target = vanillaCooldown.focusTarg;
            data.warming = false;
            data.warmupTicksRemaining = 0;
            data.warmupTotalTicks = 0;
            data.cooldownTicksRemaining = Mathf.Max(
                data.cooldownTicksRemaining,
                vanillaCooldown.ticksLeft);
            claimsVanillaRangedCooldown = true;
            return true;
        }

        private static bool TryGetPotentialVanillaRangedCooldown(
            Pawn pawn,
            ThingWithComps weapon,
            out Stance_Cooldown cooldown)
        {
            cooldown = pawn?.stances?.curStance as Stance_Cooldown;
            Verb verb = cooldown?.verb;
            ThingWithComps cooldownWeapon =
                verb?.EquipmentSource as ThingWithComps;
            if (verb == null
                || verb.IsMeleeAttack
                || cooldown.ticksLeft <= 0
                || !cooldown.focusTarg.IsValid
                || verb.verbProps?.drawAimPie != true
                || cooldownWeapon == null
                || (weapon != null && cooldownWeapon != weapon))
            {
                cooldown = null;
                return false;
            }

            return true;
        }

        public static bool TryGetNextAim(
            Pawn pawn,
            out ThingWithComps weapon,
            out LocalTargetInfo target)
        {
            weapon = null;
            target = LocalTargetInfo.Invalid;
            RimKataPawnCombatState state = StateFor(pawn, false);
            if (!TryGetNextAim(
                    pawn,
                    state,
                    out RimKataWeaponCycleState cycle,
                    out target))
            {
                return false;
            }

            weapon = cycle.weapon;
            return true;
        }

        private static bool TryGetNextAim(
            Pawn pawn,
            RimKataPawnCombatState state,
            out RimKataWeaponCycleState cycle,
            out LocalTargetInfo target)
        {
            cycle = null;
            target = LocalTargetInfo.Invalid;
            if (state == null)
            {
                return false;
            }

            RimKataWeaponCycleState first = ChooseBodyAimCycle(pawn, state);
            ThingWithComps weapon = first?.weapon;
            if (weapon == null
                || !TryGetVisualData(
                    pawn,
                    first,
                    weapon,
                    out RimKataWeaponVisualData visual)
                || !visual.target.IsValid)
            {
                return false;
            }

            cycle = first;
            target = visual.target;
            return true;
        }

        public static void NotifyLoadoutChanged(Pawn pawn)
        {
            NotifyLoadoutChanged(pawn, InvalidateWeaponBindings(pawn));
        }

        internal static void NotifyLoadoutChanged(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (state == null)
            {
                return;
            }

            Job job = pawn.CurJob;
            if (job?.def == RimKataDefOf.RimKata_Attack)
            {
                ThingWithComps orderedWeapon = ResolveWeaponScopedFocusJobWeapon(
                    pawn, state, job.targetA.Thing, job.playerForced,
                    job.killIncappedTarget);
                if (orderedWeapon != null && !WeaponStillHeld(pawn, orderedWeapon))
                {
                    state.loadoutInvalidatedCombatJob = job;
                }
            }

            if (!WeaponStillHeld(pawn, state.responsePoseWeapon))
            {
                state.CancelResponsePose();
            }

            if (!WeaponStillHeld(pawn, state.deflectionWeapon))
            {
                state.CancelDeflection();
            }
        }

        public static bool ConsumeLoadoutInvalidatedCombatJob(
            Pawn pawn,
            Job job)
        {
            return ConsumeLoadoutInvalidatedCombatJob(pawn, job, StateFor(pawn, false));
        }

        private static bool ConsumeLoadoutInvalidatedCombatJob(
            Pawn pawn,
            Job job,
            RimKataPawnCombatState state)
        {
            Job invalidatedJob = state?.loadoutInvalidatedCombatJob;
            if (invalidatedJob == null)
            {
                return false;
            }

            if (invalidatedJob != job)
            {
                if (pawn?.CurJob != invalidatedJob)
                {
                    state.loadoutInvalidatedCombatJob = null;
                }

                return false;
            }

            state.loadoutInvalidatedCombatJob = null;
            return !WeaponStillHeld(
                pawn, job.verbToUse?.EquipmentSource as ThingWithComps);
        }

        private static bool WeaponStillHeld(Pawn pawn, ThingWithComps weapon)
        {
            return weapon != null
                && !weapon.Destroyed
                && pawn?.equipment?.AllEquipmentListForReading?.Contains(weapon)
                    == true;
        }

        public static void TickIdleCycleTimers(Pawn pawn)
        {
            RimKataPawnCombatState state = StateFor(pawn, false);
            int currentTick = Find.TickManager.TicksGame;
            if (state == null
                || state.dualLastDrivenTick == currentTick)
            {
                return;
            }

            BindCurrentWeapons(pawn, state);
            if (state.primaryWeaponCycle.weapon != null)
            {
                state.primaryWeaponCycle.ClearInvalidVisualTarget(pawn);
            }
            if (state.secondaryWeaponCycle.weapon != null)
            {
                state.secondaryWeaponCycle.ClearInvalidVisualTarget(pawn);
            }
            if (state.primaryWeaponCycle.weapon != null)
            {
                state.primaryWeaponCycle.TickTimers();
            }
            if (state.secondaryWeaponCycle.weapon != null)
            {
                state.secondaryWeaponCycle.TickTimers();
            }
        }

        public static void DeactivateNonJobCycleWork(Pawn pawn)
        {
            DeactivateNonJobCycleWork(pawn, StateFor(pawn, false));
        }

        private static void DeactivateNonJobCycleWork(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (state == null)
            {
                return;
            }

            BindCurrentWeapons(pawn, state);
            if ((state.primaryWeaponCycle.weapon != null
                && NormalizeUnavailableCycleWork(
                    pawn,
                    state,
                    state.primaryWeaponCycle))
                | (state.secondaryWeaponCycle.weapon != null
                && NormalizeUnavailableCycleWork(
                    pawn,
                    state,
                    state.secondaryWeaponCycle)))
            {
                state.ResetCandidateSaturationExpansion(true);
            }
            NormalizeInvalidInterceptionState(pawn, state);

            RefreshDualEngagementState(pawn, state);
            if (state.dualEngagementActive)
            {
                RearmOpeningOwnerIfBothWaiting(state);
                if (HasContinuationSearchWork(pawn)
                    && !state.dedicatedFollowupJobPending
                    && pawn?.Drafted != true)
                {
                    QueueDedicatedFollowupJob(pawn, null);
                }
                return;
            }

            CancelUnfiredWarmupForDraftChange(state.primaryWeaponCycle);
            CancelUnfiredWarmupForDraftChange(state.secondaryWeaponCycle);
            state.dualEngagementActive = false;
            state.ResetCandidateSaturationExpansion(true);
            RearmOpeningOwnerIfBothWaiting(state);
        }

        public static void CancelOffenseForMentalState(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (state == null)
            {
                return;
            }

            state.CancelDraftedFire(false);
            state.ClearDraftedMovementSearchTracking();
            state.CancelCloseCombat();
            state.incomingThreatSource = null;
            state.incomingThreatTicksRemaining = 0;
            state.ClearCloseAttackRequest();
            state.dedicatedContinuityTarget = null;
            state.dedicatedContinuityUntilTick = -1;
            state.loadoutInvalidatedCombatJob = null;
            state.weaponSwapPending = false;
            Reset(pawn, false);

            if (pawn?.stances?.curStance is Stance_RimKataAim)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }
        }

        public static void Reset(Pawn pawn, bool clearCooldowns)
        {

            RimKataPawnCombatState state = StateFor(pawn, false);
            if (state == null)
            {
                return;
            }

            state.weaponBindingsDirty = true;
            state.dualEngagementActive = false;
            state.dualLastDrivenTick = -1;
            state.dualCloseCombatActive = false;

            state.dualCloseTarget = null;
            state.engagementOwnerWeapon = null;
            state.sharedTargetSearch?.Reset();
            state.ClearDedicatedFollowupJobRequest();
            state.projectileWakeResumeJob = null;
            if (clearCooldowns)
            {
                state.primaryWeaponCycle.Reset();
                state.secondaryWeaponCycle.Reset();
            }
            else
            {
                ResetCyclePreservingCooldown(state.primaryWeaponCycle);
                ResetCyclePreservingCooldown(state.secondaryWeaponCycle);
                state.dualLastDrivenTick = Find.TickManager.TicksGame;
            }
            state.ResetCandidateSaturationExpansion(true);
        }

        private static void ResetCyclePreservingCooldown(
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null)
            {
                return;
            }

            ThingWithComps weapon = cycle.weapon;
            int cooldown = Mathf.Max(0, cycle.cooldownTicksRemaining);
            bool cooldownFromVanillaOpening = cooldown > 0
                && cycle.cooldownFromVanillaOpening;
            cycle.Reset();
            cycle.Bind(weapon);
            cycle.cooldownTicksRemaining = cooldown;
            cycle.cooldownFromVanillaOpening = cooldownFromVanillaOpening;
        }

        private static bool TryGetContinuationTarget(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            bool playerForced,
            bool killIncappedTarget,
            out Thing target)
        {
            target = cycle?.plannedTarget ?? cycle?.cachedCandidateTarget;
            bool closeContext = cycle?.plannedCloseContext == true;
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                cycle?.weapon);
            if (pawn?.Map == null
                || cycle == null
                || verb == null
                || cycle.plannedInterception
                || cycle.cachedCandidateInterception
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map)
            {
                target = null;
                return false;
            }

            bool validForCurrentMode =
                RimKataEligibility.RandomAttackEnabledForPawn(pawn)
                    ? RimKataSharedTargetSearch.IsValidForVerb(
                            pawn,
                            verb,
                            target)
                    : ValidCurrentTargetForVerb(
                        pawn,
                        verb,
                        target,
                        playerForced,
                        killIncappedTarget,
                        closeContext);
            if (!validForCurrentMode
                || !pawn.CanReach(
                    target,
                    PathEndMode.Touch,
                    Danger.Deadly))
            {
                target = null;
                return false;
            }

            return true;
        }

        private static bool CycleHasContinuationSearchWork(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            return cycle != null
                && ((RimKataEligibility.RandomAttackEnabledForPawn(pawn)
                        && cycle.HasAutomaticCandidates)
                    || cycle.cachedCandidateTarget != null
                    || cycle.plannedInterception
                    || cycle.cachedCandidateInterception);
        }

        private static bool HasProjectileOnlySearchWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget = null)
        {
            if (pawn?.Map == null
                || state == null
                || HasNonProjectileSearchDemand(
                    pawn,
                    state,
                    assignedTarget))
            {
                return false;
            }

            return HasActiveInterceptionWork(pawn, state.primaryWeaponCycle)
                || HasActiveInterceptionWork(pawn, state.secondaryWeaponCycle);
        }

        private static bool HasNonProjectileSearchDemand(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget = null)
        {
            return state != null
                && (state.dualCloseCombatActive
                    || IsLiveNonProjectileTarget(pawn, assignedTarget)
                    || IsLiveNonProjectileTarget(
                        pawn,
                        state.closeAttackRequestTarget)
                    || HasNonProjectileCycleWork(
                        pawn,
                        state,
                        state.primaryWeaponCycle)
                    || HasNonProjectileCycleWork(
                        pawn,
                        state,
                        state.secondaryWeaponCycle));
        }

        private static bool HasNonProjectileCycleWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null)
            {
                return false;
            }

            bool closeContext = cycle.plannedCloseContext
                || state?.dualCloseCombatActive == true;
            Verb verb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                cycle.weapon);
            bool cachedTargetUsable = !cycle.cachedCandidateInterception
                && cycle.cachedCandidateTarget != null
                && (RimKataEligibility.RandomAttackEnabledForPawn(pawn)
                    ? RimKataSharedTargetSearch.IsValidForVerb(
                        pawn,
                        verb,
                        cycle.cachedCandidateTarget)
                    : ValidCurrentTargetForVerb(
                        pawn,
                        verb,
                        cycle.cachedCandidateTarget,
                        false,
                        false,
                        closeContext));
            bool plannedTargetUsable = verb != null
                && !cycle.plannedInterception
                && IsLiveNonProjectileTarget(pawn, cycle.plannedTarget)
                && CanHitTargetForCombatContext(
                    pawn,
                    verb,
                    cycle.plannedTarget,
                    cycle.plannedCloseAttack);
            return FocusedTargetUsableNow(
                    pawn,
                    cycle,
                    verb,
                    closeContext)
                || cachedTargetUsable
                || plannedTargetUsable;
        }

        private static bool HasActiveInterceptionWork(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            return cycle != null
                && ((cycle.plannedInterception
                        && IsActiveInterceptionTarget(
                            pawn,
                            cycle.plannedTarget as Projectile))
                    || (cycle.cachedCandidateInterception
                        && IsActiveInterceptionTarget(
                            pawn,
                            cycle.cachedCandidateTarget as Projectile)));
        }

        private static bool IsLiveNonProjectileTarget(Pawn pawn, Thing target)
        {
            return target != null
                && !(target is Projectile)
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn?.Map
                && (!(target is Pawn targetPawn)
                    || RimKataTargeting.IsPawnTargetStateValid(targetPawn));
        }

        private static Thing ResolveCloseTarget(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool assignedTargetValidated = false,
            bool assignedTargetInTouchRange = false)
        {
            // The immediate-target test below is the authoritative validation.
            // Reading CloseAttackRequestActive first would perform the same
            // target and reachability checks twice on this hot path.
            Thing requested = state?.closeAttackRequestTarget;
            if (requested != assignedTarget && IsImmediateCloseTarget(
                pawn,
                requested,
                playerForced,
                killIncappedTarget))
            {
                return requested;
            }

            if (assignedTargetValidated
                ? assignedTarget is Pawn && assignedTargetInTouchRange
                : IsImmediateCloseTarget(
                pawn,
                assignedTarget,
                playerForced,
                killIncappedTarget))
            {
                return assignedTarget;
            }

            Thing trigger = state?.closeCombatTrigger;
            if (IsImmediateCloseTarget(pawn, trigger, false, false))
            {
                return trigger;
            }

            return null;
        }

        private static Thing ResolveTickCloseTarget(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool ordinaryAttackAllowed,
            Thing resolvedCloseTarget,
            bool closeTargetResolutionKnown)
        {
            if (!ordinaryAttackAllowed)
            {
                return null;
            }

            return closeTargetResolutionKnown
                ? resolvedCloseTarget
                : ResolveCloseTarget(
                    pawn,
                    state,
                    assignedTarget,
                    playerForced,
                    killIncappedTarget);
        }

        private static bool IsImmediateCloseTarget(
            Pawn pawn,
            Thing target,
            bool playerForced,
            bool killIncappedTarget)
        {
            return pawn?.Map != null
                && target != null
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn.Map
                && (playerForced
                    || RimKataTargeting.IsAutomaticEnemy(pawn, target))
                && target is Pawn targetPawn
                && RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    playerForced && killIncappedTarget)
                && pawn.CanReachImmediate(target, PathEndMode.Touch);
        }

        private static void HandleCloseCombatTransition(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool closeCombatContext,
            Thing closeTarget,
            bool allowAutomaticEntryCandidate = true)
        {
            bool restartCloseContext = closeCombatContext
                && (!state.dualCloseCombatActive
                    || state.dualCloseTarget == null);
            bool changedCloseTarget = closeCombatContext
                && (!state.dualCloseCombatActive || state.dualCloseTarget != closeTarget);
            if (changedCloseTarget)
            {
                state.dualCloseCombatActive = true;
                state.dualCloseTarget = closeTarget;
                state.EnterCloseCombat(closeTarget);
                if (restartCloseContext)
                {
                    SanitizeCycleForCloseCombat(
                        pawn,
                        state,
                        state.primaryWeaponCycle);
                    SanitizeCycleForCloseCombat(
                        pawn,
                        state,
                        state.secondaryWeaponCycle);
                }
                if (allowAutomaticEntryCandidate
                    && RimKataTargetAccess.SettingsFor(pawn)?.randomAttackEnabled != false)
                {
                    RimKataSharedTargetSearch.TryAddKnownAutomaticTarget(
                        pawn, state, closeTarget, true);
                }
                return;
            }

            if (closeCombatContext || !state.dualCloseCombatActive)
            {
                return;
            }

            bool preservePlayerRushRequest = pawn?.Drafted != true
                && state.IsPlayerRushRequestFor(state.closeAttackRequestTarget);
            state.dualCloseCombatActive = false;
            state.dualCloseTarget = null;
            state.CancelCloseCombat();
            if (!preservePlayerRushRequest)
            {
                state.ClearCloseAttackRequest();
            }
            if (!AutomaticRangedFireAllowed(pawn))
            {
                // Close combat admitted adjacent targets for physical attacks.
                // They stop being ranged candidates when that context ends.
                ClearRangedTargetingForHoldFire(pawn, state.primaryWeaponCycle, true);
                ClearRangedTargetingForHoldFire(pawn, state.secondaryWeaponCycle, true);
            }
            state.ResetCandidateSaturationExpansion(true);
            RimKataSharedTargetSearch.Restart(pawn, state, pawn.Position);
        }

        private static void SanitizeCycleForCloseCombat(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle)
        {
            if (cycle?.weapon == null)
            {
                return;
            }
            Verb verb = BoundCombatVerb(pawn, cycle);

            if (pawn?.stances?.curStance is Stance_RimKataAim aim
                && aim.verb?.EquipmentSource == cycle.weapon
                && aim.verb != verb)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }

            if (verb == null)
            {
                cycle.ClearAutomaticCandidates();
                cycle.ClearPlan();
                cycle.focusedTarget = null;
                cycle.focusedTargetFromAttackGizmo = false;
                cycle.visualTarget = null;
                cycle.visualAimTicksRemaining = 0;
                return;
            }

            // Unselected stored candidates keep their admission until selection.
            // Check each distinct active reference once using the prepared slot.
            Thing cachedTarget = cycle.cachedCandidateTarget;
            bool cachedValid = cachedTarget == null
                || IsValidCloseCycleTarget(pawn, state, cycle, verb, cachedTarget);
            if (!cachedValid)
            {
                Thing invalidCachedTarget = cycle.cachedCandidateTarget;
                EvictAutomaticCandidate(
                    pawn,
                    state,
                    cycle,
                    invalidCachedTarget,
                    false);
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
            }

            Thing plannedTarget = cycle.plannedTarget;
            bool plannedValid = plannedTarget == null
                || cycle.plannedInterception
                || (plannedTarget == cachedTarget
                    ? cachedValid
                    : IsValidCloseCycleTarget(pawn, state, cycle, verb, plannedTarget));
            if (plannedTarget != null
                && !cycle.plannedInterception
                && !plannedValid)
            {
                EvictAutomaticCandidate(pawn, state, cycle, plannedTarget, false);
                ClearTargetPreservingCycle(cycle);
            }

            Thing visualTarget = cycle.visualTarget;
            if (visualTarget != null
                && !(visualTarget is Projectile)
                && !(visualTarget == cachedTarget ? cachedValid
                    : visualTarget == plannedTarget ? plannedValid
                    : IsValidCloseCycleTarget(pawn, state, cycle, verb, visualTarget)))
            {
                EvictAutomaticCandidate(pawn, state, cycle, visualTarget, false);
                cycle.visualTarget = null;
                cycle.visualAimTicksRemaining = 0;
            }
        }

        private static bool IsValidCloseCycleTarget(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing target)
        {
            return RimKataSharedTargetSearch.IsValidForVerb(
                pawn, state, cycle, verb, target,
                !(target is Projectile)
                    && cycle.ContainsAutomaticCandidate(target));
        }

        private static void ImportLegacyDraftedState(RimKataPawnCombatState state)
        {
            RimKataWeaponCycleState cycle = state.primaryWeaponCycle;
            if (cycle == null
                || cycle.Active
                || (state.draftedCooldownTicksRemaining <= 0
                    && state.draftedPlannedTarget == null))
            {
                return;
            }

            cycle.cooldownTicksRemaining = Mathf.Max(0, state.draftedCooldownTicksRemaining);
            cycle.warmupTicksRemaining = state.draftedPlannedTarget != null
                ? Mathf.Max(1, state.draftedWarmupTicksRemaining)
                : -1;
            cycle.warmupTotalTicks = Mathf.Max(0, cycle.warmupTicksRemaining);
            cycle.plannedTarget = state.draftedPlannedTarget;
            cycle.visualTarget = state.draftedPlannedTarget;
            cycle.plannedInterception = state.draftedPlannedInterception;
            cycle.plannedCloseAttack = state.draftedPlannedCloseAttack;
            cycle.plannedCloseContext = state.draftedPlannedCloseContext;
            cycle.plannedActionVerb = null;
            state.draftedCooldownTicksRemaining = 0;
            state.draftedWarmupTicksRemaining = -1;
            state.draftedPlannedTarget = null;
            state.draftedPlannedInterception = false;
            state.draftedPlannedCloseAttack = false;
            state.draftedPlannedCloseContext = false;
        }

        private static void BindCurrentWeapons(
            Pawn pawn,
            RimKataPawnCombatState state,
            bool accessVerified = false)
        {
            int revision = RimKataEquipmentUtility.WeaponConfigurationRevision;
            if (!state.weaponBindingsDirty
                && state.weaponConfigurationRevision == revision)
            {
                return;
            }

            bool hasAccess = accessVerified || RimKataEligibility.HasRimKataAccess(pawn);
            ThingWithComps primary = hasAccess
                ? RimKataWeaponSlotUtility.PrimaryWeapon(pawn)
                : null;
            ThingWithComps secondary = RimKataWeaponSlotUtility.CanUseSecondarySlot(
                    pawn,
                    primary,
                    hasAccess)
                    ? accessVerified
                        ? RimKataWeaponSlotUtility
                            .SecondaryWeaponWithVerifiedAccess(pawn)
                        : RimKataWeaponSlotUtility.SecondaryWeapon(pawn)
                    : null;

            // Keep timer/candidate ownership attached to the weapon during a
            // slot swap or automatic promotion of the surviving secondary.
            bool slotsMoved = (primary != state.primaryWeaponCycle.weapon
                    && primary != null
                    && primary == state.secondaryWeaponCycle.weapon)
                || (secondary != state.secondaryWeaponCycle.weapon
                    && secondary != null
                    && secondary == state.primaryWeaponCycle.weapon);
            if (slotsMoved)
            {
                RimKataWeaponCycleState previousPrimary = state.primaryWeaponCycle;
                state.primaryWeaponCycle = state.secondaryWeaponCycle;
                state.secondaryWeaponCycle = previousPrimary;
            }

            bool changed = state.primaryWeaponCycle.Bind(primary)
                | state.secondaryWeaponCycle.Bind(secondary);
            ResolveWeaponBinding(pawn, state.primaryWeaponCycle);
            ResolveWeaponBinding(pawn, state.secondaryWeaponCycle);
            Job job = pawn.CurJob;
            if (job?.def == RimKataDefOf.RimKata_Attack
                && !job.playerForced
                && job.verbToUse?.EquipmentSource is ThingWithComps previousJobWeapon
                && previousJobWeapon != primary && previousJobWeapon != secondary)
            {
                job.verbToUse = state.primaryWeaponCycle.boundVerb
                    ?? state.secondaryWeaponCycle.boundVerb;
            }
            state.weaponBindingsDirty = false;
            bool configurationChanged = state.weaponConfigurationRevision >= 0
                && state.weaponConfigurationRevision != revision;
            state.weaponConfigurationRevision = revision;
            if (changed || slotsMoved || configurationChanged)
            {
                state.sharedTargetSearch?.Reset();
                state.ResetCandidateSaturationExpansion(true);
                if (state.engagementOwnerWeapon != primary
                    && state.engagementOwnerWeapon != secondary)
                {
                    state.engagementOwnerWeapon = null;
                }
            }
        }

        internal static RimKataPawnCombatState InvalidateWeaponBindings(Pawn pawn)
        {
            if (RimKataCombatStatePresenceCache.TryGetOwner(
                    pawn, out RimKataMapComponent owner))
            {
                RimKataPawnCombatState state = owner.GetState(pawn, false);
                if (state != null)
                {
                    state.weaponBindingsDirty = true;
                }
                return state;
            }
            return null;
        }

        internal static void InvalidateProfileSettings(Pawn pawn)
        {
            RimKataPawnCombatState state = InvalidateWeaponBindings(pawn);
            if (state == null)
            {
                return;
            }

            state.sharedTargetSearch?.Reset();
            state.ResetCandidateSaturationExpansion(true);
        }

        private static void ResolveWeaponBinding(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            Verb previousVerb = cycle.boundVerb;
            cycle.ordinaryWeaponEnabled =
                RimKataEquipmentUtility.IsWeaponEnabled(cycle.weapon?.def);
            cycle.boundVerb = cycle.weapon == null
                ? null
                : RimKataWeaponSlotUtility.CombatVerb(pawn, cycle.weapon);
            if (previousVerb != cycle.boundVerb)
            {
                cycle.nativeAttack?.Cancel();
                if (cycle.nativeAttack?.Executing != true)
                    RimKataPreparedWeaponData.Restore(previousVerb);
                cycle.plannedActionVerb = null;
            }
            if (cycle.NativeAttackPending
                && !RimKataPreparedWeaponData.IsCurrent(cycle.boundVerb))
            {
                ApplyInterruptedBurstCooldown(pawn, cycle, cycle.boundVerb);
            }
            RimKataNativeAttack.Bind(cycle.boundVerb);
            if (cycle.boundVerb != null)
            {
                bool interruptedAfterLoad =
                    RimKataNativeAttack.ConsumeInterruptedBurstAfterLoad(cycle.boundVerb);
                if (interruptedAfterLoad || cycle.completeInterruptedAttackAfterLoad)
                {
                    // The saved target may have disappeared (notably an
                    // intercepted projectile). Consume recovery before planning
                    // so the next newly selected target is never skipped.
                    cycle.completeInterruptedAttackAfterLoad = false;
                    cycle.cooldownTicksRemaining = Mathf.Max(cycle.cooldownTicksRemaining,
                        RimKataCombatMath.CooldownTicksForSingleShot(cycle.boundVerb, pawn, false));
                    cycle.cooldownFromVanillaOpening |= cycle.openingWarmupPending;
                    cycle.firedInCurrentOpening |= cycle.openingWarmupPending;
                    cycle.openingWarmupPending = false;
                    cycle.openingWarmupBonusTicks = 0;
                    cycle.ClearPlan();
                }
            }
        }

        private static Verb BoundCombatVerb(Pawn pawn, RimKataWeaponCycleState cycle)
        {
            // A missing Verb can become available after a transient equipment
            // update. Keep the existing per-tick null retry in CombatVerb.
            return cycle.boundVerb
                ?? (cycle.boundVerb = RimKataWeaponSlotUtility.CombatVerb(
                    pawn, cycle.weapon));
        }

        private static bool NormalizeUnavailableCycleWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle)
        {
            CycleVerbAvailability availability = default;
            return NormalizeUnavailableCycleWork(
                pawn,
                state,
                cycle,
                RimKataEligibility.RandomAttackEnabledForPawn(pawn),
                ref availability);
        }

        private struct CycleVerbAvailability
        {
            public Verb verb;
            public bool evaluated;
            public bool usable;
            public bool closeContext;
            public bool selectionEmpty;
            public Thing checkedTarget;
            public Verb checkedVerb;
            public bool checkedCloseContext;
        }

        private static bool IsCycleVerbUsable(
            Pawn pawn,
            Verb verb,
            bool closeCombatContext,
            ref CycleVerbAvailability availability)
        {
            if (!availability.evaluated || availability.verb != verb
                || availability.closeContext != closeCombatContext)
            {
                availability.verb = verb;
                availability.closeContext = closeCombatContext;
                availability.usable = verb != null
                    && VerbUsable(pawn, verb, closeCombatContext);
                availability.evaluated = true;
            }
            return availability.usable;
        }

        private static bool NormalizeUnavailableCycleWork(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            bool randomAttackEnabled,
            ref CycleVerbAvailability availability)
        {
            if (cycle == null)
            {
                return false;
            }

            bool changed = false;
            if (!randomAttackEnabled
                && cycle.HasAutomaticCandidates)
            {
                cycle.ClearStoredAutomaticCandidates();
                cycle.automaticCandidateCollectionClosed = false;
                cycle.pendingCandidateLimitOverride = 0;
                cycle.activeCandidateLimitOverride = 0;
                changed = true;
            }

            if (cycle.weapon == null)
            {
                return changed;
            }

            if (IsVanillaAutomaticWeaponBusy(pawn, state, cycle))
            {
                return changed;
            }

            bool hadUnavailableWork = cycle.HasAutomaticCandidates
                || cycle.cachedCandidateTarget != null
                || cycle.focusedTarget != null
                || cycle.HasPlan
                || cycle.openingWarmupPending
                || cycle.lastFiredTarget != null
                || cycle.firedInCurrentOpening
                || cycle.NativeAttackPending
                || cycle.warmupTicksRemaining > 0
                || cycle.visualTarget != null
                || cycle.visualAimTicksRemaining > 0;
            if (!hadUnavailableWork)
            {
                return changed;
            }

            bool closeContext = state?.dualCloseCombatActive == true;
            Verb verb = availability.evaluated
                ? availability.verb : BoundCombatVerb(pawn, cycle);
            bool ordinaryWeaponEnabled = cycle.ordinaryWeaponEnabled;
            bool interceptionWork = !ordinaryWeaponEnabled
                && HasActiveInterceptionWork(pawn, cycle);
            if (interceptionWork)
            {
                changed |= cycle.HasAutomaticCandidates
                    || cycle.focusedTarget != null
                    || (cycle.HasPlan && !cycle.plannedInterception);
                cycle.ClearAutomaticCandidates();
                cycle.focusedTarget = null;
                cycle.focusedTargetFromAttackGizmo = false;
                if (cycle.HasPlan && !cycle.plannedInterception)
                {
                    ClearTargetPreservingCycle(cycle);
                    cycle.warmupTicksRemaining = -1;
                    cycle.warmupTotalTicks = 0;
                }
            }
            if ((ordinaryWeaponEnabled
                    || (RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled != false
                        && verb is Verb_LaunchProjectile
                        && interceptionWork))
                && verb != null
                && IsCycleVerbUsable(pawn, verb, closeContext, ref availability))
            {
                return changed;
            }

            ApplyInterruptedBurstCooldown(pawn, cycle, verb);
            cycle.ClearAutomaticCandidates();
            cycle.cachedCandidateTarget = null;
            cycle.cachedCandidateInterception = false;
            cycle.focusedTarget = null;
            cycle.focusedTargetFromAttackGizmo = false;
            cycle.lastFiredTarget = null;
            cycle.firedInCurrentOpening = false;
            cycle.ClearPlan();
            cycle.openingWarmupPending = false;
            cycle.openingWarmupBonusTicks = 0;
            cycle.warmupTicksRemaining = -1;
            cycle.warmupTotalTicks = 0;
            cycle.visualTarget = null;
            cycle.visualAimTicksRemaining = 0;
            return changed || hadUnavailableWork;
        }

        private static void TickWeaponCycle(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool closeCombatContext,
            bool blockedByStance,
            out Thing promotedAutomaticTarget,
            bool allowAutomaticRangedFire,
            bool randomAttackEnabled,
            int currentTick,
            ThingWithComps weaponScopedFocusJobWeapon,
            ref CycleVerbAvailability availability)
        {
            promotedAutomaticTarget = null;
            if (weaponScopedFocusJobWeapon != null
                && cycle.weapon != weaponScopedFocusJobWeapon)
            {
                assignedTarget = null;
                playerForced = false;
                killIncappedTarget = false;
            }
            if (cycle.lastDrivenTick == currentTick)
            {
                return;
            }
            cycle.lastDrivenTick = currentTick;
            if (cycle.nativeAttack?.Pending == true)
            {
                return;
            }
            if (cycle.ResponseCooldownAppliedThisTick)
            {
                // A response retains its target reference, but offensive preparation
                // starts no earlier than this slot's next game tick.
                return;
            }
            if (IsVanillaAutomaticWeaponBusy(pawn, state, cycle))
            {
                return;
            }
            Verb verb = availability.evaluated
                ? availability.verb : BoundCombatVerb(pawn, cycle);
            bool ordinaryWeaponEnabled = cycle.ordinaryWeaponEnabled;
            if (cycle.weapon == null
                || (!ordinaryWeaponEnabled
                    && (!(verb is Verb_LaunchProjectile)
                        || RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled == false
                        || !HasActiveInterceptionWork(pawn, cycle)))
                || verb == null
                || !IsCycleVerbUsable(pawn, verb, closeCombatContext, ref availability))
            {
                if (NormalizeUnavailableCycleWork(
                    pawn,
                    state,
                    cycle,
                    randomAttackEnabled,
                    ref availability))
                {
                    state?.ResetCandidateSaturationExpansion(true);
                }
                return;
            }

            bool newAutomaticRangedAttacksBlocked =
                !allowAutomaticRangedFire
                && !closeCombatContext
                && !verb.IsMeleeAttack
                && !playerForced;
            bool requestAutomaticRefill = allowAutomaticRangedFire
                && !closeCombatContext
                && randomAttackEnabled;

            bool focusedTargetControlsCycle = ordinaryWeaponEnabled && PrepareFocusedTarget(
                pawn,
                cycle,
                verb,
                closeCombatContext,
                ref availability);
            bool dedicatedAssignedTarget = ordinaryWeaponEnabled && assignedTarget != null
                && pawn?.CurJobDef == RimKataDefOf.RimKata_Attack
                && pawn.CurJob.targetA.Thing == assignedTarget;
            // Target ownership invariant: weapon cycles may change targets independently,
            // including in close combat. Keep job.targetA while it remains alive and within
            // the unified attack range; only the invalid-target or range-exit paths may
            // replace it. closeCombatContext must never force the Job target back into a cycle.
            bool directAssignedTarget = ordinaryWeaponEnabled
                && assignedTarget != null && playerForced;
            if (focusedTargetControlsCycle && !cycle.HasPlan)
            {
                return;
            }

            if (newAutomaticRangedAttacksBlocked
                && !focusedTargetControlsCycle
                && !cycle.HasPlan)
            {
                return;
            }

            Thing rangeCheckedTarget = null;
            if (!focusedTargetControlsCycle)
            {
                Thing rangeTarget = cycle.plannedTarget ?? cycle.visualTarget;
                if (!InterruptMovingFireOutsideAutomaticRange(
                    pawn,
                    state,
                    cycle,
                    verb,
                    rangeTarget,
                    requestAutomaticRefill,
                    randomAttackEnabled))
                {
                    rangeCheckedTarget = rangeTarget;
                }
            }

            bool previousCloseContext = cycle.plannedCloseContext;
            bool previousCloseAttack = cycle.plannedCloseAttack;
            PromoteApproachingShotToCloseContext(pawn, cycle, verb, closeCombatContext);
            if (cycle.plannedCloseContext != previousCloseContext
                || cycle.plannedCloseAttack != previousCloseAttack)
            {
                rangeCheckedTarget = null;
            }
            if (cycle.plannedActionVerb != null
                && !verb.IsMeleeAttack
                && cycle.plannedActionVerb.IsMeleeAttack
                    != UsesPhysicalMeleeAction(
                        pawn, verb, closeCombatContext && cycle.plannedCloseAttack))
            {
                // A close-range transition or hold-fire toggle can change the
                // action after aiming started. Keep its elapsed timers only.
                cycle.plannedActionVerb = null;
            }
            Thing checkedTarget = null;
            bool explicitPlan = cycle.plannedTarget != null
                && (cycle.plannedTarget == cycle.focusedTarget
                    || (playerForced && cycle.plannedTarget == assignedTarget));
            // A reserved automatic target waits for the last cooldown tick.
            // Active aiming still cancels/reselects here in the same tick.
            // Keep this result through the immediate shot of the same slot.
            bool checkPreparedTarget = cycle.plannedInterception
                || explicitPlan
                || cycle.cooldownTicksRemaining <= 1;
            if (cycle.HasPlan
                && checkPreparedTarget)
            {
                if (ValidPlan(
                    pawn,
                    cycle,
                    verb,
                    assignedTarget,
                    playerForced,
                    killIncappedTarget,
                    closeCombatContext,
                    ref availability))
                {
                    checkedTarget = cycle.plannedTarget;
                }
                else
                {
                    rangeCheckedTarget = null;
                    HandleInvalidPlanAtExecution(
                        pawn,
                        state,
                        cycle,
                        verb,
                        assignedTarget,
                        playerForced,
                        killIncappedTarget,
                        closeCombatContext,
                        requestAutomaticRefill);
                }
            }

            if (newAutomaticRangedAttacksBlocked
                && !focusedTargetControlsCycle
                && !cycle.HasPlan)
            {
                return;
            }

            bool automaticPromotionAttempted = false;
            if (!focusedTargetControlsCycle
                && !directAssignedTarget
                && !cycle.HasPlan)
            {
                rangeCheckedTarget = null;
                if (cycle.cachedCandidateTarget == null)
                {
                    TryCacheSharedCandidate(
                        pawn,
                        state,
                        cycle,
                        assignedTarget,
                        randomAttackEnabled,
                        verb,
                        ref availability);
                }

                automaticPromotionAttempted =
                    cycle.cachedCandidateTarget != null;
                if (automaticPromotionAttempted
                    && TryPromoteCachedCandidate(
                        pawn,
                        state,
                        cycle,
                        verb,
                        killIncappedTarget,
                        closeCombatContext,
                        requestAutomaticRefill,
                        randomAttackEnabled,
                        out Thing promotedCandidate,
                        ref availability))
                {
                    promotedAutomaticTarget = promotedCandidate;
                }
            }

            if (cycle.cooldownTicksRemaining > 1)
            {
                return;
            }

            if (!focusedTargetControlsCycle
                && !cycle.HasPlan
                && cycle.cooldownTicksRemaining <= 1)
            {
                rangeCheckedTarget = null;
                if (directAssignedTarget)
                {
                    TrySetKnownTarget(
                        pawn,
                        cycle,
                        verb,
                        assignedTarget,
                        playerForced,
                        killIncappedTarget,
                        closeCombatContext,
                        false,
                        cycle.cooldownTicksRemaining <= 0,
                        ref availability);
                }
                else if (!automaticPromotionAttempted
                    && dedicatedAssignedTarget
                    && cycle.automaticCandidateCollectionClosed)
                {
                    TrySetKnownTarget(
                        pawn,
                        cycle,
                        verb,
                        assignedTarget,
                        false,
                        killIncappedTarget,
                        closeCombatContext,
                        false,
                        cycle.cooldownTicksRemaining <= 0,
                        ref availability);
                }
            }

            // Reuse the unchanged plan within this slot pass.
            if (!focusedTargetControlsCycle
                && cycle.plannedTarget != rangeCheckedTarget
                && InterruptMovingFireOutsideAutomaticRange(
                    pawn,
                    state,
                    cycle,
                    verb,
                    cycle.plannedTarget,
                    requestAutomaticRefill,
                    randomAttackEnabled))
            {
                promotedAutomaticTarget = null;
                return;
            }

            // Promotion can supply a new reservation after the earlier check.
            // Check it at cooldown 1 / aim start, without rechecking an unchanged plan.
            if (cycle.HasPlan
                && !cycle.plannedInterception
                && cycle.plannedTarget != cycle.focusedTarget
                && !(playerForced && cycle.plannedTarget == assignedTarget)
                && cycle.plannedTarget != checkedTarget)
            {
                if (!ValidPlan(
                        pawn, cycle, verb, assignedTarget, playerForced,
                        killIncappedTarget, closeCombatContext, ref availability))
                {
                    HandleInvalidPlanAtExecution(
                        pawn, state, cycle, verb, assignedTarget, playerForced,
                        killIncappedTarget, closeCombatContext, requestAutomaticRefill);
                    promotedAutomaticTarget = null;
                    return;
                }
                checkedTarget = cycle.plannedTarget;
            }

            if (cycle.cooldownTicksRemaining > 0 || blockedByStance)
            {
                return;
            }

            if (cycle.HasPlan && cycle.warmupTicksRemaining < 0)
            {
                if (ordinaryWeaponEnabled && !closeCombatContext
                    && (!newAutomaticRangedAttacksBlocked
                        || focusedTargetControlsCycle))
                {
                    RimKataSharedTargetSearch.Begin(
                        pawn,
                        state,
                        pawn.Position);
                }

                cycle.plannedActionVerb = ResolveCycleActionVerb(
                    pawn,
                    cycle,
                    verb,
                    closeCombatContext);
                if (cycle.plannedActionVerb == null)
                {
                    ClearTargetPreservingCycle(cycle);
                    return;
                }

                int normalWarmup = RimKataCombatMath.WarmupTicksForSingleShot(
                    cycle.plannedActionVerb);
                int openingBonus = ResolveOpeningSupportBonus(
                    pawn,
                    state,
                    cycle);

                int totalWarmup = normalWarmup + openingBonus;
                // Predict once when aiming starts, not from per-tick ValidPlan.
                if (cycle.plannedInterception
                    && !RimKataInterceptionTrajectory.CanIntercept(
                        pawn, cycle.plannedActionVerb,
                        cycle.plannedTarget as Projectile, totalWarmup))
                {
                    ClearTargetPreservingCycle(cycle);
                    return;
                }
                cycle.warmupTotalTicks = totalWarmup;
                cycle.warmupTicksRemaining = totalWarmup;
                if (cycle.warmupTicksRemaining > 0)
                {
                    return;
                }
            }

            if (!ReadyToAct(cycle))
            {
                return;
            }

            // A newly assigned explicit plan may not have passed the earlier
            // automatic-plan check. Every other unchanged plan was checked above.
            if (cycle.plannedTarget != checkedTarget
                && !ValidPlan(
                    pawn, cycle, verb, assignedTarget, playerForced,
                    killIncappedTarget, closeCombatContext, ref availability))
            {
                HandleInvalidPlanAtExecution(
                    pawn, state, cycle, verb, assignedTarget, playerForced,
                    killIncappedTarget, closeCombatContext, allowAutomaticRangedFire);
                return;
            }

            Verb actionVerb = cycle.plannedActionVerb
                ?? ResolveCycleActionVerb(
                    pawn,
                    cycle,
                    verb,
                    closeCombatContext);
            if (actionVerb == null)
            {
                ClearTargetPreservingCycle(cycle);
                return;
            }
            cycle.plannedActionVerb = actionVerb;

            if (!allowAutomaticRangedFire
                && !actionVerb.IsMeleeAttack
                && !playerForced
                && !cycle.focusedTargetFromAttackGizmo)
            {
                return;
            }

            LocalTargetInfo target = TargetInfo(cycle);
            if (!target.IsValid)
            {
                ClearTargetPreservingCycle(cycle);
                return;
            }

            bool firedFromVanillaOpening = cycle.openingWarmupPending;
            if (!firedFromVanillaOpening)
            {
                RimKataVerbUtility.RequestNormalSpeedForCombat(
                    actionVerb,
                    target);
            }
            RimKataNativeAttack attack = cycle.nativeAttack ??= new RimKataNativeAttack();
            attack.pawn = pawn;
            attack.state = state;
            attack.cycle = cycle;
            attack.weapon = cycle.weapon;
            attack.verb = actionVerb;
            attack.cycleVerb = verb;
            attack.job = pawn.CurJob;
            attack.assignedTarget = assignedTarget;
            attack.firedTarget = cycle.plannedTarget;
            attack.target = target;
            attack.playerForced = playerForced;
            attack.killIncappedTarget = killIncappedTarget;
            attack.closeCombatContext = closeCombatContext;
            attack.allowAutomaticRangedFire = allowAutomaticRangedFire;
            attack.randomAttackEnabled = randomAttackEnabled;
            attack.firedFromVanillaOpening = firedFromVanillaOpening;
            attack.movingShot = !actionVerb.IsMeleeAttack && !cycle.plannedCloseAttack
                && pawn.pather?.MovingNow == true;
            attack.closeShot = !actionVerb.IsMeleeAttack && cycle.plannedCloseAttack;
            attack.interceptionShot = cycle.plannedInterception;
            attack.interceptionTarget = cycle.plannedTarget as Projectile;
            attack.closeMeleeResolution = attack.closeShot;
            attack.closeMeleeHit = false;
            attack.closeDefensePrecheck = RimKataCloseDefensePrecheck.None;

            // The actual Verb is executed by its native equipment/body owner.
            // A busy shared physical Verb is retried without discarding this plan.
            if (cycle.weapon != attack.weapon || !attack.Queue())
                attack.ClearCompletedReferences();
            return;
        }

        internal static bool NativeAttackStillAllowed(RimKataNativeAttack attack)
        {
            return !attack.cycle.ResponseCooldownAppliedThisTick
                && !MovementBlocksFire(attack.pawn, attack.state)
                && !ShouldPauseFireForDodge(attack.pawn)
                && !StanceBlocksRimKata(attack.pawn);
        }

        internal static void CompleteNativeAttack(RimKataNativeAttack attack, bool acted, bool cancelled)
        {
            CycleVerbAvailability availability = default;
            FinishCycleAction(attack, acted, cancelled, out Thing promoted, ref availability);
            Pawn pawn = attack.pawn;
            RimKataPawnCombatState state = attack.state;
            if (promoted != null && pawn.CurJob == attack.job)
            {
                TryPromoteAutomaticJobTarget(
                    pawn, state, attack.assignedTarget, attack.playerForced,
                    attack.cycle == state.primaryWeaponCycle ? promoted : null,
                    attack.cycle == state.secondaryWeaponCycle ? promoted : null,
                    attack.randomAttackEnabled, out Thing _);
            }
        }

        private static void FinishCycleAction(
            RimKataNativeAttack attack, bool acted, bool cancelled,
            out Thing promotedAutomaticTarget, ref CycleVerbAvailability availability)
        {
            promotedAutomaticTarget = null;
            Pawn pawn = attack.pawn;
            RimKataPawnCombatState state = attack.state;
            RimKataWeaponCycleState cycle = attack.cycle;
            Verb verb = attack.cycleVerb;
            Verb actionVerb = attack.verb;
            ThingWithComps firedWeapon = attack.weapon;
            Thing assignedTarget = attack.assignedTarget;
            bool playerForced = attack.playerForced;
            bool killIncappedTarget = attack.killIncappedTarget;
            bool closeCombatContext = attack.closeCombatContext;
            bool allowAutomaticRangedFire = attack.allowAutomaticRangedFire;
            bool randomAttackEnabled = attack.randomAttackEnabled;
            bool firedFromVanillaOpening = attack.firedFromVanillaOpening;
            bool requestAutomaticRefill = allowAutomaticRangedFire && !closeCombatContext && randomAttackEnabled;
            if (cycle.weapon != firedWeapon) return;
            // A shot or defense response can change targets, equipment and availability.
            availability = default;
            if (state.weaponBindingsDirty
                || state.weaponConfigurationRevision
                    != RimKataEquipmentUtility.WeaponConfigurationRevision)
            {
                BindCurrentWeapons(pawn, state);
                if (cycle.weapon != firedWeapon)
                {
                    promotedAutomaticTarget = null;
                    return;
                }
            }

            if (!acted)
            {
                ApplyInterruptedBurstCooldown(pawn, cycle, actionVerb);
                cycle.ClearPlan();
                return;
            }

            cycle.StampNativeActionTick();
            cycle.cooldownFromVanillaOpening = firedFromVanillaOpening;

            cycle.firedInCurrentOpening = firedFromVanillaOpening;
            RecordFirstFiredWeapon(state, cycle.weapon);
            bool allowAutomaticContinuation = allowAutomaticRangedFire
                || playerForced
                || closeCombatContext
                || verb.IsMeleeAttack
                || cycle.focusedTargetFromAttackGizmo;

            cycle.openingWarmupBonusTicks = 0;
            cycle.openingWarmupPending = false;

            Thing firedTarget = attack.firedTarget;
            int cooldown = RimKataCombatMath.CooldownTicksForSingleShot(actionVerb, pawn, false);
            cycle.cooldownTicksRemaining = cooldown;
            cycle.lastFiredTarget = firedTarget;
            cycle.visualTarget = firedTarget;
            cycle.visualAimTicksRemaining = cooldown;
            cycle.ClearPlan();

            if (!allowAutomaticContinuation || cancelled || pawn.CurJob != attack.job
                || pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.InMentalState)
            {
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
                return;
            }

            if (state != null)
            {
                if (!closeCombatContext)
                {
                    RimKataSharedTargetSearch.Begin(pawn, state, pawn.Position);
                }
                bool allowAutomaticReselection = !playerForced
                    && (allowAutomaticRangedFire
                        || closeCombatContext
                        || verb.IsMeleeAttack);
                if (allowAutomaticReselection)
                {
                    // Prune a target disabled by this shot; selection checks the
                    // next candidate's shootability without repeating admission.
                    if (!(firedTarget is Projectile)
                        && !RimKataSharedTargetSearch.IsLiveRegisteredCandidate(
                            pawn,
                            firedTarget))
                    {
                        EvictAutomaticCandidate(
                            pawn,
                            state,
                            cycle,
                            firedTarget,
                            requestAutomaticRefill);
                    }

                    TryCacheSharedCandidate(
                        pawn,
                        state,
                        cycle,
                        assignedTarget,
                        randomAttackEnabled,
                        verb,
                        ref availability);
                    if (cycle.cachedCandidateTarget != null
                        && TryPromoteCachedCandidate(
                            pawn,
                            state,
                            cycle,
                            verb,
                            killIncappedTarget,
                            closeCombatContext,
                            requestAutomaticRefill,
                            randomAttackEnabled,
                            out Thing nextAutomaticTarget,
                            ref availability))
                    {
                        promotedAutomaticTarget = nextAutomaticTarget;
                    }
                }
            }

            return;
        }

        private static bool TryPromoteAutomaticJobTarget(
            Pawn pawn,
            RimKataPawnCombatState state,
            Thing assignedTarget,
            bool playerForced,
            Thing primaryCandidate,
            Thing secondaryCandidate,
            bool? randomAttackEnabled,
            out Thing promotedTarget)
        {
            promotedTarget = null;
            // Do not add close-combat exceptions here; the common validity and range
            // transitions own Job-target replacement for every combat context.
            if (pawn?.Map == null
                || state == null
                || playerForced
                || pawn.CurJobDef != RimKataDefOf.RimKata_Attack
                || !(randomAttackEnabled
                    ?? RimKataEligibility.RandomAttackEnabledForPawn(pawn))
                || pawn.CurJob.targetA.Thing != assignedTarget
                || (RimKataTargeting.IsValidAutomaticAttackTarget(
                        pawn,
                        assignedTarget)
                    && TargetWithinAutomaticCandidateCellRadius(
                        pawn,
                        assignedTarget))
                || (primaryCandidate == null && secondaryCandidate == null)
                || !(pawn.jobs?.curDriver is JobDriver_RimKataAttack driver))
            {
                return false;
            }

            bool secondaryOwnsEngagement =
                state.engagementOwnerWeapon != null
                && state.engagementOwnerWeapon
                    == state.secondaryWeaponCycle?.weapon;
            Thing preferredCandidate = secondaryOwnsEngagement
                ? secondaryCandidate
                : primaryCandidate;
            Thing alternateCandidate = secondaryOwnsEngagement
                ? primaryCandidate
                : secondaryCandidate;

            if (TryPromoteAutomaticJobCandidate(
                    pawn,
                    driver,
                    preferredCandidate))
            {
                promotedTarget = preferredCandidate;
                return true;
            }

            if (alternateCandidate != preferredCandidate
                && TryPromoteAutomaticJobCandidate(
                    pawn,
                    driver,
                    alternateCandidate))
            {
                promotedTarget = alternateCandidate;
                return true;
            }

            return false;
        }

        private static bool TryPromoteAutomaticJobCandidate(
            Pawn pawn,
            JobDriver_RimKataAttack driver,
            Thing candidate)
        {
            return candidate != null
                && TargetWithinAutomaticCandidateCellRadius(pawn, candidate)
                && driver.TryPromoteAutomaticJobTarget(candidate);
        }

        private static bool TryPromoteCachedCandidate(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Verb verb,
            bool killIncappedTarget,
            bool closeCombatContext,
            bool requestRefill,
            bool automaticRangeRequired,
            out Thing promotedAutomaticTarget,
            ref CycleVerbAvailability availability)
        {
            promotedAutomaticTarget = null;
            Thing cachedTarget = cycle?.cachedCandidateTarget;
            bool cachedInterception =
                cycle?.cachedCandidateInterception == true;
            if (cycle == null || cachedTarget == null || cycle.ResponseCooldownAppliedThisTick)
            {
                return false;
            }

            cycle.cachedCandidateTarget = null;
            cycle.cachedCandidateInterception = false;
            bool promoted;
            if (cachedInterception)
            {
                promoted = CanAssignInterceptionTarget(
                    pawn,
                    cycle,
                    verb,
                    cachedTarget);
            }
            else if (automaticRangeRequired)
            {
                // Selection already checked this slot's registered candidate.
                // Reservation handoff must not run admission or shootability again.
                bool closeAttack = verb.IsMeleeAttack || closeCombatContext;
                SetCandidate(cycle, cachedTarget, false, closeAttack, closeAttack, false);
                promoted = true;
            }
            else
            {
                // Random-fire OFF can supply a preferred target without candidate admission.
                promoted = TrySetKnownTarget(
                    pawn,
                    cycle,
                    verb,
                    cachedTarget,
                    false,
                    killIncappedTarget,
                    closeCombatContext,
                    automaticRangeRequired,
                    false,
                    ref availability);
            }
            if (promoted
                && !cachedInterception
                && automaticRangeRequired
                && cycle.ContainsAutomaticCandidate(cachedTarget))
            {
                promotedAutomaticTarget = cachedTarget;
            }
            if (cachedInterception && promoted)
            {
                SetCandidate(
                    cycle,
                    cachedTarget,
                    true,
                    false,
                    false,
                    false);
            }
            else if (!promoted)
            {
                if (!(cachedTarget is Projectile))
                {
                    EvictAutomaticCandidate(
                        pawn,
                        state,
                        cycle,
                        cachedTarget,
                        requestRefill);
                }
                ClearTargetPreservingCycle(cycle);
            }

            return promoted;
        }

        private static bool EvictAutomaticCandidate(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Thing target,
            bool requestRefill)
        {
            return RimKataSharedTargetSearch.EvictAutomaticCandidate(
                pawn,
                state,
                cycle,
                target,
                requestRefill);
        }

        private static int ResolveOpeningSupportBonus(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle)
        {
            if (state == null || cycle?.weapon == null)
            {
                return 0;
            }

            if (cycle.openingWarmupPending)
            {
                return Mathf.Max(0, cycle.openingWarmupBonusTicks);
            }

            if (cycle.openingSupportDelayConsumed)
            {
                return 0;
            }

            if (state.engagementOwnerWeapon == null)
            {
                state.engagementOwnerWeapon = cycle.weapon;
                return 0;
            }

            if (state.engagementOwnerWeapon == cycle.weapon)
            {
                return 0;
            }

            RimKataWeaponCycleState ownerCycle = CycleForWeapon(
                state,
                state.engagementOwnerWeapon);
            Verb ownerVerb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                ownerCycle?.weapon);
            if (ownerVerb == null)
            {
                return 0;
            }

            int bonus = Mathf.Max(
                1,
                Mathf.CeilToInt(
                    RimKataCombatMath.CooldownTicksForSingleShot(
                        ownerVerb,
                        pawn,
                        false) * 0.5f));
            cycle.openingWarmupBonusTicks = bonus;
            cycle.openingWarmupPending = true;
            cycle.openingSupportDelayConsumed = true;
            return bonus;
        }

        private static void RearmOpeningOwnerIfBothWaiting(
            RimKataPawnCombatState state)
        {
            if (state?.primaryWeaponCycle?.DebugState != 'W'
                || state.secondaryWeaponCycle?.DebugState != 'W')
            {
                return;
            }

            state.engagementOwnerWeapon = null;
            state.primaryWeaponCycle.openingWarmupBonusTicks = 0;
            state.primaryWeaponCycle.openingWarmupPending = false;
            state.primaryWeaponCycle.openingSupportDelayConsumed = false;
            state.secondaryWeaponCycle.openingWarmupBonusTicks = 0;
            state.secondaryWeaponCycle.openingWarmupPending = false;
            state.secondaryWeaponCycle.openingSupportDelayConsumed = false;
        }

        private static bool PrepareFocusedTarget(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            bool closeCombatContext,
            ref CycleVerbAvailability availability)
        {
            Thing target = cycle?.focusedTarget;
            if (target == null)
            {
                return false;
            }

            if (PermanentlyInvalidCycleTarget(
                pawn,
                target,
                target,
                true,
                false,
                false))
            {
                cycle.focusedTarget = null;
                cycle.focusedTargetFromAttackGizmo = false;
                return false;
            }

            if (!FocusedTargetUsableNow(
                pawn,
                cycle,
                verb,
                closeCombatContext,
                ref availability))
            {
                if (cycle.HasPlan && cycle.plannedTarget == target)
                {
                    ClearTargetPreservingCycle(cycle);
                }
                return false;
            }

            cycle.cachedCandidateTarget = null;
            cycle.cachedCandidateInterception = false;

            if (cycle.HasPlan && cycle.plannedTarget != target)
            {
                ClearTargetPreservingCycle(cycle);
            }

            if (!cycle.HasPlan && cycle.cooldownTicksRemaining <= 1)
            {
                TrySetKnownTarget(
                    pawn,
                    cycle,
                    verb,
                    target,
                    true,
                    false,
                    closeCombatContext,
                    false,
                    true,
                    ref availability);
            }

            if (!cycle.HasPlan)
            {
                cycle.visualTarget = target;
                cycle.visualAimTicksRemaining = Mathf.Max(
                    cycle.visualAimTicksRemaining,
                    2);
            }

            return true;
        }

        private static bool FocusedTargetUsableNow(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            bool closeCombatContext)
        {
            CycleVerbAvailability availability = default;
            return FocusedTargetUsableNow(
                pawn, cycle, verb, closeCombatContext, ref availability);
        }

        private static bool FocusedTargetUsableNow(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            bool closeCombatContext,
            ref CycleVerbAvailability availability)
        {
            return cycle?.focusedTarget != null
                && ValidCurrentTargetForVerb(
                    pawn,
                    verb,
                    cycle.focusedTarget,
                    true,
                    false,
                    closeCombatContext,
                    ref availability);
        }

        private static void PromoteApproachingShotToCloseContext(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            bool closeCombatContext)
        {            
            if (!closeCombatContext
                || verb == null
                || verb.IsMeleeAttack
                || cycle == null
                || !cycle.HasPlan
                || cycle.plannedInterception
                || cycle.plannedCloseContext
                || !pawn.CanReachImmediate(cycle.plannedTarget, PathEndMode.Touch))
            {
                return;
            }

            cycle.plannedCloseContext = true;
            cycle.plannedCloseAttack = true;
        }

        private static void SetCandidate(
            RimKataWeaponCycleState cycle,
            Thing target,
            bool interception,
            bool closeAttack,
            bool closeContext,
            bool updateVisualTarget)
        {
            cycle.plannedTarget = target;
            cycle.plannedInterception = interception;
            cycle.plannedCloseAttack = closeAttack;
            cycle.plannedCloseContext = closeContext;
            cycle.plannedActionVerb = null;
            if (updateVisualTarget)
            {
                cycle.visualTarget = target;
            }
        }
                
        private static bool TrySetKnownTarget(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool closeCombatContext,
            bool automaticRangeRequired,
            bool updateVisualTarget,
            ref CycleVerbAvailability availability)
        {
            if (pawn?.Map == null
                || cycle == null
                || verb == null
                || assignedTarget == null
                || assignedTarget.Destroyed
                || !assignedTarget.Spawned
                || assignedTarget.Map != pawn.Map
                || (!playerForced
                    && !RimKataTargeting.IsAutomaticEnemy(
                        pawn,
                        assignedTarget)))
            {
                return false;
            }

            if (assignedTarget is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    playerForced && killIncappedTarget))
            {
                return false;
            }

            if (verb.IsMeleeAttack || closeCombatContext)
            {
                if (!CanHitTargetForCombatContext(
                        pawn,
                        verb,
                        assignedTarget,
                        closeCombatContext,
                        ref availability))
                {
                    return false;
                }

                SetCandidate(cycle, assignedTarget, false, true, true, updateVisualTarget);
                return true;
            }

            if (automaticRangeRequired)
            {
                if (!RimKataSharedTargetSearch.IsValidForVerb(
                    pawn,
                    verb,
                    assignedTarget))
                {
                    return false;
                }
            }

            if (!CanHitTargetForCombatContext(
                    pawn, verb, assignedTarget, false, ref availability))
            {
                return false;
            }

            SetCandidate(cycle, assignedTarget, false, false, false, updateVisualTarget);
            return true;
        }

        private static bool CanAssignInterceptionTarget(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing target)
        {
            if (pawn?.Map == null
                || verb == null
                || cycle == null
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || FocusedTargetUsableNow(
                    pawn,
                    cycle,
                    verb,
                    false)
                || cycle.cachedCandidateTarget != null
                || cycle.HasPlan
                || cycle.openingWarmupPending
                || cycle.NativeAttackPending)
            {
                return false;
            }

            if (verb.IsMeleeAttack)
            {
                return pawn.CanReachImmediate(target, PathEndMode.Touch);
            }

            if (target is Projectile)
            {
                return RimKataSharedTargetSearch.IsValidForVerb(
                    pawn,
                    verb,
                    target);
            }

            float candidateCellRadius =
                RimKataRangeUtility.ResolveCandidateCellRadius(
                    pawn,
                    cycle.weapon,
                    verb);
            if (pawn.Position.DistanceToSquared(target.Position)
                > candidateCellRadius * candidateCellRadius)
            {
                return false;
            }

            if (verb.CanHitTarget(target))
            {
                return true;
            }

            return pawn.CanReachImmediate(target, PathEndMode.Touch)
                && VerbUsable(pawn, verb, true);
        }

        private static bool ValidPlan(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool closeCombatContext,
            ref CycleVerbAvailability availability)
        {
            Thing target = cycle.plannedTarget;
            if (target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map)
            {
                return false;
            }

            if (cycle.plannedInterception)
            {
                return target is Projectile
                    && RimKataSharedTargetSearch.IsValidForVerb(
                        pawn,
                        verb,
                        target);
            }

            bool explicitTarget = target == cycle.focusedTarget
                || (playerForced && target == assignedTarget);
            if (explicitTarget
                && !RimKataEquipmentUtility.IsWeaponEnabled(cycle.weapon?.def))
            {
                return false;
            }

            if (!verb.IsMeleeAttack && cycle.plannedCloseContext != closeCombatContext)
            {
                return false;
            }

            if (target is Pawn targetPawn
                && (explicitTarget
                    ? !RimKataTargeting.IsPawnTargetStateValid(
                        targetPawn,
                        playerForced && killIncappedTarget && target == assignedTarget)
                    : targetPawn.Dead
                        || RimKataTargeting.IsIncapacitatedTarget(targetPawn)))
            {
                return false;
            }

            // Automatic plans retain their admitted target identity. Only current
            // shot feasibility belongs here; candidate admission owns hostility/fog.
            return CanHitTargetForCombatContext(
                pawn,
                verb,
                target,
                cycle.plannedCloseAttack,
                ref availability);
        }

        private static bool PermanentlyInvalidCycleTarget(
            Pawn pawn,
            Thing target,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool interception)
        {
            if (pawn?.Map == null
                || target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map)
            {
                return true;
            }

            if (interception)
            {
                return !(target is Projectile projectile)
                    || !RimKataTargeting.IsInterceptionTargetActive(
                        projectile);
            }

            bool forcedAssignedTarget = playerForced && target == assignedTarget;
            if (!RimKataTargeting.IsAutomaticEnemy(pawn, target)
                && !forcedAssignedTarget)
            {
                return true;
            }

            return target is Pawn targetPawn
                && !RimKataTargeting.IsPawnTargetStateValid(
                    targetPawn,
                    forcedAssignedTarget && killIncappedTarget);
        }

        internal static bool VerbUsable(Pawn pawn, Verb verb, bool closeCombatContext)
        {
            if (verb.IsMeleeAttack)
            {
                return verb.Available();
            }

            if (UsesPhysicalMeleeAction(pawn, verb, closeCombatContext))
            {
                return pawn?.kindDef?.canMeleeAttack == true
                    && pawn.meleeVerbs != null;
            }

            if (closeCombatContext)
            {
                return RimKataEligibility.IsRangedVerbAvailableInCloseCombat(pawn, verb);
            }

            return !verb.ApparelPreventsShooting() && verb.Available();
        }

        private static bool UsesPhysicalMeleeAction(
            Pawn pawn,
            Verb slotVerb,
            bool closeCombatContext)
        {
            return closeCombatContext
                && slotVerb != null
                && !slotVerb.IsMeleeAttack
                && (RimKataTargetAccess.SettingsFor(pawn)?.closeFireEnabled == false
                    || (pawn?.Drafted == true && pawn.drafter?.FireAtWill == false));
        }

        private static Verb ResolveCycleActionVerb(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb slotVerb,
            bool closeCombatContext)
        {
            if (!UsesPhysicalMeleeAction(
                    pawn, slotVerb, closeCombatContext && cycle?.plannedCloseAttack == true))
            {
                return slotVerb;
            }

            Thing target = cycle?.plannedTarget;
            return target != null
                ? pawn?.meleeVerbs?.TryGetMeleeVerb(target)
                : null;
        }

        private static bool ReadyToAct(RimKataWeaponCycleState cycle)
        {
            return cycle?.weapon != null
                && cycle.cooldownTicksRemaining <= 0
                && !cycle.NativeAttackPending
                && cycle.HasPlan
                && cycle.warmupTicksRemaining == 0
                && !cycle.ResponseCooldownAppliedThisTick;
        }

        private static void HandleInvalidPlanAtExecution(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing assignedTarget,
            bool playerForced,
            bool killIncappedTarget,
            bool closeCombatContext,
            bool allowAutomaticRangedFire)
        {
            Thing invalidTarget = cycle.plannedTarget ?? assignedTarget;
            ApplyInterruptedBurstCooldown(pawn, cycle, verb);
            ClearTargetPreservingCycle(cycle);

            bool explicitTarget = invalidTarget == cycle.focusedTarget
                || (playerForced && invalidTarget == assignedTarget);
            if (!explicitTarget
                && invalidTarget != null
                && !(invalidTarget is Projectile))
            {
                EvictAutomaticCandidate(
                    pawn,
                    state,
                    cycle,
                    invalidTarget,
                    allowAutomaticRangedFire && !closeCombatContext);
            }
        }

        private static void ApplyInterruptedBurstCooldown(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb)
        {
            if (cycle?.NativeAttackPending == true && cycle.nativeAttack.HasFired
                && pawn != null && verb != null)
            {
                cycle.cooldownTicksRemaining = Mathf.Max(cycle.cooldownTicksRemaining, RimKataCombatMath.CooldownTicksForSingleShot(verb, pawn, false));
            }
        }

        private static bool IsVanillaAutomaticWeaponBusy(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle)
        {
            if (cycle.weapon == null)
            {
                return false;
            }

            // Native warmup and final cooldown keep the Verb idle. The stance
            // still owns this weapon, even after its non-Pawn target is destroyed.
            if (pawn.stances?.curStance is Stance_Busy busy
                && !(busy is Stance_RimKataAim)
                && busy.verb?.EquipmentSource == cycle.weapon
                && UsesVanillaAutomaticTarget(pawn, busy.focusTarg.Thing, state))
            {
                return true;
            }

            Verb verb = cycle.boundVerb;
            return verb?.state == VerbState.Bursting
                && UsesVanillaAutomaticTarget(pawn, verb.CurrentTarget.Thing, state);
        }

        private static bool StanceBlocksRimKata(Pawn pawn)
        {
            return pawn?.stances?.stunner?.Stunned == true;
        }

        private static bool MovementBlocksFire(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            if (RimKataDodgeMovementUtility.CalculateIsActive(
                    pawn,
                    state))
            {
                return false;
            }

            return RimKataTargetAccess.SettingsFor(pawn)?.movingFireEnabled == false && pawn?.pather?.MovingNow == true;
        }

        private static bool InterruptMovingFireOutsideAutomaticRange(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing target,
            bool requestRefill,
            bool? randomAttackEnabled = null)
        {
            if (pawn?.pather?.MovingNow != true
                || verb == null
                || verb.IsMeleeAttack
                || cycle == null
                || target == null
                || TargetWithinAutomaticCandidateCellRadius(pawn, target))
            {
                return false;
            }

            if (pawn.CurJobDef == RimKataDefOf.RimKata_Attack
                && pawn.CurJob.targetA.Thing == target
                && verb.CanHitTarget(target))
            {
                return false;
            }

            if (!(randomAttackEnabled
                    ?? RimKataEligibility.RandomAttackEnabledForPawn(pawn))
                && !(target is Projectile)
                && verb.CanHitTarget(target))
            {
                return false;
            }

            ApplyInterruptedBurstCooldown(pawn, cycle, verb);
            bool focusedTargetUsable = FocusedTargetUsableNow(
                pawn,
                cycle,
                verb,
                false);
            ClearTargetPreservingCycle(cycle);
            if (!focusedTargetUsable && !(target is Projectile))
            {
                EvictAutomaticCandidate(
                    pawn,
                    state,
                    cycle,
                    target,
                    requestRefill);
            }

            if (cycle.cachedCandidateTarget != null
                && !TargetWithinAutomaticCandidateCellRadius(
                    pawn,
                    cycle.cachedCandidateTarget))
            {
                Thing invalidCachedTarget = cycle.cachedCandidateTarget;
                EvictAutomaticCandidate(
                    pawn,
                    state,
                    cycle,
                    invalidCachedTarget,
                    requestRefill);
                cycle.cachedCandidateTarget = null;
                cycle.cachedCandidateInterception = false;
            }

            if (pawn.stances?.curStance is Stance_RimKataAim aim
                && aim.verb == verb)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }

            return true;
        }

        private static void InterruptCycleForMovement(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null || !cycle.HasPlan)
            {
                return;
            }

            Verb verb = RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                cycle.weapon);
            ApplyInterruptedBurstCooldown(pawn, cycle, verb);
            if (cycle.openingWarmupPending
                && !cycle.firedInCurrentOpening)
            {
                ResetUnfiredOpeningTimer(cycle);
            }
            else
            {
                cycle.ClearPlan();
            }
        }

        private static LocalTargetInfo TargetInfo(RimKataWeaponCycleState cycle)
        {
            if (cycle.plannedInterception && cycle.plannedTarget is Projectile projectile)
            {
                return new LocalTargetInfo(projectile);
            }

            return cycle.plannedTarget != null
                ? new LocalTargetInfo(cycle.plannedTarget)
                : LocalTargetInfo.Invalid;
        }

        internal static void UpdateBodyAimStance(Pawn pawn, RimKataPawnCombatState state)
        {
            if ((state.responsePoseLookAtFocus
                    && state.TryGetLiveResponsePoseFocus(
                        out LocalTargetInfo _))
                || StanceBlocksRimKata(pawn))
            {
                return;
            }

            if (!TryGetNextAim(
                    pawn,
                    state,
                    out RimKataWeaponCycleState aimCycle,
                    out LocalTargetInfo target))
            {
                ReconcileRimKataAim(
                    pawn,
                    LocalTargetInfo.Invalid,
                    null);
                return;
            }

            Verb slotVerb = CombatVerbForAim(pawn, state, aimCycle);
            bool physicalMeleeAction = UsesPhysicalMeleeAction(
                pawn,
                slotVerb,
                state?.dualCloseCombatActive == true && aimCycle?.plannedCloseAttack == true);
            Verb verb = aimCycle?.plannedActionVerb ?? slotVerb;
            if (physicalMeleeAction)
            {
                if (verb?.IsMeleeAttack != true
                    && pawn.stances?.curStance is Stance_RimKataAim currentAim
                    && currentAim.verb?.IsMeleeAttack == true
                    && currentAim.focusTarg.Equals(target))
                {
                    verb = currentAim.verb;
                }

                if (verb?.IsMeleeAttack != true && target.HasThing)
                {
                    verb = pawn.meleeVerbs?.TryGetMeleeVerb(target.Thing);
                }

                if (verb == null)
                {
                    ReconcileRimKataAim(pawn, target, null);
                    if (target.IsValid
                        && target.Cell.IsValid
                        && target.Cell != pawn.Position)
                    {
                        pawn.rotationTracker.FaceCell(target.Cell);
                    }

                    return;
                }
            }

            if (verb == null)
            {
                ReconcileRimKataAim(pawn, target, null);
                return;
            }

            if (ReconcileRimKataAim(pawn, target, verb))
            {
                return;
            }

            if (pawn.stances.curStance is Stance_Busy
                && !(pawn.stances.curStance is Stance_RimKataAim))
            {
                return;
            }

            Stance_RimKataAim aim = new Stance_RimKataAim(2, target, verb);
            pawn.stances.SetStance(aim);
            aim.RefreshLeanNow();
        }

        private static bool ReconcileRimKataAim(
            Pawn pawn,
            LocalTargetInfo target,
            Verb verb)
        {
            if (!(pawn?.stances?.curStance is Stance_RimKataAim current))
            {
                return false;
            }

            if (verb != null
                && target.IsValid
                && current.verb == verb
                && current.focusTarg.Equals(target))
            {
                current.ticksLeft = Mathf.Max(current.ticksLeft, 2);
                current.RefreshLeanNow();
                return true;
            }

            if (verb == null || !target.IsValid)
            {
                pawn.stances.SetStance(new Stance_Mobile());
            }
            return false;
        }

        private static RimKataWeaponCycleState ChooseBodyAimCycle(
            Pawn pawn,
            RimKataPawnCombatState state)
        {
            RimKataWeaponCycleState primary = state.primaryWeaponCycle;
            RimKataWeaponCycleState secondary = state.secondaryWeaponCycle;
            bool primaryHasAim = IsLiveVisualTarget(pawn, primary?.plannedTarget)
                || ((primary?.HasPlan == true
                        || primary?.cooldownTicksRemaining > 0
                        || primary?.visualAimTicksRemaining > 0)
                    && IsLiveVisualTarget(pawn, primary?.visualTarget));
            bool secondaryHasAim = IsLiveVisualTarget(pawn, secondary?.plannedTarget)
                || ((secondary?.HasPlan == true
                        || secondary?.cooldownTicksRemaining > 0
                        || secondary?.visualAimTicksRemaining > 0)
                    && IsLiveVisualTarget(pawn, secondary?.visualTarget));
            if (!secondaryHasAim)
            {
                return primaryHasAim ? primary : null;
            }

            if (!primaryHasAim)
            {
                return secondary;
            }

            int primaryEta = Mathf.Max(0, primary.cooldownTicksRemaining) + Mathf.Max(0, primary.warmupTicksRemaining);
            int secondaryEta = Mathf.Max(0, secondary.cooldownTicksRemaining) + Mathf.Max(0, secondary.warmupTicksRemaining);
            Verb primaryPendingVerb = primary.warmupTicksRemaining < 0
                ? CombatVerbForAim(pawn, state, primary) : null;
            Verb secondaryPendingVerb = secondary.warmupTicksRemaining < 0
                ? CombatVerbForAim(pawn, state, secondary) : null;
            bool shareAimingFactor = primaryPendingVerb?.CasterPawn == pawn
                && secondaryPendingVerb?.CasterPawn == pawn;
            // Both estimates belong to this one pose decision. Read the live
            // pawn stat once; actual aim start still resolves its current timing.
            float aimingFactor = shareAimingFactor
                ? pawn.GetStatValue(StatDefOf.AimingDelayFactor) : 1f;
            if (primary.warmupTicksRemaining < 0)
            {
                primaryEta += shareAimingFactor
                    ? RimKataCombatMath.WarmupTicksForSingleShotWithAimingFactor(
                        primaryPendingVerb, aimingFactor)
                    : RimKataCombatMath.WarmupTicksForSingleShot(primaryPendingVerb);
            }

            if (secondary.warmupTicksRemaining < 0)
            {
                secondaryEta += shareAimingFactor
                    ? RimKataCombatMath.WarmupTicksForSingleShotWithAimingFactor(
                        secondaryPendingVerb, aimingFactor)
                    : RimKataCombatMath.WarmupTicksForSingleShot(secondaryPendingVerb);
            }

            return primaryEta <= secondaryEta ? primary : secondary;
        }

        private static Verb CombatVerbForAim(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle)
        {
            // Aim readers reuse resolved bindings without mutating slot ownership.
            if (!state.weaponBindingsDirty
                && state.weaponConfigurationRevision
                    == RimKataEquipmentUtility.WeaponConfigurationRevision
                && cycle.boundVerb != null)
            {
                return cycle.boundVerb;
            }

            return RimKataWeaponSlotUtility.CombatVerb(pawn, cycle.weapon);
        }

        private static RimKataWeaponCycleState CycleForWeapon(
            RimKataPawnCombatState state,
            ThingWithComps weapon)
        {
            if (state == null || weapon == null)
            {
                return null;
            }

            if (state.primaryWeaponCycle?.weapon == weapon)
            {
                return state.primaryWeaponCycle;
            }

            return state.secondaryWeaponCycle?.weapon == weapon
                ? state.secondaryWeaponCycle
                : null;
        }

        private static RimKataPawnCombatState StateFor(Pawn pawn, bool create)
        {
            return pawn?.Map?.GetComponent<RimKataMapComponent>()?.GetState(pawn, create);
        }

    }

    [HarmonyPatch(
        typeof(Verb),
        nameof(Verb.TryStartCastOn),
        new Type[]
        {
            typeof(LocalTargetInfo),
            typeof(LocalTargetInfo),
            typeof(bool),
            typeof(bool),
            typeof(bool),
            typeof(bool)
        })]
    public static class Patch_Verb_TryStartCastOn_RimKataOpening
    {
        public static bool Prefix(
            Verb __instance,
            LocalTargetInfo __0,
            ref bool __result,
            out RimKataVanillaOpeningAttempt __state)
        {
            __state = default(RimKataVanillaOpeningAttempt);
            if (RimKataNativeAttack.WaitingForNativeTick(__instance))
            {
                __result = false;
                return false;
            }
            if (RimKataDualWeaponController.ShouldSuppressVanillaCast(
                __instance?.CasterPawn,
                __instance,
                __0))
            {
                __result = false;
                return false;
            }

            __state = RimKataDualWeaponController.PrepareVanillaOpening(
                __instance?.CasterPawn,
                __instance,
                __0);
            RimKataDualWeaponController.PrepareVanillaShotData(
                __instance?.CasterPawn,
                __instance,
                __0,
                ref __state);
            return true;
        }

        public static void Postfix(
            Verb __instance,
            bool __result,
            RimKataVanillaOpeningAttempt __state)
        {
            try
            {
                if (__result)
                {
                    if (__state.prepared)
                    {
                        RimKataDualWeaponController.CommitVanillaOpening(
                            __instance?.CasterPawn,
                            __instance,
                            __state);
                    }
                }
            }
            finally
            {
                RimKataDualWeaponController.FinishVanillaOpeningAttempt(
                    __instance);
            }
        }

        public static Exception Finalizer(
            Verb __instance,
            Exception __exception)
        {
            RimKataDualWeaponController.FinishVanillaOpeningAttempt(
                __instance);
            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(Pawn_DraftController),
        nameof(Pawn_DraftController.Drafted),
        MethodType.Setter)]
    public static class Patch_PawnDraftController_RimKataCycleReset
    {
        public static void Prefix(Pawn_DraftController __instance, out bool __state)
        {
            __state = __instance?.Drafted == true;
        }

        public static void Postfix(
            Pawn_DraftController __instance,
            bool __0,
            bool __state)
        {
            if (__state != __0)
            {
                RimKataDualWeaponController.NotifyDraftStatusChanged(__instance?.pawn);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.SetStance))]
    public static class Patch_PawnStanceTracker_RimKataCooldown
    {
        public static bool Prefix(
            Stance newStance,
            Pawn ___pawn)
        {
            if (!(newStance is Stance_Cooldown cooldown))
            {
                return true;
            }

            if (cooldown.verb == null)
            {
                return true;
            }

            if (cooldown.verb.IsMeleeAttack
                && RimKataDualWeaponController.TryTakeVanillaMeleeCooldown(
                    ___pawn,
                    cooldown.verb,
                    cooldown.focusTarg))
            {
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Stance_Warmup), "Expire")]
    public static class Patch_StanceWarmup_RimKataPendingFollowupAim
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Stance_Warmup __instance)
        {
            RimKataDualWeaponController.RefreshPendingDedicatedFollowupAim(
                __instance?.stanceTracker?.pawn,
                __instance?.verb,
                __instance?.focusTarg ?? LocalTargetInfo.Invalid);
        }
    }

    [HarmonyPatch(typeof(Stance_Busy), "Expire")]
    public static class Patch_StanceCooldown_RimKataPendingFollowupAim
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Stance_Busy __instance)
        {
            if (__instance?.GetType() != typeof(Stance_Cooldown))
            {
                return;
            }

            RimKataDualWeaponController.RefreshPendingDedicatedFollowupAim(
                __instance.stanceTracker?.pawn,
                __instance.verb,
                __instance.focusTarg);
        }
    }
}
