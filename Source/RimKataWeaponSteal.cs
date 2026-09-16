using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    public sealed class RimKataWeaponStealMapComponent : MapComponent
    {
        private sealed class PendingWeaponSteal : IExposable
        {
            public Pawn defender;
            public ThingWithComps weapon;
            public IntVec3 dropPosition;
            public int dueTick;

            public void ExposeData()
            {
                Scribe_References.Look(ref defender, "defender");
                Scribe_References.Look(ref weapon, "weapon");
                Scribe_Values.Look(ref dropPosition, "dropPosition");
                Scribe_Values.Look(ref dueTick, "dueTick");
            }
        }

        private List<PendingWeaponSteal> pending = new List<PendingWeaponSteal>();

        public RimKataWeaponStealMapComponent(Map map) : base(map)
        {
        }

        internal static bool TrySchedule(
            Pawn defender,
            Pawn attacker,
            Verb attackingVerb,
            out bool allowDisarm)
        {
            allowDisarm = defender?.equipment != null;
            if (defender?.Spawned != true
                || attacker?.Spawned != true
                || attacker.Map != defender.Map
                || !IsUnarmed(defender))
            {
                return false;
            }

            ThingWithComps weapon = attackingVerb != null
                ? attackingVerb.EquipmentSource as ThingWithComps
                : attacker.equipment?.Primary;
            if (weapon == null
                || attacker.equipment?.AllEquipmentListForReading?.Contains(weapon) != true)
            {
                return false;
            }

            // An unarmed parry must not drop a weapon the defender cannot equip,
            // even when stealing is disabled or its chance roll would fail.
            allowDisarm = CanWield(defender, weapon);
            if (!allowDisarm)
            {
                return false;
            }

            RimKataSettings settings = RimKataTargetAccess.SettingsFor(defender);
            float chance = settings?.UnarmedWeaponStealChance ?? 0f;
            if (chance <= 0f
                || !defender.CanReachImmediate(attacker, PathEndMode.Touch))
            {
                return false;
            }

            RimKataWeaponStealMapComponent component =
                defender.Map.GetComponent<RimKataWeaponStealMapComponent>();
            if (component == null || component.HasPending(defender, weapon)
                || (chance < 1f && !Rand.Chance(chance)))
            {
                return false;
            }

            if (!attacker.equipment.TryDropEquipment(
                    weapon, out ThingWithComps dropped, defender.Position, false)
                || dropped?.Spawned != true
                || dropped.Map != defender.Map)
            {
                return false;
            }

            // Keep the real item on the map until the attacking verb has returned.
            // Re-equipping here would change that verb's caster and reset it mid-cast.
            component.pending.Add(new PendingWeaponSteal
            {
                defender = defender,
                weapon = dropped,
                dropPosition = dropped.Position,
                dueTick = Find.TickManager.TicksGame + 1
            });
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pendingWeaponSteals", LookMode.Deep);
            if (pending == null)
            {
                pending = new List<PendingWeaponSteal>();
            }
        }

        public override void MapComponentTick()
        {
            if (pending.Count == 0)
            {
                return;
            }

            int tick = Find.TickManager.TicksGame;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingWeaponSteal entry = pending[i];
                if (entry != null && tick < entry.dueTick)
                {
                    continue;
                }

                pending.RemoveAt(i);
                if (entry != null)
                {
                    TryComplete(entry);
                }
            }
        }

        private bool HasPending(Pawn defender, ThingWithComps weapon)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                PendingWeaponSteal entry = pending[i];
                if (entry != null && (entry.defender == defender || entry.weapon == weapon))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsUnarmed(Pawn pawn)
        {
            List<ThingWithComps> equipment = pawn?.equipment?.AllEquipmentListForReading;
            if (equipment == null)
            {
                return false;
            }

            for (int i = 0; i < equipment.Count; i++)
            {
                ThingDef def = equipment[i]?.def;
                if (def?.IsWeapon == true || def?.equipmentType == EquipmentType.Primary)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool CanWield(Pawn defender, ThingWithComps weapon)
        {
            return defender?.equipment != null
                && weapon != null
                && !weapon.Destroyed
                && weapon.stackCount == 1
                && weapon.def?.IsWeapon == true
                && weapon.def.equipmentType == EquipmentType.Primary
                && weapon.TryGetComp<CompEquippable>() != null
                && RimKataEquipmentUtility.IsWeaponEnabled(weapon.def)
                && !defender.WorkTagIsDisabled(WorkTags.Violent)
                && (!weapon.def.IsRangedWeapon || !defender.WorkTagIsDisabled(WorkTags.Shooting))
                && defender.health?.capacities?.CapableOf(PawnCapacityDefOf.Manipulation) == true
                && EquipmentUtility.QuestLodgerCanEquip(weapon, defender)
                && EquipmentUtility.CanEquip(weapon, defender);
        }

        private void TryComplete(PendingWeaponSteal entry)
        {
            Pawn defender = entry.defender;
            ThingWithComps weapon = entry.weapon;
            if (defender?.Spawned != true
                || defender.Map != map
                || !IsUnarmed(defender)
                || !RimKataEligibility.CanUseMeleeResponse(defender)
                || RimKataTargetAccess.SettingsFor(defender)?.responseEnabled == false
                || weapon?.Spawned != true
                || weapon.Map != map
                || weapon.Position != entry.dropPosition
                || !CanWield(defender, weapon)
                || !defender.CanReachImmediate(weapon, PathEndMode.Touch)
                || !defender.CanReserve(weapon, 1, 1))
            {
                return;
            }

            weapon.DeSpawn();
            try
            {
                defender.equipment.AddEquipment(weapon);
            }
            finally
            {
                // A rejected equip must leave the same item available on the map.
                if (!weapon.Destroyed && !weapon.Spawned && weapon.ParentHolder == null
                    && !GenPlace.TryPlaceThing(weapon, entry.dropPosition, map, ThingPlaceMode.Near))
                {
                    GenSpawn.Spawn(weapon, entry.dropPosition, map);
                }
            }
        }
    }
}
