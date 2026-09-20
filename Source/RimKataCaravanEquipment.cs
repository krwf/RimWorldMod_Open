using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCaravanEquipment
    {
        // UI reads must never promote, unregister, or move equipment.
        internal static ThingWithComps HeldSecondary(Pawn pawn)
        {
            ThingWithComps secondary = RimKataSecondaryWeaponRegistry.CurrentRegistry?.GetRegistered(pawn);
            return secondary != null && !secondary.Destroyed
                && pawn?.equipment != null && pawn.equipment.Primary != secondary
                && secondary.holdingOwner == pawn.equipment.GetDirectlyHeldThings()
                && pawn.equipment.AllEquipmentListForReading.Contains(secondary)
                    ? secondary : null;
        }

        internal static bool CanUseSlot(Pawn pawn)
        {
            return pawn != null && !pawn.Spawned && !pawn.Dead && !pawn.Downed
                && pawn.equipment != null && pawn.GetCaravan() != null
                && !RimKataSecondaryHandRequirement.HasMissingHand(pawn)
                && RimKataWeaponSlotUtility.CanUseSecondarySlot(pawn);
        }

        internal static bool CanEquip(Pawn pawn, ThingWithComps weapon, out string reason)
        {
            reason = "CannotEquip".Translate(weapon?.LabelShort ?? string.Empty);
            if (!CanUseSlot(pawn) || weapon == null || weapon.Destroyed || weapon.Spawned
                || weapon.stackCount < 1 || weapon.def?.equipmentType != EquipmentType.Primary
                || weapon == pawn.equipment.Primary || weapon == HeldSecondary(pawn)
                || !RimKataEquipmentUtility.IsWeaponEnabled(weapon.def)
                || RimKataGripUtility.GripTypeFor(weapon.def) != RimKataGripType.OneHand
                || weapon.TryGetComp<CompEquippable>() == null
                || pawn.IsPrisoner || pawn.WorkTagIsDisabled(WorkTags.Violent)
                || (weapon.def.IsRangedWeapon && pawn.WorkTagIsDisabled(WorkTags.Shooting))
                || pawn.health?.capacities?.CapableOf(PawnCapacityDefOf.Manipulation) != true)
            {
                if (pawn != null && RimKataSecondaryHandRequirement.HasMissingHand(pawn))
                    reason = "KRWF_RimKata_SecondaryMissingHand".Translate();
                return false;
            }

            ThingWithComps secondary = HeldSecondary(pawn);
            if (secondary != null && (!RimKataEquipmentUtility.IsWeaponEnabled(secondary.def)
                || RimKataGripUtility.GripTypeFor(secondary.def) != RimKataGripType.OneHand))
                return false;
            Caravan caravan = pawn.GetCaravan();
            if (FindDirectOwner(caravan, weapon.holdingOwner) == null)
                return false;
            // Keep vanilla's existing bond restrictions; any confirmation still
            // belongs to the gear UI before this transaction.
            if (!EquipmentUtility.CanEquip(weapon, pawn, out string vanillaReason, checkBonded: true))
            {
                reason = "MessageCantEquipCustom".Translate(vanillaReason);
                return false;
            }
            reason = null;
            return true;
        }

        internal static bool TryEquip(Pawn pawn, ThingWithComps weapon)
        {
            if (!CanEquip(pawn, weapon, out _))
                return false;

            RimKataSecondaryWeaponRegistry registry = RimKataSecondaryWeaponRegistry.CurrentRegistry;
            if (registry == null)
                return false;
            Caravan caravan = pawn.GetCaravan();
            ThingOwner source = weapon.holdingOwner;
            Pawn sourcePawn = FindDirectOwner(caravan, source);
            ThingOwner destination = pawn.equipment.GetDirectlyHeldThings();
            ThingWithComps primary = pawn.equipment.Primary;
            ThingWithComps secondary = HeldSecondary(pawn);
            Pawn cargoPawn = secondary == null ? null
                : CaravanInventoryUtility.FindPawnToMoveInventoryTo(
                    primary, caravan.PawnsListForReading, null, null);
            ThingOwner cargo = cargoPawn?.inventory?.innerContainer;
            if (sourcePawn == null || (secondary != null && cargo == null))
                return false;

            var destinationOrder = new List<ThingWithComps>(pawn.equipment.AllEquipmentListForReading);
            var sourceOrder = sourcePawn == pawn || sourcePawn.equipment == null ? null
                : new List<ThingWithComps>(sourcePawn.equipment.AllEquipmentListForReading);
            ThingWithComps sourceSecondary = sourcePawn == pawn ? secondary : HeldSecondary(sourcePawn);
            registry.CancelPrimaryReplacement(pawn);
            registry.CancelPrimaryReplacement(sourcePawn);
            Thing movedIncoming = null;
            Thing movedPrimary = null;
            bool committed = false;
            try
            {
                if (source.TryTransferToContainer(weapon, destination, 1, out movedIncoming, false) != 1
                    || !(movedIncoming is ThingWithComps incoming)
                    || incoming.holdingOwner != destination)
                    return false;

                if (secondary != null)
                {
                    if (destination.TryTransferToContainer(primary, cargo, 1, out movedPrimary, false) != 1
                        || movedPrimary != primary || primary.holdingOwner != cargo)
                        return false;
                }

                ThingWithComps expectedPrimary = secondary ?? primary;
                List<ThingWithComps> equipment = pawn.equipment.AllEquipmentListForReading;
                int primaryIndex = equipment.IndexOf(expectedPrimary);
                int incomingIndex = equipment.IndexOf(incoming);
                if (primaryIndex < 0 || incomingIndex < 0)
                    return false;
                if (incomingIndex < primaryIndex)
                {
                    equipment.RemoveAt(primaryIndex);
                    equipment.Insert(incomingIndex, expectedPrimary);
                }
                if (pawn.equipment.Primary != expectedPrimary)
                    return false;

                registry.Set(pawn, incoming, false);
                if (pawn.mindState != null)
                    pawn.mindState.droppedWeapon = null;
                committed = true;
                return true;
            }
            finally
            {
                if (!committed)
                {
                    bool restoredPrimary = RestoreToOwner(movedPrimary, destination);
                    bool restoredIncoming = RestoreToOwner(movedIncoming, source);
                    if (restoredPrimary && movedPrimary != null && movedPrimary != primary
                        && primary.holdingOwner == destination && !primary.Destroyed
                        && movedPrimary.holdingOwner == destination)
                        primary.TryAbsorbStack(movedPrimary, true);
                    // A count-one transfer can split a stack. On rollback reunite
                    // that piece with its original, still-owned stack when possible.
                    if (restoredIncoming && movedIncoming != null && movedIncoming != weapon
                        && weapon.holdingOwner == source && !weapon.Destroyed
                        && movedIncoming.holdingOwner == source)
                        weapon.TryAbsorbStack(movedIncoming, true);
                    RestoreOrder(pawn, destinationOrder);
                    if (sourceOrder != null)
                        RestoreOrder(sourcePawn, sourceOrder);
                    RestoreRegistration(registry, pawn, secondary);
                    if (sourcePawn != pawn)
                        RestoreRegistration(registry, sourcePawn, sourceSecondary);
                    if (!restoredPrimary || !restoredIncoming)
                        Log.Error("[RimKata] Caravan equipment transfer rollback was rejected by an item owner; the items remain in their current containers.");
                }
                NotifyEquipmentChanged(pawn);
                if (sourcePawn != pawn)
                    NotifyEquipmentChanged(sourcePawn);
                RimKataWeaponSlotUtility.NotifyLoadoutChanged(pawn);
                if (sourcePawn != pawn)
                    RimKataWeaponSlotUtility.NotifyLoadoutChanged(sourcePawn);
            }
        }

        internal static void NotifyEquipmentChanged(Pawn pawn)
        {
            if (pawn == null || pawn.Spawned || pawn.GetCaravan() == null)
                return;
            RimKataSecondaryWeaponRegistry registry = RimKataSecondaryWeaponRegistry.CurrentRegistry;
            if (registry == null)
                return;
            registry.CancelPrimaryReplacement(pawn);
            ThingWithComps secondary = registry.GetRegistered(pawn);
            if (secondary != null && HeldSecondary(pawn) == null)
                registry.Clear(pawn, secondary, false);
        }

        private static Pawn FindDirectOwner(Caravan caravan, ThingOwner owner)
        {
            if (caravan == null || owner == null)
                return null;
            List<Pawn> pawns = caravan.PawnsListForReading;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn candidate = pawns[i];
                if (candidate.inventory?.innerContainer == owner
                    || candidate.equipment?.GetDirectlyHeldThings() == owner)
                    return candidate;
            }
            return null;
        }

        private static bool RestoreToOwner(Thing thing, ThingOwner owner)
        {
            if (thing == null)
                return true;
            if (thing.Destroyed || owner == null)
                return false;
            if (thing.holdingOwner == owner)
                return true;
            ThingOwner currentOwner = thing.holdingOwner;
            if (currentOwner == null)
                return owner.TryAdd(thing, false);
            int count = thing.stackCount;
            return currentOwner.TryTransferToContainer(thing, owner, count,
                out Thing restored, false) == count && restored == thing
                && thing.holdingOwner == owner;
        }

        private static void RestoreOrder(Pawn pawn, List<ThingWithComps> original)
        {
            List<ThingWithComps> current = pawn.equipment.AllEquipmentListForReading;
            for (int i = original.Count - 1; i >= 0; i--)
            {
                ThingWithComps weapon = original[i];
                int index = current.IndexOf(weapon);
                if (index < 0)
                    continue;
                current.RemoveAt(index);
                current.Insert(0, weapon);
            }
        }

        private static void RestoreRegistration(RimKataSecondaryWeaponRegistry registry,
            Pawn pawn, ThingWithComps secondary)
        {
            if (secondary != null && !secondary.Destroyed && pawn.equipment != null
                && secondary.holdingOwner == pawn.equipment.GetDirectlyHeldThings()
                && pawn.equipment.Primary != secondary)
                registry.Set(pawn, secondary, false);
            else
                registry.Clear(pawn, null, false);
        }
    }
}
