using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed class RimKataSharedTargetSearchState : IExposable
    {
        public bool sessionActive;
        public bool scanActive;
        public int maximumRing;
        public int completedRing;
        public float maximumCandidateCellRadius;
        public IntVec3 origin = IntVec3.Invalid;
        public int lastAdvancedTick = -1;
        internal RimKataRingSearchRuntime ringRuntime;
        internal readonly HashSet<int> neutralPawnIds = new HashSet<int>();
        private Map neutralMap;
        private RimKataNeutralTargetInvalidation.Version neutralVersion;
        private int neutralMapRevision;
        private int neutralGlobalRevision;

        public bool KeepsCombatAlive => scanActive || ringRuntime?.HasPending == true;

        public void ExposeData()
        {
            Scribe_Values.Look(ref sessionActive, "sessionActive");
            Scribe_Values.Look(ref scanActive, "scanActive");
            Scribe_Values.Look(ref maximumRing, "maximumRing");
            Scribe_Values.Look(ref completedRing, "completedRing");
            Scribe_Values.Look(
                ref maximumCandidateCellRadius,
                "effectiveMaximumRange");
            Scribe_Values.Look(ref origin, "origin", IntVec3.Invalid);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                maximumRing = Mathf.Max(0, maximumRing);
                completedRing = Mathf.Max(0, completedRing);
                maximumCandidateCellRadius = Mathf.Max(
                    0f,
                    maximumCandidateCellRadius);
                lastAdvancedTick = -1;
                ClearRingRuntime();
                ClearNeutralTargets();
                if (scanActive)
                {
                    sessionActive = true;
                    // Buffered rings are transient. Revisit the geometry after
                    // loading; registered pawn IDs still suppress duplicates.
                    completedRing = 0;
                }
            }
        }

        public void Reset()
        {
            ClearRingRuntime();
            ClearNeutralTargets();
            sessionActive = false;
            scanActive = false;
            maximumRing = 0;
            completedRing = 0;
            maximumCandidateCellRadius = 0f;
            origin = IntVec3.Invalid;
            lastAdvancedTick = -1;
        }

        internal void ClearRingRuntime()
        {
            ringRuntime?.Clear();
        }

        internal void RefreshNeutralTargets(Map map)
        {
            if (neutralMap != map || neutralVersion == null)
            {
                neutralMap = map;
                neutralVersion = RimKataNeutralTargetInvalidation.ForMap(map);
                neutralPawnIds.Clear();
                ringRuntime?.discoveredIds.Clear();
                neutralMapRevision = neutralVersion.revision;
                neutralGlobalRevision = RimKataNeutralTargetInvalidation.GlobalRevision;
            }
            else if (neutralMapRevision != neutralVersion.revision
                || neutralGlobalRevision != RimKataNeutralTargetInvalidation.GlobalRevision)
            {
                neutralPawnIds.Clear();
                // A formerly neutral pawn must not remain hidden by this scan's
                // seen-ID set. Candidates, pending draws and geometry stay intact.
                ringRuntime?.discoveredIds.Clear();
                neutralMapRevision = neutralVersion.revision;
                neutralGlobalRevision = RimKataNeutralTargetInvalidation.GlobalRevision;
            }
        }

        private void ClearNeutralTargets()
        {
            neutralPawnIds.Clear();
            neutralMap = null;
            neutralVersion = null;
        }
    }

    internal sealed class RimKataRingCandidateDraw
    {
        internal readonly List<int> remaining = new List<int>();

        internal int Draw()
        {
            int index = Rand.Range(0, remaining.Count);
            int result = remaining[index];
            int last = remaining.Count - 1;
            remaining[index] = remaining[last];
            remaining.RemoveAt(last);
            return result;
        }

        internal void Clear()
        {
            remaining.Clear();
        }
    }

    internal sealed class RimKataRingCandidateBatch
    {
        internal readonly List<Pawn> targets = new List<Pawn>();
        internal readonly RimKataRingCandidateDraw primary = new RimKataRingCandidateDraw();
        internal readonly RimKataRingCandidateDraw secondary = new RimKataRingCandidateDraw();
        internal IntVec3 center;
        internal int ring;
        internal bool dormantMovement;
        internal bool sealedForDrawing;

        internal bool HasPending => primary.remaining.Count != 0
            || secondary.remaining.Count != 0;

        internal void Clear()
        {
            targets.Clear();
            primary.Clear();
            secondary.Clear();
            ring = 0;
            center = IntVec3.Invalid;
            dormantMovement = false;
            sealedForDrawing = false;
        }
    }

    internal sealed class RimKataRingSearchRuntime
    {
        internal Map map;
        internal readonly RimKataRingTraversal traversal = new RimKataRingTraversal();
        internal readonly List<Pawn> discovered = new List<Pawn>();
        internal readonly HashSet<int> discoveredIds = new HashSet<int>();
        internal readonly HashSet<int> primaryPendingIds = new HashSet<int>();
        internal readonly HashSet<int> secondaryPendingIds = new HashSet<int>();
        internal readonly List<RimKataRingCandidateBatch> batches =
            new List<RimKataRingCandidateBatch>();
        private readonly Stack<RimKataRingCandidateBatch> unusedBatches =
            new Stack<RimKataRingCandidateBatch>();
        internal IntVec3 center;
        internal int ring;
        internal int cellBudget;
        internal bool collectionComplete;
        internal IntVec3 incomingOrigin = IntVec3.Invalid;
        internal int incomingMaximumRing;

        internal bool HasPending => batches.Count != 0;

        internal RimKataRingCandidateBatch CreateBatch(int batchRing, IntVec3 batchCenter, bool dormant)
        {
            RimKataRingCandidateBatch batch = unusedBatches.Count != 0
                ? unusedBatches.Pop()
                : new RimKataRingCandidateBatch();
            batch.ring = batchRing;
            batch.center = batchCenter;
            batch.dormantMovement = dormant;
            batches.Add(batch);
            return batch;
        }

        internal void RemoveBatchAt(int index)
        {
            RimKataRingCandidateBatch batch = batches[index];
            ReleaseDraw(batch, batch.primary, primaryPendingIds);
            ReleaseDraw(batch, batch.secondary, secondaryPendingIds);
            batches.RemoveAt(index);
            batch.Clear();
            unusedBatches.Push(batch);
        }

        internal static void ReleaseDraw(
            RimKataRingCandidateBatch batch,
            RimKataRingCandidateDraw draw,
            HashSet<int> pendingIds)
        {
            for (int i = 0; i < draw.remaining.Count; i++)
            {
                pendingIds.Remove(batch.targets[draw.remaining[i]].thingIDNumber);
            }
            draw.Clear();
        }

        internal void ClearRing()
        {
            discovered.Clear();
            traversal.Clear();
            ring = 0;
            collectionComplete = false;
        }

        internal void Clear()
        {
            ClearRing();
            for (int i = batches.Count - 1; i >= 0; i--)
            {
                RemoveBatchAt(i);
            }
            discoveredIds.Clear();
            primaryPendingIds.Clear();
            secondaryPendingIds.Clear();
            incomingOrigin = IntVec3.Invalid;
            incomingMaximumRing = 0;
            map = null;
        }
    }

    internal static class RimKataSharedTargetSearch
    {
        private const float CandidateCellRadiusPadding =
            RimKataRangeUtility.CandidateCellRadiusPadding;
        private const float RadiusEpsilon = 0.001f;
        // Deliberately covers the center cell and all eight adjacent cells.
        private const float CloseCombatRangedCandidateCellRadius = 1.7f;

        private static readonly List<Thing> EligibleCandidates =
            new List<Thing>();

        private struct RingCandidateSlot
        {
            internal RimKataWeaponCycleState cycle;
            private Verb verb;
            private bool verbResolved;
            private float configuredRadius;
            internal float configuredRadiusSquared;
            private bool radiusResolved;
            private bool newAdmissionChecked;
            private bool newAdmissionAllowed;

            internal Verb ResolveVerb(Pawn pawn)
            {
                if (!verbResolved)
                {
                    verb = CombatVerbForCycle(pawn, cycle);
                    verbResolved = true;
                }
                return verb;
            }

            internal float ResolveConfiguredRadius(
                Pawn pawn,
                RimKataPawnCombatState combatState)
            {
                if (!radiusResolved)
                {
                    Verb resolvedVerb = ResolveVerb(pawn);
                    configuredRadius = cycle?.weapon != null && resolvedVerb != null
                        ? ResolveCandidateCellRadiusForCycle(
                            pawn, combatState, cycle, resolvedVerb)
                        : 0f;
                    configuredRadiusSquared = configuredRadius * configuredRadius;
                    radiusResolved = true;
                }
                return configuredRadius;
            }

            internal bool CanAdmitNew(
                Pawn pawn,
                RimKataPawnCombatState combatState)
            {
                if (!newAdmissionChecked)
                {
                    newAdmissionAllowed = IsSlotAvailableForNewCandidates(
                        pawn, combatState, cycle, ResolveVerb(pawn));
                    newAdmissionChecked = true;
                }
                return newAdmissionAllowed;
            }

            internal float NewCandidateRadius(
                Pawn pawn,
                RimKataPawnCombatState combatState)
            {
                // A failed new-admission gate must not replace the configured
                // shootability radius used by an already registered candidate.
                return CanAdmitNew(pawn, combatState)
                    ? ResolveConfiguredRadius(pawn, combatState)
                    : 0f;
            }
        }

        internal static bool Begin(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            IntVec3 origin)
        {
            RimKataSharedTargetSearchState search =
                combatState?.sharedTargetSearch;
            if (pawn?.Map == null
                || pawn.InMentalState
                || search == null
                || !origin.IsValid)
            {
                return false;
            }

            if (!RandomAttackEnabled(pawn))
            {
                StopOrdinaryTargetSearch(combatState);
                return false;
            }

            if (search.scanActive)
            {
                return true;
            }

            if (ShouldSkipSaturatedCandidateSearch(
                    pawn,
                    combatState,
                    origin))
            {
                return true;
            }

            float maximumCellRadius =
                MaximumCandidateCellRadius(pawn, combatState);
            if (maximumCellRadius <= 0f)
            {
                return false;
            }

            // A real aim may start while a dormant movement batch is waiting.
            // Keep those identities and only restart the geometry here.
            search.ringRuntime?.ClearRing();
            search.ringRuntime?.discoveredIds.Clear();
            search.sessionActive = true;
            search.scanActive = true;
            search.maximumCandidateCellRadius = maximumCellRadius;
            search.maximumRing = MaximumLogicalRingFromCellRadius(
                maximumCellRadius);
            search.completedRing = 0;
            search.origin = origin;
            RebaseIncomingCandidates(search.ringRuntime, origin);
            search.lastAdvancedTick = -1;
            InitializeCollectionClosure(
                pawn,
                combatState,
                combatState.primaryWeaponCycle);
            InitializeCollectionClosure(
                pawn,
                combatState,
                combatState.secondaryWeaponCycle);
            if (Prefs.DevMode && RimKataDebugHUD.Enabled)
            {
                RimKataDebugHUD.RecordSearchIndicator(pawn);
            }
            return true;
        }

        internal static bool Restart(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            IntVec3 origin)
        {
            RimKataSharedTargetSearchState search =
                combatState?.sharedTargetSearch;
            search?.Reset();
            return Begin(pawn, combatState, origin);
        }

        internal static bool Advance(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            Thing knownTarget)
        {
            RimKataSharedTargetSearchState search =
                combatState?.sharedTargetSearch;
            if (pawn?.Map == null
                || pawn.InMentalState
                || search == null
                || (!search.scanActive && !HasPendingCandidates(combatState)))
            {
                return false;
            }

            if (!RandomAttackEnabled(pawn))
            {
                StopOrdinaryTargetSearch(combatState);
                return false;
            }

            int currentTick = Find.TickManager?.TicksGame ?? -1;
            if (currentTick >= 0 && search.lastAdvancedTick == currentTick)
            {
                return false;
            }
            search.lastAdvancedTick = currentTick;
            search.RefreshNeutralTargets(pawn.Map);

            if (search.scanActive)
            {
                TryAddKnownAutomaticTarget(pawn, combatState, knownTarget);
            }

            RingCandidateSlot primarySlot = new RingCandidateSlot
            {
                cycle = combatState.primaryWeaponCycle
            };
            RingCandidateSlot secondarySlot = new RingCandidateSlot
            {
                cycle = combatState.secondaryWeaponCycle
            };
            float maximumCellRadius = Mathf.Max(
                primarySlot.NewCandidateRadius(pawn, combatState),
                secondarySlot.NewCandidateRadius(pawn, combatState));
            if (maximumCellRadius <= 0f)
            {
                Finish(combatState);
                return false;
            }

            if (search.scanActive)
            {
                search.maximumCandidateCellRadius = maximumCellRadius;
                search.maximumRing = MaximumLogicalRingFromCellRadius(maximumCellRadius);
            }
            RimKataRingSearchRuntime runtime = search.ringRuntime;
            if (runtime == null)
            {
                runtime = new RimKataRingSearchRuntime();
                search.ringRuntime = runtime;
            }
            if (runtime.map == null)
            {
                runtime.map = pawn.Map;
            }
            if (runtime.map != pawn.Map)
            {
                Finish(combatState);
                return false;
            }

            if (search.scanActive
                && runtime.ring == 0
                && search.completedRing < search.maximumRing
                && !ExpansionHasEnoughProspects(
                    pawn, combatState, runtime, ref primarySlot, ref secondarySlot))
            {
                int nextRing = Mathf.Max(0, search.completedRing) + 1;
                CloseCollectionsAtBandEntry(
                    pawn, combatState, nextRing, ref primarySlot, ref secondarySlot);
                if (!BothCandidateCollectionsClosed(combatState))
                {
                    BeginRing(pawn, search, runtime, nextRing, maximumCellRadius);
                }
            }

            if (runtime.ring != 0)
            {
                if (!runtime.collectionComplete)
                {
                    CollectAutomaticTargetsInRing(pawn, combatState, runtime);
                }
                if (runtime.collectionComplete)
                {
                    BufferCompletedRing(
                        pawn, combatState, runtime, ref primarySlot, ref secondarySlot);
                    search.completedRing = runtime.ring;
                    runtime.ClearRing();
                }
            }

            // Every completed ring has its own draw. Earlier rings can keep
            // validating while a later ring is being collected over several ticks.
            bool? dormantMovementAllowed = null;
            for (int i = 0; i < runtime.batches.Count;)
            {
                RimKataRingCandidateBatch batch = runtime.batches[i];
                batch.sealedForDrawing = true;
                if (batch.dormantMovement && !dormantMovementAllowed.HasValue)
                {
                    dormantMovementAllowed =
                        RimKataDualWeaponController.CanReceiveDormantMovingHostiles(pawn);
                }
                if (batch.dormantMovement && dormantMovementAllowed == false)
                {
                    runtime.RemoveBatchAt(i);
                    continue;
                }
                Pawn validationTarget = null;
                bool? newTargetValid = null;
                ProcessNextBufferedCandidate(
                    pawn, combatState, runtime, batch, batch.primary,
                    runtime.primaryPendingIds, ref primarySlot,
                    ref validationTarget, ref newTargetValid);
                ProcessNextBufferedCandidate(
                    pawn, combatState, runtime, batch, batch.secondary,
                    runtime.secondaryPendingIds, ref secondarySlot,
                    ref validationTarget, ref newTargetValid);
                if (!batch.HasPending)
                {
                    runtime.RemoveBatchAt(i);
                }
                else
                {
                    i++;
                }
            }

            if (search.scanActive)
            {
                int outerRing = Mathf.Max(1, search.completedRing);
                UpdateCollectionClosure(
                    pawn, combatState, outerRing, ref primarySlot, ref secondarySlot);
                if (BothCandidateCollectionsClosed(combatState)
                    || (search.completedRing >= search.maximumRing && !runtime.HasPending))
                {
                    CompleteScan(pawn, combatState, ref primarySlot, ref secondarySlot);
                }
            }

            return true;
        }

        internal static bool HasPendingCandidates(RimKataPawnCombatState combatState)
        {
            return combatState?.sharedTargetSearch?.ringRuntime?.HasPending == true;
        }

        private static void RebaseIncomingCandidates(
            RimKataRingSearchRuntime runtime,
            IntVec3 origin)
        {
            if (runtime == null)
            {
                return;
            }
            runtime.incomingOrigin = origin;
            runtime.incomingMaximumRing = 0;
            for (int i = 0; i < runtime.batches.Count; i++)
            {
                RimKataRingCandidateBatch batch = runtime.batches[i];
                if (!batch.dormantMovement)
                {
                    continue;
                }
                batch.center = origin;
                int ring = 1;
                for (int slot = 0; slot < 2; slot++)
                {
                    List<int> remaining = (slot == 0 ? batch.primary : batch.secondary).remaining;
                    for (int j = 0; j < remaining.Count; j++)
                    {
                        Pawn target = batch.targets[remaining[j]];
                        ring = Mathf.Max(ring, MaximumLogicalRingFromCellRadius(
                            Mathf.Sqrt(origin.DistanceToSquared(target.Position))));
                    }
                }
                batch.ring = ring;
                runtime.incomingMaximumRing = Mathf.Max(runtime.incomingMaximumRing, ring);
            }
        }

        internal static bool EnqueueDormantMovingTarget(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            Pawn target,
            bool primaryEligible,
            bool secondaryEligible)
        {
            RimKataSharedTargetSearchState search = combatState?.sharedTargetSearch;
            if (search == null || pawn?.Map == null || target == null
                || (!primaryEligible && !secondaryEligible))
            {
                return false;
            }
            search.RefreshNeutralTargets(pawn.Map);
            RimKataRingSearchRuntime runtime = search.ringRuntime;
            if (runtime == null)
            {
                runtime = new RimKataRingSearchRuntime();
                search.ringRuntime = runtime;
            }
            if (runtime.map != null && runtime.map != pawn.Map)
            {
                runtime.Clear();
            }
            runtime.map = pawn.Map;
            primaryEligible &= !combatState.primaryWeaponCycle.ContainsAutomaticCandidate(target)
                && !runtime.primaryPendingIds.Contains(target.thingIDNumber);
            secondaryEligible &= !combatState.secondaryWeaponCycle.ContainsAutomaticCandidate(target)
                && !runtime.secondaryPendingIds.Contains(target.thingIDNumber);
            if ((!primaryEligible && !secondaryEligible)
                || !IsHostileBufferTarget(pawn, target, search))
            {
                return false;
            }

            if (!runtime.HasPending)
            {
                runtime.incomingOrigin = search.scanActive && search.origin.IsValid
                    ? search.origin : pawn.Position;
                runtime.incomingMaximumRing = 0;
            }
            if (!runtime.incomingOrigin.IsValid)
            {
                runtime.incomingOrigin = search.scanActive && search.origin.IsValid
                    ? search.origin : pawn.Position;
            }
            IntVec3 center = runtime.incomingOrigin;
            int ring = MaximumLogicalRingFromCellRadius(
                Mathf.Sqrt(center.DistanceToSquared(target.Position)));
            runtime.incomingMaximumRing = Mathf.Max(runtime.incomingMaximumRing, ring);
            RimKataRingCandidateBatch batch = null;
            for (int i = runtime.batches.Count - 1; i >= 0; i--)
            {
                RimKataRingCandidateBatch pending = runtime.batches[i];
                if (pending.dormantMovement && !pending.sealedForDrawing
                    && pending.ring == ring && pending.center == center)
                {
                    batch = pending;
                    break;
                }
            }
            if (batch == null)
            {
                batch = runtime.CreateBatch(ring, center, true);
            }
            AddBufferedIdentity(runtime, batch, target, primaryEligible, secondaryEligible);
            return true;
        }

        internal static bool TrySelectCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            Verb verb,
            Thing preferredTarget,
            out Thing target,
            out bool interception)
        {
            target = null;
            interception = false;
            if (pawn?.Map == null || combatState == null || verb == null)
            {
                return false;
            }

            RimKataWeaponCycleState cycle = CycleForVerb(combatState, verb);
            if (cycle == null)
            {
                return false;
            }

            bool randomAttack = RandomAttackEnabled(pawn);
            bool idleProjectilePriority = !randomAttack
                && combatState.idleProjectileSearchTriggerPending;
            bool ordinaryWeaponEnabled = !idleProjectilePriority
                && cycle.weapon != null
                && RimKataEquipmentUtility.IsWeaponEnabled(cycle.weapon.def);
            return TrySelectCandidate(
                pawn,
                combatState,
                cycle,
                verb,
                preferredTarget,
                randomAttack,
                ordinaryWeaponEnabled,
                out target,
                out interception);
        }

        internal static bool TrySelectCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing preferredTarget,
            bool randomAttack,
            bool ordinaryWeaponEnabled,
            out Thing target,
            out bool interception)
        {
            return TrySelectCandidate(
                pawn,
                combatState,
                cycle,
                verb,
                preferredTarget,
                randomAttack,
                ordinaryWeaponEnabled,
                out target,
                out interception,
                combatState?.ownerComponent);
        }

        internal static bool TrySelectCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing preferredTarget,
            bool randomAttack,
            bool ordinaryWeaponEnabled,
            out Thing target,
            out bool interception,
            RimKataMapComponent knownComponent = null)
        {
            target = null;
            interception = false;
            if (pawn?.Map == null
                || combatState == null
                || cycle == null
                || verb == null)
            {
                return false;
            }

            bool idleProjectilePriority = !randomAttack
                && combatState.idleProjectileSearchTriggerPending;
            ordinaryWeaponEnabled = !idleProjectilePriority
                && cycle.weapon != null
                && ordinaryWeaponEnabled;
            if (!randomAttack && !idleProjectilePriority)
            {
                if (ordinaryWeaponEnabled
                    && preferredTarget is Pawn
                    && (verb.IsMeleeAttack
                        || IsCloseCombatContext(combatState)
                        || RimKataDualWeaponController.AutomaticRangedFireAllowed(pawn)))
                {
                    target = preferredTarget;
                    return true;
                }

                return false;
            }

            EligibleCandidates.Clear();
            if (!idleProjectilePriority && ordinaryWeaponEnabled)
            {
                List<Thing> ordinary = cycle.automaticCandidates;
                for (int i = 0;
                    ordinary != null && i < ordinary.Count;
                    i++)
                {
                    EligibleCandidates.Add(ordinary[i]);
                }
            }

            bool includeProjectiles = randomAttack
                || idleProjectilePriority;
            if (includeProjectiles
                && RimKataTargetAccess.SettingsFor(pawn)?.explosiveInterceptionEnabled != false)
            {
                RimKataMapComponent mapComponent = knownComponent
                    ?? combatState.ownerComponent;
                if (mapComponent?.map != pawn.Map
                    && !RimKataCombatStatePresenceCache.TryGetOwner(
                        pawn, out mapComponent))
                {
                    mapComponent = pawn.Map.GetComponent<RimKataMapComponent>();
                }
                if (mapComponent?.HasActiveExplosiveProjectiles == true)
                {
                    float projectileRange = ProjectileRangeForCycle(
                        pawn,
                        cycle,
                        verb);
                    if (projectileRange > 0f)
                    {
                        mapComponent.AppendValidHostileProjectiles(
                            pawn,
                            verb,
                            projectileRange * projectileRange,
                            EligibleCandidates);
                    }
                }
            }

            if (EligibleCandidates.Count == 0)
            {
                return false;
            }

            bool removedCandidate = false;
            while (EligibleCandidates.Count > 0)
            {
                int candidateIndex = randomAttack
                    ? Rand.Range(0, EligibleCandidates.Count)
                    : 0;
                Thing candidate = EligibleCandidates[candidateIndex];
                if (candidate is Projectile)
                {
                    target = candidate;
                    break;
                }

                if (!CanShootRegisteredCandidate(
                    pawn,
                    combatState,
                    cycle,
                    verb,
                    candidate))
                {
                    removedCandidate |= RemoveAutomaticCandidate(
                        combatState,
                        cycle,
                        candidate,
                        !IsLiveRegisteredCandidate(pawn, candidate));
                    EligibleCandidates.RemoveAt(candidateIndex);
                    continue;
                }

                target = candidate;
                break;
            }

            if (removedCandidate)
            {
                NotifyAutomaticCandidateCountChanged(pawn, combatState, true);
            }

            interception = target is Projectile;
            return target != null;
        }

        internal static bool TryAddKnownAutomaticTarget(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            Thing target)
        {
            return TryAddKnownAutomaticTarget(
                pawn,
                combatState,
                target,
                false);
        }

        internal static bool TryAddKnownAutomaticTarget(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            Thing target,
            bool randomAttackVerified)
        {
            if (!(target is Pawn)
                || pawn?.Map == null
                || pawn.InMentalState
                || combatState == null
                || (!randomAttackVerified
                    && !RandomAttackEnabled(pawn)))
            {
                return false;
            }

            bool? newTargetValid = null;
            bool accepted = TryAddValidatedAutomaticTargetToCycle(
                pawn,
                combatState,
                combatState.primaryWeaponCycle,
                target,
                ref newTargetValid);
            accepted = TryAddValidatedAutomaticTargetToCycle(
                pawn,
                combatState,
                combatState.secondaryWeaponCycle,
                target,
                ref newTargetValid)
                || accepted;
            return accepted;
        }

        internal static bool IsValidForVerb(
            Pawn pawn,
            Verb verb,
            Thing target)
        {
            if (pawn?.Map == null || verb == null || target == null)
            {
                return false;
            }

            RimKataPawnCombatState state = pawn.Map
                .GetComponent<RimKataMapComponent>()?
                .GetState(pawn, false);
            RimKataWeaponCycleState cycle = CycleForVerb(state, verb);
            return IsValidForVerb(
                pawn,
                state,
                cycle,
                verb,
                target,
                cycle?.ContainsAutomaticCandidate(target) == true);
        }

        internal static bool IsValidForVerb(
            Pawn pawn,
            RimKataPawnCombatState state,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing target,
            bool registeredCandidate)
        {
            if (pawn?.Map == null
                || state == null
                || cycle == null
                || verb == null
                || target == null)
            {
                return false;
            }

            if (target is Projectile projectile)
            {
                return IsValidProjectileForCycle(
                    pawn,
                    cycle,
                    verb,
                    projectile);
            }

            if (registeredCandidate)
            {
                return CanShootRegisteredCandidate(
                    pawn, state, cycle, verb, target);
            }

            return RimKataTargeting.IsValidAutomaticAttackTarget(pawn, target)
                && IsValidAutomaticTargetForCycle(
                    pawn,
                    state,
                    cycle,
                    verb,
                    target);
        }

        private static void BeginRing(
            Pawn pawn,
            RimKataSharedTargetSearchState search,
            RimKataRingSearchRuntime runtime,
            int ring,
            float maximumCellRadius)
        {
            runtime.ClearRing();
            runtime.ring = ring;
            runtime.center = search.origin.IsValid ? search.origin : pawn.Position;
            float outerRadius = Mathf.Min(
                ring + CandidateCellRadiusPadding, maximumCellRadius);
            ResolveRingHeading(pawn, out int direction, out bool clockwise);
            runtime.traversal.Reset(ring, outerRadius, direction, clockwise);
            int sliceCount = ring <= 12 ? 1 : ring <= 25 ? 2 : 3;
            runtime.cellBudget = ring > 40
                ? 96
                : Mathf.Max(1, (runtime.traversal.CellCount + sliceCount - 1) / sliceCount);

            int farthestX = Mathf.Max(
                Mathf.Abs(runtime.center.x),
                Mathf.Abs(runtime.map.Size.x - 1 - runtime.center.x));
            int farthestZ = Mathf.Max(
                Mathf.Abs(runtime.center.z),
                Mathf.Abs(runtime.map.Size.z - 1 - runtime.center.z));
            float innerRadius = ring <= 1 ? -1f : ring - 1 + CandidateCellRadiusPadding;
            if (innerRadius >= 0f
                && (float)((long)farthestX * farthestX + (long)farthestZ * farthestZ)
                    <= innerRadius * innerRadius)
            {
                CompleteRingCollection(runtime);
            }
        }

        private static void ResolveRingHeading(
            Pawn pawn,
            out int direction,
            out bool clockwise)
        {
            direction = pawn.Rotation.AsInt * 2;
            clockwise = true;
            if (!(pawn.stances?.curStance is Stance_Busy busy)
                || !busy.focusTarg.IsValid)
            {
                return;
            }

            LocalTargetInfo focus = busy.focusTarg;
            if (focus.HasThing
                && (focus.Thing.Destroyed
                    || !focus.Thing.Spawned
                    || focus.Thing.Map != pawn.Map))
            {
                return;
            }
            IntVec3 targetCell = focus.Cell;
            if (!targetCell.IsValid || targetCell == pawn.Position)
            {
                return;
            }

            int x = targetCell.x - pawn.Position.x;
            int z = targetCell.z - pawn.Position.z;
            float angle = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
            direction = Mathf.RoundToInt(angle / 45f) & 7;
            clockwise = Mathf.DeltaAngle(direction * 45f, angle) >= 0f;
        }

        private static void CollectAutomaticTargetsInRing(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime)
        {
            Map map = runtime.map;
            bool recordSearchCells = Prefs.DevMode
                && RimKataDebugHUD.SearchRangeEnabled
                && RimKataDebugHUD.TryBeginActualSearchCellRecording(pawn, map);
            int checkedCells = 0;
            bool primaryOpen = combatState.primaryWeaponCycle?.weapon != null
                && !combatState.primaryWeaponCycle.automaticCandidateCollectionClosed;
            bool secondaryOpen = combatState.secondaryWeaponCycle?.weapon != null
                && !combatState.secondaryWeaponCycle.automaticCandidateCollectionClosed;
            while (checkedCells < runtime.cellBudget
                && runtime.traversal.TryNext(out IntVec3 offset))
            {
                int x = runtime.center.x + offset.x;
                int z = runtime.center.z + offset.z;
                if ((uint)x >= (uint)map.Size.x || (uint)z >= (uint)map.Size.z)
                {
                    continue;
                }

                // Every in-map cell consumes the same geometry quota.
                checkedCells++;
                int cellIndex = map.cellIndices.CellToIndex(x, z);
                CollectAutomaticTargetsInCell(
                    map.thingGrid.ThingsListAtFast(cellIndex), combatState, runtime,
                    primaryOpen, secondaryOpen);
                if (recordSearchCells)
                {
                    RimKataDebugHUD.RecordActualSearchCell(map, new IntVec3(x, 0, z));
                }
            }
            if (runtime.traversal.Complete)
            {
                CompleteRingCollection(runtime);
            }
        }

        private static void CollectAutomaticTargetsInCell(
            List<Thing> things,
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime,
            bool primaryOpen,
            bool secondaryOpen)
        {
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn candidate
                    && !combatState.sharedTargetSearch.neutralPawnIds.Contains(candidate.thingIDNumber)
                    && runtime.discoveredIds.Add(candidate.thingIDNumber)
                    && ((primaryOpen
                            && !combatState.primaryWeaponCycle.ContainsAutomaticCandidate(candidate)
                            && !runtime.primaryPendingIds.Contains(candidate.thingIDNumber))
                        || (secondaryOpen
                            && !combatState.secondaryWeaponCycle.ContainsAutomaticCandidate(candidate)
                            && !runtime.secondaryPendingIds.Contains(candidate.thingIDNumber))))
                {
                    runtime.discovered.Add(candidate);
                }
            }
        }

        private static void CompleteRingCollection(RimKataRingSearchRuntime runtime)
        {
            runtime.collectionComplete = true;
        }

        private static void BufferCompletedRing(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime,
            ref RingCandidateSlot primarySlot,
            ref RingCandidateSlot secondarySlot)
        {
            RimKataRingCandidateBatch batch = null;
            float primaryRadius = primarySlot.NewCandidateRadius(pawn, combatState);
            float secondaryRadius = secondarySlot.NewCandidateRadius(pawn, combatState);
            for (int i = 0; i < runtime.discovered.Count; i++)
            {
                Pawn target = runtime.discovered[i];
                if (target.Destroyed || !target.Spawned || target.Map != runtime.map)
                {
                    continue;
                }
                float distanceSquared = pawn.Position.DistanceToSquared(target.Position);
                bool primary = primaryRadius > 0f
                    && !primarySlot.cycle.automaticCandidateCollectionClosed
                    && distanceSquared <= primarySlot.configuredRadiusSquared
                    && !primarySlot.cycle.ContainsAutomaticCandidate(target)
                    && !runtime.primaryPendingIds.Contains(target.thingIDNumber);
                bool secondary = secondaryRadius > 0f
                    && !secondarySlot.cycle.automaticCandidateCollectionClosed
                    && distanceSquared <= secondarySlot.configuredRadiusSquared
                    && !secondarySlot.cycle.ContainsAutomaticCandidate(target)
                    && !runtime.secondaryPendingIds.Contains(target.thingIDNumber);
                if ((!primary && !secondary)
                    || !IsHostileBufferTarget(pawn, target, combatState.sharedTargetSearch))
                {
                    continue;
                }
                if (batch == null)
                {
                    batch = runtime.CreateBatch(runtime.ring, runtime.center, false);
                }
                AddBufferedIdentity(runtime, batch, target, primary, secondary);
            }
        }

        private static bool IsHostileBufferTarget(
            Pawn pawn, Pawn target, RimKataSharedTargetSearchState search)
        {
            if (target == pawn || target.Destroyed || !target.Spawned
                || target.Map != pawn.Map || target.Dead
                || RimKataTargeting.IsIncapacitatedTarget(target)
                || RimKataTargeting.IsSleepingOrDormant(target))
            {
                return false;
            }
            if (target.HostileTo(pawn))
            {
                return true;
            }

            // Only a completed nonhostile verdict enters this temporary list.
            // Some native hostility rules change without a target-cache event.
            if (CanRememberNonhostile(pawn, target))
            {
                search.neutralPawnIds.Add(target.thingIDNumber);
            }
            return false;
        }

        private static bool CanRememberNonhostile(Pawn pawn, Pawn target)
        {
            RaceProperties pawnRace = pawn.RaceProps;
            RaceProperties targetRace = target.RaceProps;
            // Predator distance and a colony roamer's recent-combat timeout
            // can change hostility while the same jobs/relations remain active.
            if (pawnRace.predator || targetRace.predator
                || (pawnRace.roamMtbDays.HasValue && pawn.Faction == Faction.OfPlayer)
                || (targetRace.roamMtbDays.HasValue && target.Faction == Faction.OfPlayer))
            {
                return false;
            }
            // Releasing a herd animal changes shambler hostility without a
            // target-cache notification. Ordinary pawn pairs skip this getter.
            return !(pawnRace.Animal && pawnRace.herdAnimal
                    && pawn.Faction != null && target.IsShambler)
                && !(targetRace.Animal && targetRace.herdAnimal
                    && target.Faction != null && pawn.IsShambler);
        }

        private static void AddBufferedIdentity(
            RimKataRingSearchRuntime runtime,
            RimKataRingCandidateBatch batch,
            Pawn target,
            bool primary,
            bool secondary)
        {
            int index = batch.targets.Count;
            batch.targets.Add(target);
            if (primary)
            {
                runtime.primaryPendingIds.Add(target.thingIDNumber);
                batch.primary.remaining.Add(index);
            }
            if (secondary)
            {
                runtime.secondaryPendingIds.Add(target.thingIDNumber);
                batch.secondary.remaining.Add(index);
            }
        }

        private static bool ExpansionHasEnoughProspects(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime,
            ref RingCandidateSlot primarySlot,
            ref RingCandidateSlot secondarySlot)
        {
            int ring = combatState.sharedTargetSearch.completedRing;
            return ring > 0
                && SlotHasEnoughProspects(pawn, combatState, runtime, ref primarySlot, ring)
                && SlotHasEnoughProspects(pawn, combatState, runtime, ref secondarySlot, ring);
        }

        private static bool SlotHasEnoughProspects(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime,
            ref RingCandidateSlot slot,
            int ring)
        {
            float radius = slot.NewCandidateRadius(pawn, combatState);
            RimKataWeaponCycleState cycle = slot.cycle;
            if (radius <= 0f || cycle.automaticCandidateCollectionClosed
                || ring >= MaximumLogicalRingFromCellRadius(radius))
            {
                return true;
            }
            return UsesRangedCandidateLimit(cycle)
                && CountStoredCandidatesThroughRing(cycle, SearchCenter(pawn, combatState), ring)
                    + CountPendingThroughRing(combatState, runtime, cycle, ring)
                        >= EffectiveCandidateLimitForRing(pawn, cycle, ring);
        }

        private static int CountPendingThroughRing(
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime,
            RimKataWeaponCycleState cycle,
            int ring)
        {
            bool primary = cycle == combatState.primaryWeaponCycle;
            int count = 0;
            for (int i = 0; i < runtime.batches.Count; i++)
            {
                RimKataRingCandidateBatch batch = runtime.batches[i];
                RimKataRingCandidateDraw draw = primary ? batch.primary : batch.secondary;
                if (batch.dormantMovement)
                {
                    float radius = ring + CandidateCellRadiusPadding;
                    float radiusSquared = radius * radius;
                    for (int j = 0; j < draw.remaining.Count; j++)
                    {
                        Pawn target = batch.targets[draw.remaining[j]];
                        if (runtime.incomingOrigin.DistanceToSquared(target.Position) <= radiusSquared)
                        {
                            count++;
                        }
                    }
                }
                else if (batch.ring <= ring)
                {
                    count += draw.remaining.Count;
                }
            }
            return count;
        }

        private static void ProcessNextBufferedCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataRingSearchRuntime runtime,
            RimKataRingCandidateBatch batch,
            RimKataRingCandidateDraw draw,
            HashSet<int> pendingIds,
            ref RingCandidateSlot slot,
            ref Pawn validationTarget,
            ref bool? newTargetValid)
        {
            if (draw.remaining.Count == 0)
            {
                return;
            }

            RimKataWeaponCycleState cycle = slot.cycle;
            if (cycle?.weapon == null
                || (combatState.sharedTargetSearch.scanActive
                    && cycle.automaticCandidateCollectionClosed)
                || slot.NewCandidateRadius(pawn, combatState) <= 0f)
            {
                RimKataRingSearchRuntime.ReleaseDraw(batch, draw, pendingIds);
                return;
            }

            bool limited = UsesRangedCandidateLimit(cycle);
            if (limited)
            {
                // All concurrent rings consult the same current slot population.
                // No batch owns a stale copy of a vacant-slot count.
                int capacityRing = Mathf.Max(batch.ring, runtime.incomingMaximumRing);
                if (combatState.sharedTargetSearch.scanActive)
                {
                    capacityRing = Mathf.Max(capacityRing, combatState.sharedTargetSearch.completedRing);
                }
                IntVec3 center = combatState.sharedTargetSearch.scanActive
                    ? SearchCenter(pawn, combatState)
                    : runtime.incomingOrigin.IsValid ? runtime.incomingOrigin : batch.center;
                if (CountStoredCandidatesThroughRing(cycle, center, capacityRing)
                    >= EffectiveCandidateLimitForRing(pawn, cycle, capacityRing))
                {
                    RimKataRingSearchRuntime.ReleaseDraw(batch, draw, pendingIds);
                    return;
                }
            }

            // Each slot owns its lottery. A rejection consumes this tick's single draw.
            Pawn target = batch.targets[draw.Draw()];
            pendingIds.Remove(target.thingIDNumber);
            if (batch.dormantMovement && target.pather?.Moving != true)
            {
                return;
            }
            TryAddBufferedAutomaticTarget(
                pawn, combatState, ref slot, target,
                ref validationTarget, ref newTargetValid);
        }

        private static bool TryAddBufferedAutomaticTarget(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            ref RingCandidateSlot slot,
            Pawn target,
            ref Pawn validationTarget,
            ref bool? newTargetValid)
        {
            RimKataWeaponCycleState cycle = slot.cycle;
            if (target == null
                || target.Destroyed
                || !target.Spawned
                || target.Map != pawn.Map
                || cycle.ContainsAutomaticCandidate(target))
            {
                return false;
            }

            // Admission uses live positions; the scan origin only defines ring membership.
            float candidateCellRadius = slot.ResolveConfiguredRadius(pawn, combatState);
            if (candidateCellRadius <= 0f
                || pawn.Position.DistanceToSquared(target.Position) > slot.configuredRadiusSquared)
            {
                return false;
            }
            if (validationTarget != target)
            {
                validationTarget = target;
                newTargetValid = null;
            }
            if (!newTargetValid.HasValue)
            {
                // Hostility was shared at ingress. Delayed admission only needs
                // the remaining current target state and per-weapon shootability.
                newTargetValid = !target.Position.Fogged(pawn.Map)
                    && RimKataTargeting.IsPawnTargetStateValid(target);
            }
            if (!newTargetValid.Value
                || !CanHitRingCandidate(pawn, combatState, ref slot, target))
            {
                return false;
            }
            return cycle.AddAutomaticCandidate(target);
        }

        private static bool CanHitRingCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            ref RingCandidateSlot slot,
            Thing target)
        {
            Verb verb = slot.ResolveVerb(pawn);
            return !verb.IsMeleeAttack && IsCloseCombatContext(combatState)
                ? pawn.CanReachImmediate(target, PathEndMode.Touch)
                : verb.CanHitTarget(target);
        }

        private static bool TryAddValidatedAutomaticTargetToCycle(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Thing target,
            ref bool? newTargetValid)
        {
            if (cycle == null)
            {
                return false;
            }

            if (cycle.ContainsAutomaticCandidate(target))
            {
                // Registered candidates are checked when selected or maintained.
                return true;
            }

            if (!IsValidNewAutomaticTarget(pawn, target, ref newTargetValid))
            {
                return false;
            }

            Verb verb = CombatVerbForCycle(pawn, cycle);
            if (IsValidAutomaticTargetForCycle(
                pawn,
                combatState,
                cycle,
                verb,
                target))
            {
                return cycle.AddAutomaticCandidate(target)
                    || cycle.ContainsAutomaticCandidate(target);
            }

            return false;
        }

        private static bool IsValidNewAutomaticTarget(
            Pawn pawn,
            Thing target,
            ref bool? newTargetValid)
        {
            if (!newTargetValid.HasValue)
            {
                newTargetValid = RimKataTargeting.IsValidAutomaticAttackTarget(
                    pawn, target);
            }
            return newTargetValid.Value;
        }

        internal static bool IsLiveRegisteredCandidate(Pawn pawn, Thing target)
        {
            return pawn?.Map != null
                && target != null
                && !target.Destroyed
                && target.Spawned
                && target.Map == pawn.Map
                && (!(target is Pawn targetPawn)
                    || (!targetPawn.Dead
                        && !RimKataTargeting.IsIncapacitatedTarget(targetPawn)));
        }

        internal static bool CanShootRegisteredCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing target)
        {
            if (cycle?.weapon == null
                || verb == null
                || !IsLiveRegisteredCandidate(pawn, target))
            {
                return false;
            }

            // Membership carries admission; only current shooting conditions remain.
            float candidateCellRadius = ResolveCandidateCellRadiusForCycle(
                pawn, combatState, cycle, verb);
            if (candidateCellRadius <= 0f
                || pawn.Position.DistanceToSquared(target.Position)
                    > candidateCellRadius * candidateCellRadius)
            {
                return false;
            }

            return !verb.IsMeleeAttack && IsCloseCombatContext(combatState)
                ? pawn.CanReachImmediate(target, PathEndMode.Touch)
                : verb.CanHitTarget(target);
        }

        private static bool IsValidAutomaticTargetForCycle(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Thing target)
        {
            if (target == null)
            {
                return false;
            }

            float candidateCellRadius = CandidateCellRadiusForCycle(
                pawn,
                combatState,
                cycle,
                verb);
            if (candidateCellRadius <= 0f
                || pawn.Position.DistanceToSquared(target.Position)
                    > candidateCellRadius * candidateCellRadius)
            {
                return false;
            }

            if (!verb.IsMeleeAttack && IsCloseCombatContext(combatState))
            {
                return pawn.CanReachImmediate(target, PathEndMode.Touch);
            }

            return verb.CanHitTarget(target);
        }

        private static bool IsValidProjectileForCycle(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb,
            Projectile projectile)
        {
            float range = ProjectileRangeForCycle(pawn, cycle, verb);
            return range > 0f
                && RimKataTargeting.IsValidExplosiveProjectileForVerb(
                    pawn,
                    verb,
                    projectile,
                    range * range);
        }

        internal static bool EvictAutomaticCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Thing target,
            bool requestRefill)
        {
            if (target == null || target is Projectile)
            {
                return false;
            }

            bool removed = RemoveAutomaticCandidate(
                combatState,
                cycle,
                target,
                !IsLiveRegisteredCandidate(pawn, target));
            if (removed)
            {
                NotifyAutomaticCandidateCountChanged(
                    pawn, combatState, requestRefill);
            }

            return removed;
        }

        private static void NotifyAutomaticCandidateCountChanged(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            bool requestRefill)
        {
            if (combatState == null)
            {
                return;
            }

            if (combatState.sharedTargetSearch?.scanActive == true)
            {
                // Reallow cap scheduling without restarting the in-progress rings.
                combatState.ResetCandidateSaturationExpansion(false);
                return;
            }

            combatState.ResetCandidateSaturationExpansion(true);
            if (requestRefill
                && pawn?.Map != null
                && RandomAttackEnabled(pawn)
                && !IsCloseCombatContext(combatState))
            {
                Begin(pawn, combatState, pawn.Position);
            }
        }

        private static bool RemoveAutomaticCandidate(
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Thing target,
            bool globallyInvalid)
        {
            if (!globallyInvalid)
            {
                return RemoveAutomaticCandidateFromCycle(cycle, target);
            }

            bool removed = RemoveAutomaticCandidateFromCycle(
                combatState?.primaryWeaponCycle,
                target);
            if (RemoveAutomaticCandidateFromCycle(
                combatState?.secondaryWeaponCycle,
                target))
            {
                removed = true;
            }
            return removed;
        }

        private static bool RemoveAutomaticCandidateFromCycle(
            RimKataWeaponCycleState cycle,
            Thing target)
        {
            if (cycle?.RemoveAutomaticCandidate(target) != true)
            {
                return false;
            }

            cycle.automaticCandidateCollectionClosed = false;
            return true;
        }

        private static void UpdateCollectionClosure(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            int outerRing,
            ref RingCandidateSlot primarySlot,
            ref RingCandidateSlot secondarySlot)
        {
            bool primarySaturated = UpdateCycleCollectionClosure(
                pawn,
                combatState,
                ref primarySlot,
                outerRing,
                out bool primaryVacancy);
            bool secondarySaturated = UpdateCycleCollectionClosure(
                pawn,
                combatState,
                ref secondarySlot,
                outerRing,
                out bool secondaryVacancy);

            ReleaseConsumedCandidateLimit(
                pawn,
                combatState.primaryWeaponCycle,
                outerRing);
            ReleaseConsumedCandidateLimit(
                pawn,
                combatState.secondaryWeaponCycle,
                outerRing);

            if (!IsCloseCombatContext(combatState))
            {
                if (!combatState.candidateSaturationExpansionUsed
                    && (primarySaturated || secondarySaturated))
                {
                    if (primarySaturated)
                    {
                        TryScheduleNextCandidateLimit(
                            pawn,
                            combatState,
                            ref primarySlot,
                            outerRing);
                    }
                    if (secondarySaturated)
                    {
                        TryScheduleNextCandidateLimit(
                            pawn,
                            combatState,
                            ref secondarySlot,
                            outerRing);
                    }
                    combatState.candidateSaturationExpansionUsed = true;
                }

                if (primaryVacancy || secondaryVacancy)
                {
                    combatState.candidateSaturationExpansionUsed = false;
                }
            }
        }

        private static void CloseCollectionsAtBandEntry(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            int outerRing,
            ref RingCandidateSlot primarySlot,
            ref RingCandidateSlot secondarySlot)
        {
            int previousLimit = outerRing > 1
                ? CandidateLimitForRing(pawn, outerRing - 1)
                : CandidateLimitForRing(pawn, outerRing);
            int currentLimit = CandidateLimitForRing(pawn, outerRing);
            if (currentLimit >= previousLimit)
            {
                return;
            }

            bool primarySaturated = CloseCycleCollectionAtBandEntry(
                pawn,
                combatState,
                combatState?.primaryWeaponCycle,
                outerRing);
            bool secondarySaturated = CloseCycleCollectionAtBandEntry(
                pawn,
                combatState,
                combatState?.secondaryWeaponCycle,
                outerRing);

            if (!IsCloseCombatContext(combatState)
                && !combatState.candidateSaturationExpansionUsed
                && (primarySaturated || secondarySaturated))
            {
                // A falling cap closes the slot before the regular ring-end
                // saturation check. Preserve its next-band reservation here.
                if (primarySaturated)
                {
                    TryScheduleNextCandidateLimit(
                        pawn, combatState, ref primarySlot, outerRing);
                }
                if (secondarySaturated)
                {
                    TryScheduleNextCandidateLimit(
                        pawn, combatState, ref secondarySlot, outerRing);
                }
                combatState.candidateSaturationExpansionUsed = true;
            }
        }

        private static bool CloseCycleCollectionAtBandEntry(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            int outerRing)
        {
            if (!UsesRangedCandidateLimit(cycle)
                || cycle.automaticCandidateCollectionClosed)
            {
                return false;
            }

            int limit = EffectiveCandidateLimitForRing(pawn, cycle, outerRing);
            if (CountStoredCandidatesThroughRing(
                    cycle,
                    SearchCenter(pawn, combatState),
                    outerRing) >= limit)
            {
                cycle.automaticCandidateCollectionClosed = true;
                return true;
            }
            return false;
        }

        private static bool BothCandidateCollectionsClosed(
            RimKataPawnCombatState combatState)
        {
            return (combatState?.primaryWeaponCycle == null
                    || combatState.primaryWeaponCycle
                        .automaticCandidateCollectionClosed)
                && (combatState?.secondaryWeaponCycle == null
                    || combatState.secondaryWeaponCycle
                        .automaticCandidateCollectionClosed);
        }

        private static bool ShouldSkipSaturatedCandidateSearch(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            IntVec3 origin)
        {
            if (combatState?.candidateSaturationExpansionUsed != true
                || IsCloseCombatContext(combatState))
            {
                return false;
            }

            if (HasPendingCandidateLimitOverride(combatState))
            {
                return false;
            }

            bool primarySaturated = StoredCandidatesSaturateCycle(
                pawn,
                combatState,
                combatState.primaryWeaponCycle,
                origin,
                out bool primaryUsable);
            bool secondarySaturated = StoredCandidatesSaturateCycle(
                pawn,
                combatState,
                combatState.secondaryWeaponCycle,
                origin,
                out bool secondaryUsable);
            if ((primaryUsable || secondaryUsable)
                && (!primaryUsable || primarySaturated)
                && (!secondaryUsable || secondarySaturated))
            {
                return true;
            }

            combatState.ResetCandidateSaturationExpansion(true);
            return false;
        }

        private static bool HasPendingCandidateLimitOverride(
            RimKataPawnCombatState combatState)
        {
            return combatState?.primaryWeaponCycle
                    ?.pendingCandidateLimitOverride > 0
                || combatState?.primaryWeaponCycle
                    ?.activeCandidateLimitOverride > 0
                || combatState?.secondaryWeaponCycle
                    ?.pendingCandidateLimitOverride > 0
                || combatState?.secondaryWeaponCycle
                    ?.activeCandidateLimitOverride > 0;
        }

        private static bool StoredCandidatesSaturateCycle(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            IntVec3 origin,
            out bool usable)
        {
            usable = UsesRangedCandidateLimit(cycle);
            if (!usable)
            {
                return false;
            }

            Verb verb = CombatVerbForCycle(pawn, cycle);
            float candidateCellRadius = CandidateCellRadiusForCycle(
                pawn,
                combatState,
                cycle,
                verb);
            usable = verb != null && candidateCellRadius > 0f;
            if (!usable)
            {
                return false;
            }

            List<Thing> candidates = cycle.automaticCandidates;
            int maximumRing = MaximumLogicalRingFromCellRadius(
                candidateCellRadius);
            for (int ring = 1; ring <= maximumRing; ring++)
            {
                int limit = CandidateLimitForRing(pawn, ring);
                if (candidates == null || candidates.Count < limit)
                {
                    continue;
                }

                float outerRadius = Mathf.Min(
                    ring + CandidateCellRadiusPadding,
                    candidateCellRadius);
                float outerSquared = outerRadius * outerRadius;
                int count = 0;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Thing candidate = candidates[i];
                    if (candidate != null
                        && origin.DistanceToSquared(candidate.Position)
                            <= outerSquared
                        && ++count >= limit)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void InitializeCollectionClosure(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle)
        {
            if (cycle == null)
            {
                return;
            }

            Verb verb = CombatVerbForCycle(pawn, cycle);
            float candidateCellRadius = CandidateCellRadiusForCycle(
                pawn,
                combatState,
                cycle,
                verb);
            if (IsCloseCombatContext(combatState))
            {
                cycle.activeCandidateLimitOverride = 0;
            }
            else
            {
                cycle.activeCandidateLimitOverride = Mathf.Max(
                    0,
                    cycle.pendingCandidateLimitOverride);
                cycle.pendingCandidateLimitOverride = 0;
            }
            cycle.automaticCandidateCollectionClosed =
                candidateCellRadius <= 0f;
        }

        private static bool UpdateCycleCollectionClosure(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            ref RingCandidateSlot slot,
            int outerRing,
            out bool hasCandidateVacancy)
        {
            RimKataWeaponCycleState cycle = slot.cycle;
            hasCandidateVacancy = false;
            if (!UsesRangedCandidateLimit(cycle)
                || cycle.automaticCandidateCollectionClosed)
            {
                return false;
            }

            float candidateCellRadius = slot.NewCandidateRadius(pawn, combatState);
            int limit = EffectiveCandidateLimitForRing(pawn, cycle, outerRing);
            int maximumRing = MaximumLogicalRingFromCellRadius(
                candidateCellRadius);
            bool reachedWeaponRange = candidateCellRadius <= 0f
                || outerRing >= maximumRing;
            bool saturated = CountStoredCandidatesThroughRing(
                    cycle,
                    SearchCenter(pawn, combatState),
                    outerRing) >= limit;
            hasCandidateVacancy = !saturated;

            RimKataRingSearchRuntime runtime = combatState.sharedTargetSearch.ringRuntime;
            bool pendingAdmission = runtime != null
                && (cycle == combatState.primaryWeaponCycle
                    ? runtime.primaryPendingIds.Count != 0
                    : runtime.secondaryPendingIds.Count != 0);
            cycle.automaticCandidateCollectionClosed = saturated
                || (reachedWeaponRange && !pendingAdmission);
            return saturated;
        }

        private static bool TryScheduleNextCandidateLimit(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            ref RingCandidateSlot slot,
            int saturatedRing)
        {
            RimKataWeaponCycleState cycle = slot.cycle;
            if (pawn?.Map == null
                || !UsesRangedCandidateLimit(cycle)
                || cycle.pendingCandidateLimitOverride > 0
                || cycle.activeCandidateLimitOverride > 0)
            {
                return false;
            }

            float candidateCellRadius = slot.NewCandidateRadius(pawn, combatState);
            int maximumRing = MaximumLogicalRingFromCellRadius(
                candidateCellRadius);
            int currentLimit = CandidateLimitForRing(pawn, saturatedRing);
            int nextLimit = 0;
            for (int ring = saturatedRing + 1; ring <= maximumRing; ring++)
            {
                int ringLimit = CandidateLimitForRing(pawn, ring);
                // Editable bands may decrease before growing again. Reserve
                // the next larger capacity instead of stopping at a decrease.
                if (ringLimit <= currentLimit)
                {
                    continue;
                }

                nextLimit = ringLimit;
                break;
            }

            if (nextLimit <= currentLimit)
            {
                return false;
            }

            cycle.pendingCandidateLimitOverride = nextLimit;
            return true;
        }

        private static int EffectiveCandidateLimitForRing(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            int ring)
        {
            return Mathf.Max(
                CandidateLimitForRing(pawn, ring),
                cycle?.activeCandidateLimitOverride ?? 0);
        }

        private static void ReleaseConsumedCandidateLimit(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            int ring)
        {
            if (cycle?.activeCandidateLimitOverride > 0
                && CandidateLimitForRing(pawn, ring)
                    >= cycle.activeCandidateLimitOverride)
            {
                cycle.activeCandidateLimitOverride = 0;
            }
        }

        private static int CountStoredCandidatesThroughRing(
            RimKataWeaponCycleState cycle,
            IntVec3 center,
            int outerRing)
        {
            List<Thing> candidates = cycle?.automaticCandidates;
            if (candidates == null
                || !center.IsValid
                || outerRing <= 0)
            {
                return 0;
            }

            float outerRadius =
                outerRing + CandidateCellRadiusPadding;
            float outerSquared = outerRadius * outerRadius;
            int count = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                Thing candidate = candidates[i];
                if (candidate == null
                    || center.DistanceToSquared(candidate.Position)
                        > outerSquared)
                {
                    continue;
                }

                count++;
            }
            return count;
        }

        private static IntVec3 SearchCenter(
            Pawn pawn,
            RimKataPawnCombatState combatState)
        {
            return combatState?.sharedTargetSearch?.origin.IsValid == true
                ? combatState.sharedTargetSearch.origin
                : pawn?.Position ?? IntVec3.Invalid;
        }

        private static int CandidateLimitForRing(Pawn pawn, int ring)
        {
            float radius = ring;
            RimKataRangeBands bands = RimKataRangeUtility.CurrentBands;
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(pawn);
            if (radius <= bands.Touch + RadiusEpsilon)
            {
                return settings?.touchCandidateLimit ?? 8;
            }
            if (radius <= bands.Short + RadiusEpsilon)
            {
                return settings?.shortCandidateLimit ?? 16;
            }
            if (radius <= bands.Medium + RadiusEpsilon)
            {
                return settings?.mediumCandidateLimit ?? 12;
            }
            if (radius <= bands.Long + RadiusEpsilon)
            {
                return settings?.longCandidateLimit ?? 8;
            }
            return settings?.beyondCandidateLimit ?? 4;
        }

        private static bool UsesRangedCandidateLimit(
            RimKataWeaponCycleState cycle)
        {
            return cycle?.weapon?.def?.IsRangedWeapon == true;
        }

        private static int MaximumLogicalRingFromCellRadius(
            float candidateCellRadius)
        {
            return Mathf.Max(
                1,
                Mathf.CeilToInt(
                    Mathf.Max(
                        0f,
                        candidateCellRadius - CandidateCellRadiusPadding)));
        }

        private static float MaximumCandidateCellRadius(
            Pawn pawn,
            RimKataPawnCombatState combatState)
        {
            RimKataWeaponCycleState primary =
                combatState?.primaryWeaponCycle;
            RimKataWeaponCycleState secondary =
                combatState?.secondaryWeaponCycle;
            return Mathf.Max(
                CandidateCellRadiusForCycle(
                    pawn,
                    combatState,
                    primary,
                    CombatVerbForCycle(pawn, primary)),
                CandidateCellRadiusForCycle(
                    pawn,
                    combatState,
                    secondary,
                    CombatVerbForCycle(pawn, secondary)));
        }

        private static float CandidateCellRadiusForCycle(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb)
        {
            if (!IsSlotAvailableForNewCandidates(pawn, combatState, cycle, verb))
            {
                return 0f;
            }

            return ResolveCandidateCellRadiusForCycle(
                pawn, combatState, cycle, verb);
        }

        private static bool IsSlotAvailableForNewCandidates(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb)
        {
            bool closeCombatContext = IsCloseCombatContext(combatState);
            return pawn?.Map != null
                && cycle?.weapon != null
                && RimKataEquipmentUtility.IsWeaponEnabled(cycle.weapon.def)
                && verb != null
                && RimKataDualWeaponController.VerbUsable(
                    pawn,
                    verb,
                    closeCombatContext);
        }

        private static float ResolveCandidateCellRadiusForCycle(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            RimKataWeaponCycleState cycle,
            Verb verb)
        {
            bool closeCombatContext = IsCloseCombatContext(combatState);
            if (closeCombatContext)
            {
                return UsesRangedCandidateLimit(cycle)
                    ? CloseCombatRangedCandidateCellRadius
                    : RimKataRangeUtility.ResolveEffectiveRange(
                        pawn,
                        cycle.weapon,
                        verb);
            }

            return AutomaticCandidateCellRadiusForCycle(pawn, cycle, verb);
        }

        private static float AutomaticCandidateCellRadiusForCycle(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb)
        {
            if (pawn?.Map == null || cycle?.weapon == null || verb == null)
            {
                return 0f;
            }

            if (!verb.IsMeleeAttack)
            {
                if (!RimKataDualWeaponController.AutomaticRangedFireAllowed(pawn))
                {
                    return 0f;
                }

                return Mathf.Max(
                    0f,
                    RimKataRangeUtility.ResolveCandidateCellRadius(
                        pawn,
                        cycle.weapon,
                        verb));
            }

            return RimKataRangeUtility.ResolveEffectiveRange(
                pawn,
                cycle.weapon,
                verb);
        }

        private static float ProjectileRangeForCycle(
            Pawn pawn,
            RimKataWeaponCycleState cycle,
            Verb verb)
        {
            if (pawn?.Map == null
                || cycle?.weapon == null
                || verb == null
                || verb.IsMeleeAttack
                || !RimKataDualWeaponController.AutomaticRangedFireAllowed(pawn)
                || !RimKataDualWeaponController.VerbUsable(
                    pawn,
                    verb,
                    false))
            {
                return 0f;
            }

            return RimKataRangeUtility.ResolveEffectiveRange(
                pawn,
                cycle.weapon,
                verb);
        }

        private static bool IsCloseCombatContext(
            RimKataPawnCombatState combatState)
        {
            return combatState?.dualCloseCombatActive == true;
        }

        private static Verb CombatVerbForCycle(
            Pawn pawn,
            RimKataWeaponCycleState cycle)
        {
            return RimKataWeaponSlotUtility.CombatVerb(
                pawn,
                cycle?.weapon);
        }

        private static RimKataWeaponCycleState CycleForVerb(
            RimKataPawnCombatState combatState,
            Verb verb)
        {
            ThingWithComps weapon = verb?.EquipmentSource as ThingWithComps;
            if (weapon == null || combatState == null)
            {
                return null;
            }

            if (combatState.primaryWeaponCycle?.weapon == weapon)
            {
                return combatState.primaryWeaponCycle;
            }
            return combatState.secondaryWeaponCycle?.weapon == weapon
                ? combatState.secondaryWeaponCycle
                : null;
        }

        private static bool RandomAttackEnabled(Pawn pawn)
        {
            return RimKataEligibility.RandomAttackEnabledForPawn(pawn);
        }

        private static void StopOrdinaryTargetSearch(
            RimKataPawnCombatState combatState)
        {
            ClearOrdinaryCandidateList(
                combatState?.primaryWeaponCycle);
            ClearOrdinaryCandidateList(
                combatState?.secondaryWeaponCycle);
            combatState?.sharedTargetSearch?.Reset();
        }

        private static void ClearOrdinaryCandidateList(
            RimKataWeaponCycleState cycle)
        {
            cycle?.ClearStoredAutomaticCandidates();
            if (cycle == null)
            {
                return;
            }

            cycle.automaticCandidateCollectionClosed = false;
            cycle.pendingCandidateLimitOverride = 0;
            cycle.activeCandidateLimitOverride = 0;
        }

        private static void CompleteScan(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            ref RingCandidateSlot primarySlot,
            ref RingCandidateSlot secondarySlot)
        {
            bool removedCandidate = CheckNextStoredCandidate(
                pawn, combatState, ref primarySlot);
            removedCandidate |= CheckNextStoredCandidate(
                pawn, combatState, ref secondarySlot);
            if (removedCandidate)
            {
                // Notify while the scan is still active: preserve the next-band
                // reservation and do not turn maintenance into another search.
                NotifyAutomaticCandidateCountChanged(pawn, combatState, false);
            }
            Finish(combatState);
        }

        private static bool CheckNextStoredCandidate(
            Pawn pawn,
            RimKataPawnCombatState combatState,
            ref RingCandidateSlot slot)
        {
            RimKataWeaponCycleState cycle = slot.cycle;
            if (cycle == null
                || !cycle.TryGetNextAutomaticCandidateForValidation(out Thing target))
            {
                return false;
            }

            if (CanShootRegisteredCandidate(
                pawn, combatState, cycle, slot.ResolveVerb(pawn), target))
            {
                return false;
            }

            return RemoveAutomaticCandidate(
                combatState,
                cycle,
                target,
                !IsLiveRegisteredCandidate(pawn, target));
        }

        private static void Finish(RimKataPawnCombatState combatState)
        {
            RimKataSharedTargetSearchState search =
                combatState?.sharedTargetSearch;
            if (search == null)
            {
                return;
            }

            search.scanActive = false;
            search.ClearRingRuntime();
            if (combatState.primaryWeaponCycle != null)
            {
                combatState.primaryWeaponCycle.activeCandidateLimitOverride = 0;
            }
            if (combatState.secondaryWeaponCycle != null)
            {
                combatState.secondaryWeaponCycle.activeCandidateLimitOverride = 0;
            }
        }
    }
}
