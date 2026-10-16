using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed class RimKataThrownPawn : Thing, IThingHolderTickable
    {
        internal const int TicksPerCell = 8;
        private ThingOwner<Thing> contents;
        private Pawn thrower;
        private IntVec3 origin;
        private Vector3 launchOffset, visualPosition;
        private int elapsed, crossedCells, stunTicks;
        private bool wasDrafted, fireAtWill;
        private bool resolving;
        private bool pushed;
        private IntVec3 pushDirection;
        private Rot4 pushFacing;
        private DamageDef pushDamage;
        private JobQueue pushedJobs;

        private int FlightCells => pushed ? 1 : 2;
        private IntVec3 FlightDirection => pushed ? pushDirection : Rotation.FacingCell;

        public RimKataThrownPawn() { contents = new ThingOwner<Thing>(this, true, LookMode.Deep); }
        public bool ShouldTickContents => false;
        public override int UpdateRateTicks => 1;
        public override Vector3 DrawPos => visualPosition;
        private Pawn Victim => contents.Count != 0 ? contents[0] as Pawn : null;
        public ThingOwner GetDirectlyHeldThings() => contents;
        public void GetChildHolders(List<IThingHolder> outChildren)
            => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, contents);

        internal static bool CanLaunch(RimKataSubdueState state)
            => RimKataSubdueUtility.IsRelationValid(state)
                && (state.pawn.Position + state.facing.FacingCell * 2).InBounds(state.pawn.Map);

        internal static void Launch(RimKataSubdueState state)
        {
            if (!CanLaunch(state)) return;
            Pawn carrier = state.pawn, target = state.target;
            var flight = (RimKataThrownPawn)ThingMaker.MakeThing(RimKataDefOf.RimKata_ThrownPawn);
            flight.thrower = carrier;
            flight.origin = carrier.Position;
            flight.Rotation = state.facing;
            flight.launchOffset = RimKataSubdueRender.CarriedPosition(carrier, carrier.DrawPos, state.facing)
                - flight.origin.ToVector3Shifted();
            flight.stunTicks = RimKataTargetAccess.SettingsFor(carrier)?.subdueImpactStunTicks ?? 180;
            flight.wasDrafted = target.Drafted;
            flight.fireAtWill = target.drafter?.FireAtWill ?? true;
            GenSpawn.Spawn(flight, flight.origin, carrier.Map, state.facing);
            if (!carrier.carryTracker.innerContainer.TryTransferToContainer(target, flight.contents))
            { flight.Destroy(); return; }
            RimKataSubdueUtility.Remove(state);
            RimKataSubdueJobs.StopAttackJob(carrier);
            target.Rotation = state.facing.Opposite;
        }

        internal static void LaunchPush(Pawn attacker, Pawn target, IntVec3 direction, DamageDef damageDef)
        {
            Map map = target.Map;
            var flight = (RimKataThrownPawn)ThingMaker.MakeThing(RimKataDefOf.RimKata_ThrownPawn);
            flight.pushed = true;
            flight.pushDirection = direction;
            flight.pushFacing = target.Rotation;
            flight.pushDamage = damageDef;
            flight.thrower = attacker;
            flight.origin = target.Position;
            flight.launchOffset = target.DrawPos - flight.origin.ToVector3Shifted();
            flight.stunTicks = 120;
            flight.wasDrafted = target.Drafted;
            flight.fireAtWill = target.drafter?.FireAtWill ?? true;
            GenSpawn.Spawn(flight, flight.origin, map, target.Rotation);
            try
            {
                if (target.CurJob != null) target.jobs.SuspendCurrentJob(JobCondition.InterruptForced);
                flight.pushedJobs = target.jobs.CaptureAndClearJobQueue();
                target.DeSpawn(DestroyMode.WillReplace);
                flight.contents.TryAdd(target);
            }
            finally
            {
                if (flight.Victim != target)
                {
                    try
                    {
                        if (!target.Destroyed && !target.Spawned && target.holdingOwner == null)
                            GenSpawn.Spawn(target, flight.origin, map, flight.pushFacing);
                        if (target.Spawned) flight.RestorePawn(target);
                    }
                    finally
                    {
                        flight.Destroy();
                        if (!target.Destroyed && !target.Spawned && target.holdingOwner == null)
                        {
                            flight.RestorePawn(target);
                            if (!Find.WorldPawns.Contains(target))
                                Find.WorldPawns.PassToWorld(target, PawnDiscardDecideMode.Decide);
                        }
                    }
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref contents, "thrownContents", this);
            Scribe_References.Look(ref thrower, "thrower");
            Scribe_Values.Look(ref origin, "throwOrigin");
            Scribe_Values.Look(ref launchOffset, "throwLaunchOffset");
            Scribe_Values.Look(ref elapsed, "throwElapsed");
            Scribe_Values.Look(ref crossedCells, "throwCrossedCells");
            Scribe_Values.Look(ref stunTicks, "throwStunTicks", 180);
            Scribe_Values.Look(ref wasDrafted, "throwWasDrafted");
            Scribe_Values.Look(ref fireAtWill, "throwFireAtWill", true);
            Scribe_Values.Look(ref pushed, "pushed");
            Scribe_Values.Look(ref pushDirection, "pushDirection");
            Scribe_Values.Look(ref pushFacing, "pushFacing");
            Scribe_Defs.Look(ref pushDamage, "pushDamage");
            Scribe_Deep.Look(ref pushedJobs, "pushedJobs");
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            RefreshVisual();
        }

        protected override void TickInterval(int delta)
        {
            if (Victim == null) { Destroy(); return; }
            elapsed = Math.Min(TicksPerCell * FlightCells, elapsed + delta);
            while (crossedCells < FlightCells && elapsed >= (crossedCells + 1) * TicksPerCell)
            {
                int step = crossedCells + 1;
                IntVec3 landing;
                bool impact, discard;
                bool finished = pushed
                    ? EvaluatePushArrival(out landing, out impact, out discard)
                    : EvaluateArrival(Map, Victim, origin, Rotation, step,
                        out landing, out impact, out discard);
                if (finished) { Finish(landing, impact, discard); return; }
                crossedCells = step;
                Position = landing;
            }
            RefreshVisual();
        }

        internal static bool EvaluateArrival(Map map, Pawn pawn, IntVec3 origin, Rot4 facing,
            int step, out IntVec3 landing, out bool impact, out bool discard)
        {
            IntVec3 next = origin + facing.FacingCell * step;
            landing = origin + facing.FacingCell * (step - 1);
            impact = discard = false;
            if (!next.InBounds(map)) return true;
            var things = next.GetThingList(map);
            for (int i = 0; i < things.Count; ++i)
                if (things[i] is Building building && (building.BlocksPawn(pawn)
                    || building is Building_Door door && !door.Open))
                {
                    impact = true;
                    discard = !landing.WalkableBy(map, pawn);
                    return true;
                }
            landing = next;
            if (step < 2) return false;
            discard = !landing.WalkableBy(map, pawn);
            return true;
        }

        private bool EvaluatePushArrival(out IntVec3 landing, out bool impact, out bool discard)
        {
            Pawn pawn = Victim;
            IntVec3 next = origin + pushDirection;
            landing = origin;
            impact = discard = false;
            if (!next.InBounds(Map)) return true;
            impact = RimKataPushKick.StructureBlocks(Map, pawn, next)
                || pushDirection.x != 0 && pushDirection.z != 0
                    && (RimKataPushKick.StructureBlocks(Map, pawn, origin + new IntVec3(pushDirection.x, 0, 0))
                        || RimKataPushKick.StructureBlocks(Map, pawn, origin + new IntVec3(0, 0, pushDirection.z)));
            if (!impact) landing = next;
            discard = !landing.WalkableBy(Map, pawn);
            return true;
        }

        private void RefreshVisual()
        {
            float progress = Mathf.Clamp01(elapsed / (float)(TicksPerCell * FlightCells));
            visualPosition = origin.ToVector3Shifted() + FlightDirection.ToVector3() * (progress * FlightCells)
                + launchOffset * (1f - progress);
            visualPosition.y = AltitudeLayer.Pawn.AltitudeFor() + 0.04f;
        }

        public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false)
        {
            Victim?.DynamicDrawPhaseAt(phase, visualPosition, flip);
        }

        private void Finish(IntVec3 cell, bool impact, bool discard)
        {
            Pawn victim = Victim;
            resolving = true;
            try
            {
                if (victim == null) return;
                if (discard)
                {
                    contents.Remove(victim);
                    pushedJobs?.Clear(victim, canReturnToPool: true);
                    pushedJobs = null;
                    RimKataSubdueUtility.KillAndDiscard(victim, pushed
                        ? new DamageInfo(pushDamage, 0f, instigator: thrower) : (DamageInfo?)null);
                    return;
                }
                if (contents.TryDrop(victim, cell, Map, ThingPlaceMode.Direct, out _)
                    || contents.TryDrop(victim, cell, Map, ThingPlaceMode.Near, out _,
                        nearPlaceValidator: c => c.WalkableBy(Map, victim)))
                {
                    RestorePawn(victim);
                    if (impact && !victim.Dead) victim.stances?.stunner.StunFor(stunTicks, thrower);
                }
                else PreserveContents();
            }
            finally
            {
                // Foreign landing callbacks may throw before transferring the held pawn.
                if (Victim != null) PreserveContents();
                Destroy();
                resolving = false;
            }
        }

        private void RestorePawn(Pawn victim)
        {
            if (!victim.Spawned)
            {
                pushedJobs?.Clear(victim, canReturnToPool: true);
                pushedJobs = null;
                return;
            }
            victim.Rotation = pushed ? pushFacing : Rotation.Opposite;
            if (victim.drafter != null)
            { victim.drafter.Drafted = wasDrafted; victim.drafter.FireAtWill = fireAtWill; }
            if (pushedJobs != null)
            {
                QueuedJob queued;
                while ((queued = pushedJobs.Dequeue()) != null)
                    victim.jobs.jobQueue.EnqueueLast(queued.job, queued.tag);
                pushedJobs = null;
                victim.jobs.CheckForJobOverride(0f, ignoreQueue: false);
            }
            if (pushed) victim.Drawer?.tweener?.ResetTweenedPosToRoot();
        }

        private void PreserveContents()
        {
            Pawn victim = Victim;
            if (victim == null) return;
            contents.Remove(victim);
            if (pushed) RestorePawn(victim);
            if (!Find.WorldPawns.Contains(victim)) Find.WorldPawns.PassToWorld(victim, PawnDiscardDecideMode.Decide);
        }

        public override void Notify_MyMapRemoved()
        {
            // MapDeiniter has already registered nested pawns with WorldPawns.
            Pawn victim = Victim;
            if (victim != null && Find.WorldPawns.Contains(victim))
            {
                contents.Remove(victim);
                if (pushed) RestorePawn(victim);
            }
            base.Notify_MyMapRemoved();
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (!resolving && Victim != null)
            {
                Pawn victim = Victim;
                if (Map == null || !contents.TryDrop(victim, Position, Map, ThingPlaceMode.Near,
                    out _, nearPlaceValidator: c => c.WalkableBy(Map, victim))) PreserveContents();
                else if (pushed) RestorePawn(victim);
            }
            base.Destroy(mode);
        }
    }
}
