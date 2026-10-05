using System;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataPushKick
    {
        internal static void Apply(Pawn attacker, Pawn target, DamageDef damageDef)
        {
            if (attacker?.Spawned != true || target?.Spawned != true || target.Dead
                || target == attacker || target.Map != attacker.Map) return;
            RimKataSettings settings = RimKataTargetAccess.SettingsFor(attacker);
            if (settings?.pushKickEnabled != true) return;
            double limit = RimKataStrengthUtility.BodyMass(attacker)
                * (settings.GetPushKickMassMultiplier(attacker) + (double)RimKataStrengthUtility.Bonus(attacker));
            if (RimKataStrengthUtility.BodyMass(target) > limit) return;

            Map map = target.Map;
            IntVec3 origin = target.Position;
            IntVec3 away = origin - attacker.Position;
            IntVec3 step = new IntVec3(Math.Sign(away.x), 0, Math.Sign(away.z));
            if (step == IntVec3.Zero) step = attacker.Rotation.FacingCell;
            IntVec3 destination = origin + step;
            if (!destination.InBounds(map)) return;
            if (StructureBlocks(map, target, destination)
                || step.x != 0 && step.z != 0
                    && (StructureBlocks(map, target, origin + new IntVec3(step.x, 0, 0))
                        || StructureBlocks(map, target, origin + new IntVec3(0, 0, step.z))))
            {
                target.stances?.stunner.StunFor(120, attacker);
                return;
            }

            RimKataThrownPawn.LaunchPush(attacker, target, step, damageDef);
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
