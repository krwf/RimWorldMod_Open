using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    // One hit owns one victim. The victim stays spawned at the destination throughout.
    public sealed class RimKataPushMotion : Thing
    {
        internal const int Duration = 8;
        private Pawn victim, attacker;
        private Vector3 start, end;
        private DamageDef damageDef;
        private int elapsed;
        private RimKataPushVisual visual;
        private bool finishing;

        public override int UpdateRateTicks => 1;
        private bool OwnsWait => victim?.CurJobDef == RimKataDefOf.RimKata_Pushed
            && victim.CurJob.targetA.Thing == this;

        internal static bool Start(Pawn attacker, Pawn target, IntVec3 destination, DamageDef damageDef)
        {
            if (target.CurJobDef == RimKataDefOf.RimKata_Pushed) return false;
            var motion = (RimKataPushMotion)ThingMaker.MakeThing(RimKataDefOf.RimKata_PushMotion);
            motion.victim = target;
            motion.attacker = attacker;
            motion.start = target.DrawPos;
            motion.damageDef = damageDef;
            Rot4 facing = target.Rotation;
            GenSpawn.Spawn(motion, destination, target.Map);
            try
            {
                Job wait = JobMaker.MakeJob(RimKataDefOf.RimKata_Pushed, motion);
                // Native queuing defers orders; combat AI also respects this short forced job.
                wait.playerForced = true;
                target.jobs.StartJob(wait, JobCondition.InterruptForced, resumeCurJobAfterwards: true);
                if (!motion.OwnsWait) { motion.Destroy(); return false; }
                target.Position = destination;
                target.Notify_Teleported(false, true);
                target.Rotation = facing;
                motion.end = target.DrawPos;
                motion.Publish();
                return true;
            }
            catch
            {
                motion.Destroy();
                throw;
            }
        }

        protected override void TickInterval(int delta)
        {
            if (victim?.Spawned != true || victim.Map != Map)
            {
                // This is cleanup of the one known victim, never a corpse render lookup.
                Corpse corpse = victim?.Dead == true ? victim.Corpse : null;
                if (corpse?.Spawned == true && corpse.Map == Map && corpse.Position == Position
                    && !Position.WalkableBy(Map, victim)) RimKataSubdueUtility.KillAndDiscard(victim);
                Destroy();
                return;
            }
            elapsed = Math.Min(Duration, elapsed + delta);
            visual?.SetElapsed(elapsed);
            if (elapsed < Duration && OwnsWait) return;
            finishing = true;
            try
            {
                ClearVisual();
                bool discard = victim.Position == Position && !Position.WalkableBy(Map, victim);
                if (OwnsWait)
                    victim.jobs.EndCurrentJob(JobCondition.Succeeded, startNewJob: !discard);
                if (discard)
                    RimKataSubdueUtility.KillAndDiscard(victim,
                        new DamageInfo(damageDef, 0f, instigator: attacker));
            }
            finally { Destroy(); }
        }

        private void Publish()
        {
            visual = new RimKataPushVisual(start, end);
            visual.SetElapsed(elapsed);
            RimKataResponseVisualParticipantCache.PublishPush(victim, visual);
        }

        private void ClearVisual()
        {
            if (visual == null) return;
            RimKataResponseVisualParticipantCache.PublishPush(victim, null);
            visual = null;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (respawningAfterLoad)
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    if (Spawned && victim?.Spawned == true && victim.Map == Map && OwnsWait) Publish();
                });
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref victim, "pushVictim");
            Scribe_References.Look(ref attacker, "pushAttacker");
            Scribe_Values.Look(ref start, "pushStart");
            Scribe_Values.Look(ref end, "pushEnd");
            Scribe_Values.Look(ref elapsed, "pushElapsed");
            Scribe_Defs.Look(ref damageDef, "pushDamage");
        }

        public override void Notify_MyMapRemoved()
        {
            ClearVisual();
            base.Notify_MyMapRemoved();
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            ClearVisual();
            if (!finishing && OwnsWait && victim.Spawned && victim.Map == Map)
                victim.jobs.EndCurrentJob(JobCondition.InterruptForced);
            base.Destroy(mode);
        }
    }

    public sealed class JobDriver_RimKataPushed : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.AddEndCondition(() => job.targetA.Thing?.Spawned == true
                ? JobCondition.Ongoing : JobCondition.Incompletable);
            Toil wait = ToilMaker.MakeToil("RimKataPushMotion");
            wait.atomicWithPrevious = true;
            wait.handlingFacing = true;
            wait.defaultCompleteMode = ToilCompleteMode.Never;
            yield return wait;
        }
    }
}
