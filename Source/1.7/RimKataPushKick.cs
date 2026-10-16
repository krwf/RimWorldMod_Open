using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataPushKick
    {
        internal static bool Apply(Pawn attacker, Pawn target, DamageDef damageDef, Job sourceJob)
        {
            if (attacker?.Spawned != true || target?.Spawned != true || target.Dead
                || target == attacker || target.Map != attacker.Map) return false;
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(attacker);
            if (settings?.pushKickEnabled != true) return false;
            double limit = RimKataStrengthUtility.BodyMass(attacker)
                * (settings.GetPushKickMassMultiplier(attacker) + (double)RimKataStrengthUtility.Bonus(attacker));
            if (RimKataStrengthUtility.BodyMass(target) > limit) return false;

            Map map = target.Map;
            IntVec3 origin = target.Position;
            IntVec3 away = origin - attacker.Position;
            IntVec3 step = new IntVec3(Math.Sign(away.x), 0, Math.Sign(away.z));
            if (step == IntVec3.Zero) step = attacker.Rotation.FacingCell;
            IntVec3 destination = origin + step;
            if (!destination.InBounds(map)) return false;
            if (StructureBlocks(map, target, destination)
                || step.x != 0 && step.z != 0
                    && (StructureBlocks(map, target, origin + new IntVec3(step.x, 0, 0))
                        || StructureBlocks(map, target, origin + new IntVec3(0, 0, step.z))))
            {
                target.stances?.stunner.StunFor(120, attacker);
                return false;
            }

            bool follow = IsOnlyCloseTarget(attacker, target, sourceJob);
            return RimKataPushMotion.Start(attacker, target, destination, damageDef) && follow;
        }

        private static bool IsOnlyCloseTarget(Pawn attacker, Pawn target, Job sourceJob)
        {
            if (sourceJob == null || attacker.CurJob != sourceJob
                || attacker.jobs.jobQueue.Count != 0 || sourceJob.forceSleep
                || sourceJob.playerForced && sourceJob.targetA.HasThing && sourceJob.targetA.Thing != target
                || !RimKataTargeting.IsPawnTargetStateValid(target)) return false;
            // This is the sole-candidate decision at the hit, not a search for the kicked pawn.
            // Stop at the first alternative in the attacker's Touch neighbourhood.
            foreach (IntVec3 offset in GenAdj.AdjacentCellsAndInside)
            {
                IntVec3 cell = attacker.Position + offset;
                if (!cell.InBounds(attacker.Map)) continue;
                var things = cell.GetThingList(attacker.Map);
                for (int i = 0; i < things.Count; i++)
                    if (things[i] is Pawn other && other != target && other != attacker
                        && RimKataTargeting.IsValidAutomaticAttackTarget(attacker, other)
                        && attacker.CanReachImmediate(other, PathEndMode.Touch)) return false;
            }
            return true;
        }

        internal static Job ContinueAttack(Pawn attacker, Pawn target, Job sourceJob)
        {
            // Called by the same kick after its native cast has returned, before new input.
            if (attacker.CurJob != sourceJob || !attacker.Spawned || attacker.Dead || attacker.Downed
                || attacker.InMentalState || attacker.jobs.jobQueue.Count != 0
                || !target.Spawned || target.Map != attacker.Map
                || !RimKataTargeting.IsPawnTargetStateValid(target)
                || !attacker.CanReach(target, PathEndMode.Touch, Danger.Deadly)) return null;
            if (sourceJob.def == JobDefOf.AttackMelee && sourceJob.targetA.Thing == target)
            {
                attacker.pather.StartPath(target, PathEndMode.Touch);
            }
            else
            {
                Job followup = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                followup.playerForced = sourceJob.playerForced;
                followup.killIncappedTarget = sourceJob.killIncappedTarget;
                attacker.jobs.StartJob(followup, JobCondition.InterruptForced, cancelBusyStances: false);
            }
            // StartJob callbacks can replace the requested job. Pass only the actual handoff.
            Job current = attacker.CurJob;
            return current?.targetA.Thing == target
                && (current.def == JobDefOf.AttackMelee || current.def == RimKataDefOf.RimKata_Attack)
                ? current : null;
        }

        internal static bool StructureBlocks(Map map, Pawn target, IntVec3 cell)
        {
            var things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
                if (things[i] is Building building
                    && (building.BlocksPawn(target) || building is Building_Door door && !door.Open))
                    return true;
            return false;
        }
    }
}
