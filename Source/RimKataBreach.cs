using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal enum BreachPhase { Approach, Run, Slide, Rise, Wait, Released }

    internal struct RimKataBreachVisual
    {
        internal Rot4 facing;
        internal float angle, progress;
        internal bool poseActive, protectedPose, carryDoor, holdWeapons;
        internal Vector3 doorOrigin;
        internal RimKataBreachDoorSnapshot door;
    }

    public sealed class RimKataBreachState : IExposable
    {
        internal Pawn pawn;
        internal Building_Door target;
        internal IntVec3 runup, doorCell, direction, endpoint;
        internal Vector3 doorOrigin;
        internal Rot4 facing;
        internal BreachPhase phase;
        internal int ownerJobId, elapsed, slideTicks, waitTicks;
        internal int attackResumeTick = -1;
        internal Toil waitToil;
        internal Action waitTickAction, waitFinishAction;
        internal Job waitRangedJob;
        internal int waitRangedJobId = -1;
        internal bool waitRangedAllowed;
        internal float rate, riseFrom = 1f;
        internal bool player, protectedState, broken, dropped, carried;
        internal RimKataBreachDoorSnapshot door;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref target, "target");
            Scribe_Values.Look(ref runup, "runup");
            Scribe_Values.Look(ref doorCell, "doorCell");
            Scribe_Values.Look(ref doorOrigin, "doorOrigin");
            Scribe_Values.Look(ref direction, "direction");
            Scribe_Values.Look(ref endpoint, "endpoint");
            Scribe_Values.Look(ref facing, "facing");
            Scribe_Values.Look(ref phase, "phase");
            Scribe_Values.Look(ref ownerJobId, "ownerJobId");
            Scribe_Values.Look(ref elapsed, "elapsed");
            Scribe_Values.Look(ref slideTicks, "slideTicks");
            Scribe_Values.Look(ref waitTicks, "waitTicks");
            Scribe_Values.Look(ref attackResumeTick, "attackResumeTick", -1);
            Scribe_Values.Look(ref waitRangedJobId, "waitRangedJobId", -1);
            Scribe_Values.Look(ref waitRangedAllowed, "waitRangedAllowed");
            Scribe_Values.Look(ref rate, "rate");
            Scribe_Values.Look(ref riseFrom, "riseFrom", 1f);
            Scribe_Values.Look(ref player, "player");
            Scribe_Values.Look(ref protectedState, "protected");
            Scribe_Values.Look(ref broken, "broken");
            Scribe_Values.Look(ref dropped, "dropped");
            Scribe_Values.Look(ref carried, "carried");
            Scribe_Deep.Look(ref door, "doorGraphic");
        }

        internal float Progress => phase == BreachPhase.Slide ? Mathf.Clamp01(elapsed / 6f)
            : phase == BreachPhase.Rise ? riseFrom * Mathf.Clamp01(1f - elapsed / 6f) : 0f;

        internal void Drop()
        {
            if (!broken || dropped || pawn?.Map == null) return;
            dropped = true;
            door?.Drop(pawn.Map, pawn.Position);
        }

        internal void DetachWaitToil(bool removeFinish = true)
        {
            // The Job itself is saved by the pawn. Resolve by ID after loading,
            // and never restore a pooled Job that has since been reused.
            Job rangedJob = waitRangedJob ?? pawn?.CurJob;
            if (waitRangedJobId >= 0 && rangedJob?.loadID == waitRangedJobId)
                rangedJob.canUseRangedWeapon = waitRangedAllowed;
            waitRangedJob = null;
            waitRangedJobId = -1;
            waitRangedAllowed = false;
            if (waitToil != null)
            {
                waitToil.tickAction -= waitTickAction;
                if (removeFinish) waitToil.finishActions?.Remove(waitFinishAction);
            }
            waitToil = null;
            waitTickAction = waitFinishAction = null;
        }
    }

    public sealed class RimKataBreachRegistry : GameComponent
    {
        private readonly Game game;
        internal readonly Dictionary<Pawn, RimKataBreachState> states = new Dictionary<Pawn, RimKataBreachState>();
        internal readonly HashSet<Pawn> waiting = new HashSet<Pawn>();
        private List<RimKataBreachState> saved;
        public RimKataBreachRegistry(Game game)
        { this.game = game; RimKataBreachUtility.SetRegistry(this); }
        internal bool IsCurrent => ReferenceEquals(game, Verse.Current.Game);

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) saved = new List<RimKataBreachState>(states.Values);
            Scribe_Collections.Look(ref saved, "breaches", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                states.Clear();
                waiting.Clear();
                if (saved != null) foreach (RimKataBreachState state in saved)
                    if (state?.pawn != null) states[state.pawn] = state;
                saved = null;
            }
        }

        public override void FinalizeInit()
        {
            RimKataBreachUtility.SetRegistry(this);
            foreach (RimKataBreachState state in new List<RimKataBreachState>(states.Values))
            {
                // Restore a saved temporary Job flag before deciding whether the
                // remaining breach wait should be attached again or discarded.
                state.DetachWaitToil();
                if (state.pawn.Dead || state.pawn.Downed || !state.pawn.Spawned
                    || !RimKataEligibilityCache.IsCachedQualifiedPawn(state.pawn)
                    || RimKataTemporaryInactivity.IsInactive(state.pawn))
                { state.Drop(); RimKataBreachUtility.Remove(state.pawn); }
                else if (state.phase != BreachPhase.Released && state.phase != BreachPhase.Wait
                    && state.pawn.CurJob?.loadID != state.ownerJobId)
                { state.Drop(); RimKataBreachUtility.Remove(state.pawn); }
                else
                {
                    RimKataBreachUtility.Publish(state);
                    if (state.phase == BreachPhase.Wait)
                    {
                        RimKataBreachUtility.BeginNaturalWait(state);
                        LongEventHandler.ExecuteWhenFinished(() =>
                        {
                            if (!IsCurrent || RimKataBreachUtility.Get(state.pawn) != state) return;
                            if (state.pawn.jobs?.curDriver is JobDriver_RimKataBreach driver)
                                driver.ResumeNaturalWait();
                            else RimKataBreachUtility.RestoreWaitToil(state.pawn);
                        });
                    }
                    else if (state.phase == BreachPhase.Run || state.phase == BreachPhase.Slide)
                        LongEventHandler.ExecuteWhenFinished(() =>
                        {
                            if (IsCurrent && RimKataBreachUtility.Get(state.pawn) == state)
                                RimKataBreachMovement.StartStraight(state.pawn, state, true);
                        });
                }
            }
        }
    }

    internal static class RimKataBreachUtility
    {
        private static RimKataBreachRegistry registry;
        internal static int AttackStateVersion { get; private set; }
        private static readonly Func<JobDriver, Toil> CurrentToil =
            AccessTools.MethodDelegate<Func<JobDriver, Toil>>(
                AccessTools.PropertyGetter(typeof(JobDriver), "CurToil"));
        internal static void SetRegistry(RimKataBreachRegistry value)
        {
            if (registry == value) return;
            if (registry != null)
                foreach (RimKataBreachState state in registry.states.Values)
                {
                    state.DetachWaitToil();
                    RimKataResponseVisualParticipantCache.PublishBreach(state.pawn, null);
                    RimKataBreachRender.Clear(state.pawn);
                }
            registry = value;
            unchecked { ++AttackStateVersion; }
        }
        internal static RimKataBreachRegistry Registry => registry?.IsCurrent == true ? registry : null;
        internal static RimKataBreachState Get(Pawn pawn)
        {
            var current = Registry;
            return pawn != null && current != null && current.states.Count != 0
                && current.states.TryGetValue(pawn, out var state) ? state : null;
        }
        internal static bool BlocksAttacks(Pawn pawn) => Get(pawn) is RimKataBreachState s && s.phase != BreachPhase.Released;
        internal static bool IsWaiting(Pawn pawn)
        {
            var current = Registry;
            return pawn != null && current != null && current.waiting.Count != 0
                && current.waiting.Contains(pawn);
        }
        internal static bool IsNaturalWait(Pawn pawn, Job job)
            => pawn?.Drafted == true && job != null && !job.playerForced
                && (job.def == JobDefOf.Wait_Combat
                    || job.def == JobDefOf.Wait_MaintainPosture && job.expiryInterval == 1);

        internal static void BeginNaturalWait(RimKataBreachState state)
        {
            if (!RimKataEligibilityCache.IsCachedQualifiedPawn(state.pawn)
                || RimKataTemporaryInactivity.IsInactive(state.pawn))
            { state.Drop(); Remove(state.pawn); return; }
            int now = Find.TickManager.TicksGame;
            if (state.attackResumeTick < 0)
                state.attackResumeTick = (int)Math.Min(int.MaxValue,
                    (long)now + Math.Max(0, state.waitTicks - state.elapsed));
            // Native job control may change without ending protection or fixed facing.
            if (state.attackResumeTick <= now || !state.pawn.Drafted
                || state.pawn.CurJob?.loadID != state.ownerJobId
                    && !IsNaturalWait(state.pawn, state.pawn.CurJob)) ReleaseWait(state);
            else
            {
                state.phase = BreachPhase.Wait;
                Registry.waiting.Add(state.pawn);
                Publish(state);
            }
        }

        private static void ReleaseWait(RimKataBreachState state)
        {
            state.DetachWaitToil();
            Registry.waiting.Remove(state.pawn);
            state.phase = BreachPhase.Released;
            unchecked { ++AttackStateVersion; }
            Publish(state);
        }

        internal static void AttachWaitToil(Pawn pawn, Toil toil)
        {
            if (!IsWaiting(pawn) || toil == null || !IsNaturalWait(pawn, pawn.CurJob)) return;
            var state = Get(pawn);
            if (state.waitToil == toil) return;
            state.DetachWaitToil();
            state.waitToil = toil;
            Job waitJob = pawn.CurJob;
            if (waitJob.def == JobDefOf.Wait_Combat)
            {
                // DecorateWaitToil runs before initAction. Stop ranged scanning
                // at its existing Job gate, while leaving fire beating and the
                // pawn's natural wait/job control intact.
                state.waitRangedJob = waitJob;
                state.waitRangedJobId = waitJob.loadID;
                state.waitRangedAllowed = waitJob.canUseRangedWeapon;
                waitJob.canUseRangedWeapon = false;
            }
            state.waitTickAction = () =>
            {
                // Only this participant's existing native wait owns the delay.
                if (state.waitToil != toil) return;
                if (pawn.Dead || pawn.Downed || !pawn.Spawned
                    || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                    || RimKataTemporaryInactivity.IsInactive(pawn)) Remove(pawn);
                else if (!pawn.Drafted
                    || Find.TickManager.TicksGame >= state.attackResumeTick) ReleaseWait(state);
            };
            state.waitFinishAction = () =>
            {
                // Cleanup iterates finishActions; do not remove this entry mid-loop.
                if (state.waitToil == toil) state.DetachWaitToil(false);
            };
            toil.tickAction += state.waitTickAction;
            toil.AddFinishAction(state.waitFinishAction);
        }

        internal static void RestoreWaitToil(Pawn pawn)
        {
            if (IsWaiting(pawn) && pawn.jobs?.curDriver is JobDriver_Wait driver)
                AttachWaitToil(pawn, CurrentToil(driver));
        }
        internal static bool IsProtected(Pawn pawn) => Get(pawn)?.protectedState == true;
        internal static bool TryGetVisual(Pawn pawn, out RimKataBreachVisual visual)
            => RimKataWorldRenderContext.TryBreach(pawn, out visual);

        internal static void Publish(RimKataBreachState state)
        {
            if (state == null || Get(state.pawn) != state) return;
            float progress = state.Progress;
            // Fall away from the door while keeping the original travel facing.
            // The sprite's head starts north, as in the ordinary backward fall.
            float angle = Mathf.DeltaAngle(0f, state.facing.AsAngle + 180f) * progress;
            var visual = new RimKataBreachVisual
            {
                facing = state.facing, angle = angle, progress = progress,
                poseActive = state.phase == BreachPhase.Slide || state.phase == BreachPhase.Rise,
                protectedPose = state.protectedState, carryDoor = state.carried,
                holdWeapons = state.phase != BreachPhase.Released,
                doorOrigin = state.doorOrigin,
                door = state.broken && !state.dropped ? state.door : null
            };
            RimKataResponseVisualParticipantCache.PublishBreach(state.pawn, visual);
        }

        internal static void Remove(Pawn pawn)
        {
            if (pawn == null || Registry == null) return;
            Get(pawn)?.DetachWaitToil();
            Registry.waiting.Remove(pawn);
            if (Registry.states.Remove(pawn)) unchecked { ++AttackStateVersion; }
            RimKataResponseVisualParticipantCache.PublishBreach(pawn, null);
            RimKataBreachRender.Clear(pawn);
        }

        internal static bool TryCreate(Pawn pawn, Building_Door door, bool player, out Job replacement)
        {
            replacement = null;
            if (pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || (player && !pawn.Drafted) || BlocksAttacks(pawn)
                || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)
                || RimKataTemporaryInactivity.IsInactive(pawn)
                || RimKataTargetAccess.SettingsFor(pawn)?.breachEnabled != true
                || door?.Spawned != true || door.Open || !door.def.destroyable || door.Map != pawn.Map)
                return false;
            // Prefer the approach on the pawn's current side. A run-up must be
            // reachable without crossing the very door being breached.
            IntVec3 chosen = IntVec3.Invalid, direction = IntVec3.Invalid, entry = IntVec3.Invalid;
            CellRect occupied = door.OccupiedRect();
            int best = int.MaxValue;
            foreach (IntVec3 delta in GenAdj.CardinalDirections)
            {
                IntVec3 edge = new IntVec3(Mathf.Clamp(pawn.Position.x, occupied.minX, occupied.maxX),
                    0, Mathf.Clamp(pawn.Position.z, occupied.minZ, occupied.maxZ));
                if (delta.x != 0) edge.x = delta.x > 0 ? occupied.minX : occupied.maxX;
                else edge.z = delta.z > 0 ? occupied.minZ : occupied.maxZ;
                IntVec3 start = edge - delta * 3;
                if ((pawn.Position - edge).x * delta.x + (pawn.Position - edge).z * delta.z > 0) continue;
                bool clear = true;
                for (int i = 1; i <= 3; i++) if (!CanEnter(pawn, edge - delta * i, null)) { clear = false; break; }
                if (!clear || !pawn.CanReach(start, PathEndMode.OnCell, Danger.Deadly)) continue;
                int distance = (pawn.Position - start).LengthHorizontalSquared;
                if (distance >= best) continue;
                using (PawnPath approach = pawn.Map.pathFinder.FindPathNow(pawn.Position, start, pawn))
                    if (!approach.Found || approach.NodesReversed.Exists(cell => occupied.Contains(cell))) continue;
                best = distance;
                chosen = start;
                direction = delta;
                entry = edge;
            }
            if (!chosen.IsValid || !RimKataBreachDoorSnapshot.TryCapture(door, out var snapshot)) return false;
            replacement = JobMaker.MakeJob(RimKataDefOf.RimKata_Breach, door);
            replacement.playerForced = player;
            replacement.locomotionUrgency = LocomotionUrgency.Sprint;
            replacement.canBashDoors = true;
            var settings = RimKataTargetAccess.SettingsFor(pawn);
            var state = new RimKataBreachState
            {
                pawn = pawn, target = door, doorCell = entry, doorOrigin = door.DrawPos, runup = chosen,
                direction = direction, facing = Rot4.FromIntVec3(direction),
                ownerJobId = replacement.loadID, player = player, door = snapshot,
                slideTicks = Math.Max(0, settings.breachSlideDurationTicks),
                waitTicks = player ? Math.Max(0, settings.breachWaitDurationTicks) : 0
            };
            Get(pawn)?.Drop();
            Remove(pawn);
            Registry.states[pawn] = state;
            unchecked { ++AttackStateVersion; }
            return true;
        }

        internal static bool CanEnter(Pawn pawn, IntVec3 cell, Building_Door allowed)
        {
            Map map = pawn.Map;
            if (!cell.InBounds(map) || !cell.WalkableBy(map, pawn)) return false;
            Building building = cell.GetEdifice(map);
            if (building != null && building != allowed
                && (building.BlocksPawn(pawn) || building is Building_Door door && !door.Open)) return false;
            Pawn occupant = cell.GetFirstPawn(map);
            return occupant == null || occupant == pawn || occupant.Downed;
        }

        internal static void NotifyMeleeAttempt(Pawn pawn)
            => NotifyMeleeAttempt(pawn, Get(pawn));

        internal static void NotifyMeleeAttempt(Pawn pawn, RimKataBreachState state)
        {
            if (state == null) return;
            state.protectedState = false;
            if (pawn.jobs?.curDriver is JobDriver_RimKataBreach driver) driver.Cancel();
            else Remove(pawn);
        }

        internal static void NotifyPathStarted(Pawn pawn)
        {
            var state = Get(pawn);
            if (state != null && (state.phase == BreachPhase.Released || state.phase == BreachPhase.Wait)
                && pawn.pather.Moving) Remove(pawn);
        }

        internal static void NotifyJobStarting(Pawn pawn, Job next)
        {
            var state = Get(pawn);
            if (state == null || next?.loadID == state.ownerJobId) return;
            if (state.phase == BreachPhase.Wait)
            {
                // EndCurrentJob inserts a one-tick posture wait before Wait_Combat.
                if (IsNaturalWait(pawn, next)) return;
                ReleaseWait(state);
            }
            if (state.phase != BreachPhase.Released)
            { state.Drop(); Remove(pawn); }
            else if (next?.def == JobDefOf.Goto) Remove(pawn);
        }

        internal static void NotifyEligibilityLost(Pawn pawn)
        {
            var state = Get(pawn);
            if (state == null) return;
            if (pawn.jobs?.curDriver is JobDriver_RimKataBreach driver) driver.Cancel();
            else { state.Drop(); Remove(pawn); }
        }

        internal static bool OwnDoor(Pawn pawn, Building_Door door)
        {
            var state = Get(pawn);
            return state != null && !state.broken && state.target == door && pawn.CurJob?.loadID == state.ownerJobId;
        }
    }

    public sealed class JobDriver_RimKataBreach : JobDriver
    {
        private RimKataBreachState State => RimKataBreachUtility.Get(pawn);
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            AddFinishAction(Finish);
            var toil = new Toil { defaultCompleteMode = ToilCompleteMode.Never };
            toil.initAction = Begin;
            toil.tickAction = Tick;
            yield return toil;
        }

        private void Begin()
        {
            var state = State;
            if (state == null || state.ownerJobId != job.loadID) { EndJobWith(JobCondition.Incompletable); return; }
            if (state.phase == BreachPhase.Wait || state.phase == BreachPhase.Released)
            { ResumeNaturalWait(); return; }
            RimKataGroundPoseUtility.Clear(pawn.Map.GetComponent<RimKataMapComponent>()?.GetState(pawn, false));
            RimKataDualWeaponController.Reset(pawn, false);
            if (pawn.equipment != null) foreach (ThingWithComps equipment in pawn.equipment.AllEquipmentListForReading)
            {
                var verbs = equipment.TryGetComp<CompEquippable>()?.AllVerbs;
                if (verbs != null) foreach (Verb verb in verbs) verb.Reset();
            }
            pawn.stances.CancelBusyStanceSoft();
            if (state.phase == BreachPhase.Approach) pawn.pather.StartPath(state.runup, PathEndMode.OnCell);
            else if (state.phase == BreachPhase.Run || state.phase == BreachPhase.Slide) RimKataBreachMovement.StartStraight(pawn, state);
            if (State == state) RimKataBreachUtility.Publish(state);
        }

        private void Tick()
        {
            var state = State;
            if (state == null || state.ownerJobId != job.loadID) { EndJobWith(JobCondition.Incompletable); return; }
            if (pawn.Dead || pawn.Downed || RimKataTemporaryInactivity.IsInactive(pawn)
                || state.player && !pawn.Drafted || !RimKataEligibilityCache.IsCachedQualifiedPawn(pawn)) { Cancel(); return; }
            if (!state.broken && (state.target?.Spawned != true || state.target.Open)) { Cancel(); return; }
            if (state.phase == BreachPhase.Approach && pawn.pather.Moving
                && pawn.pather.nextCell.GetEdifice(pawn.Map) == state.target) { Cancel(); return; }
            // Only the actual breach Job inspects its next movement cell. No
            // ordinary pawn tick is patched to poll for a breach or obstruction.
            if ((state.phase == BreachPhase.Run || state.phase == BreachPhase.Slide)
                && pawn.pather.Moving && pawn.pather.nextCell != pawn.Position
                && !RimKataBreachUtility.CanEnter(pawn, pawn.pather.nextCell, state.target))
            { RimKataBreachMovement.Stop(pawn, state); return; }
            switch (state.phase)
            {
                case BreachPhase.Approach:
                    if (pawn.Position == state.runup && !pawn.pather.Moving)
                    { state.phase = BreachPhase.Run; RimKataBreachMovement.StartStraight(pawn, state); }
                    break;
                case BreachPhase.Slide:
                    Vector3 offset = pawn.Position.ToVector3Shifted() - state.doorOrigin;
                    state.carried |= offset.x * state.direction.x + offset.z * state.direction.z >= 0f;
                    if (++state.elapsed >= state.slideTicks) BeginRise();
                    break;
                case BreachPhase.Rise:
                    if (++state.elapsed >= 6)
                    {
                        state.phase = BreachPhase.Wait;
                        state.elapsed = 0;
                        ResumeNaturalWait();
                        return;
                    }
                    break;
                case BreachPhase.Wait:
                    ResumeNaturalWait();
                    return;
            }
            if (State == state) RimKataBreachUtility.Publish(state);
        }

        internal void ResumeNaturalWait()
        {
            var state = State;
            if (state == null || state.ownerJobId != job.loadID) return;
            if (state.phase != BreachPhase.Released) RimKataBreachUtility.BeginNaturalWait(state);
            // Preserve the queue; ordinary AI and drafted Wait_Combat resume.
            EndJobWith(JobCondition.Succeeded);
        }

        internal void BreakDoor()
        {
            var state = State;
            if (state?.phase != BreachPhase.Run || state.target?.Spawned != true
                || state.target.Open || !state.target.def.destroyable) { Cancel(); return; }
            Building_Door door = state.target;
            door.Destroy(DestroyMode.KillFinalize);
            // Destruction callbacks may cancel or replace this job. Never publish
            // success into a stale state or treat a refused destruction as a breach.
            if (State != state || pawn.jobs?.curDriver != this) return;
            if (!door.Destroyed || door.Spawned) { Cancel(); return; }
            state.broken = true;
            state.protectedState = true;
            state.phase = BreachPhase.Slide;
            state.elapsed = 0;
            state.target = null;
            job.targetA = LocalTargetInfo.Invalid;
            RimKataBreachUtility.Publish(state);
        }

        internal void BeginRise()
        {
            var state = State;
            if (state == null || state.phase == BreachPhase.Rise || state.phase == BreachPhase.Wait) return;
            if (!state.broken) { Cancel(); return; }
            state.riseFrom = state.Progress;
            state.phase = BreachPhase.Rise;
            state.elapsed = 0;
            pawn.pather.StopDead();
            state.Drop();
            RimKataBreachUtility.Publish(state);
        }

        internal void Cancel()
        {
            State?.Drop();
            RimKataBreachUtility.Remove(pawn);
            pawn.pather.StopDead();
            EndJobWith(JobCondition.Incompletable);
        }

        private void Finish(JobCondition condition)
        {
            var state = State;
            if (state == null || state.ownerJobId != job.loadID) return;
            state.Drop();
            if (!state.protectedState || (state.phase != BreachPhase.Released
                && !(state.phase == BreachPhase.Wait && condition == JobCondition.Succeeded)))
                RimKataBreachUtility.Remove(pawn);
        }

        public override void Notify_PatherArrived()
        {
            if (State?.phase == BreachPhase.Approach) return;
            BeginRise();
        }
        public override void Notify_PatherFailed()
        {
            if (State?.broken == true) BeginRise(); else Cancel();
        }
    }
}
