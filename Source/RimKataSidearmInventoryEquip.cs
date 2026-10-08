using RimWorld;
using Verse;
using Verse.Sound;

namespace KRWF.RimKata
{
    internal static class RimKataSidearmInventoryEquip
    {
        internal static void TryEquip(Pawn pawn, ThingWithComps weapon)
        {
            if (!CanEquipFromInventory(pawn, weapon)) return;

            string confirmation = EquipmentUtility.GetPersonaWeaponConfirmationText(weapon, pawn);
            if (confirmation.NullOrEmpty())
            {
                EquipFromInventory(pawn, weapon);
            }
            else
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(confirmation, delegate
                {
                    // Ownership and equipment conditions may change while the dialog is open.
                    if (CanEquipFromInventory(pawn, weapon)) EquipFromInventory(pawn, weapon);
                }, false, null, WindowLayer.Dialog));
            }
        }

        private static bool CanEquipFromInventory(Pawn pawn, ThingWithComps weapon)
        {
            return pawn?.Spawned == true && pawn.IsPlayerControlled
                && !pawn.Dead && !pawn.Downed && pawn.equipment != null
                && pawn.inventory != null && weapon != null && !weapon.Destroyed
                && weapon.holdingOwner == pawn.inventory.innerContainer
                && RimKataWeaponSlotUtility.CanEquipAsSecondary(pawn, weapon)
                && !RimKataSecondaryHandRequirement.HasMissingHand(pawn)
                && (!weapon.def.IsWeapon || !pawn.WorkTagIsDisabled(WorkTags.Violent))
                && (!weapon.def.IsRangedWeapon || !pawn.WorkTagIsDisabled(WorkTags.Shooting))
                && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
                && (!pawn.IsQuestLodger() || EquipmentUtility.QuestLodgerCanEquip(weapon, pawn))
                && EquipmentUtility.CanEquip(weapon, pawn);
        }

        private static void EquipFromInventory(Pawn pawn, ThingWithComps source)
        {
            ThingOwner inventory = pawn.inventory.innerContainer;
            ThingWithComps primary = pawn.equipment.Primary;
            ThingWithComps previous = RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn);
            ThingWithComps weapon = null;
            bool equipped = false;
            try
            {
                // Inventory switching stores the old offhand first. The shared equip
                // method otherwise drops it, as appropriate for a ground equip job.
                // Do not merge: retain the original instance for rollback if needed.
                if (previous != null && !inventory.TryAddOrTransfer(previous, false)) return;

                if (source.stackCount > 1)
                    weapon = (ThingWithComps)source.SplitOff(1);
                else if (inventory.Remove(source))
                    weapon = source;
                else
                    return;

                equipped = RimKataWeaponSlotUtility.TryEquipSecondary(pawn, weapon);
                if (equipped)
                    weapon.def.soundInteract?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            }
            finally
            {
                if (!equipped)
                {
                    ReturnToInventory(pawn, weapon);
                    RestorePreviousSecondary(pawn, primary, previous);
                }
            }
        }

        private static void RestorePreviousSecondary(Pawn pawn, ThingWithComps primary, ThingWithComps previous)
        {
            if (previous == null || previous.Destroyed || pawn.equipment.Primary != primary
                || RimKataWeaponSlotUtility.SecondaryWeaponWithVerifiedAccess(pawn) != null
                || RimKataSecondaryHandRequirement.HasMissingHand(pawn)
                || !EquipmentUtility.CanEquip(previous, pawn)
                || !RimKataWeaponSlotUtility.CanEquipAsSecondary(pawn, previous)) return;

            if (previous.holdingOwner == pawn.equipment.GetDirectlyHeldThings())
            {
                // ThingOwner may already have returned a rejected transfer to equipment.
                // Its remove notification cleared the offhand registration meanwhile.
                RimKataSecondaryWeaponRegistry.CurrentRegistry?.Set(pawn, previous, false);
                RimKataWeaponSlotUtility.NotifyLoadoutChanged(pawn);
            }
            else if (previous.holdingOwner == pawn.inventory.innerContainer
                && pawn.inventory.innerContainer.Remove(previous)
                && !RimKataWeaponSlotUtility.TryEquipSecondary(pawn, previous))
            {
                ReturnToInventory(pawn, previous);
            }
        }

        private static void ReturnToInventory(Pawn pawn, ThingWithComps weapon)
        {
            if (weapon != null && !weapon.Destroyed && weapon.holdingOwner == null
                && !pawn.inventory.innerContainer.TryAddOrTransfer(weapon, true))
                GenPlace.TryPlaceThing(weapon, pawn.Position, pawn.Map, ThingPlaceMode.Near);
        }
    }
}
