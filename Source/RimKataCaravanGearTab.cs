using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    [HarmonyPatch(typeof(WITab_Caravan_Gear), "TryEquipDraggedItem")]
    internal static class Patch_WITabCaravanGear_RimKataEquipSecondary
    {
        private static readonly AccessTools.FieldRef<WITab_Caravan_Gear, Thing> DraggedItem =
            AccessTools.FieldRefAccess<WITab_Caravan_Gear, Thing>("draggedItem");
        private static readonly AccessTools.FieldRef<WITab_Caravan_Gear, bool> DroppedItem =
            AccessTools.FieldRefAccess<WITab_Caravan_Gear, bool>("droppedDraggedItem");

        public static bool Prefix(WITab_Caravan_Gear __instance, Pawn __0)
        {
            Pawn pawn = __0;
            // Let vanilla choose the pawn from its entire row, then route that
            // equip request through the available secondary slot.
            if (!(DraggedItem(__instance) is ThingWithComps weapon)
                || weapon.def?.IsWeapon != true
                || !RimKataCaravanEquipment.CanUseSlot(pawn))
                return true;

            DraggedItem(__instance) = null;
            DroppedItem(__instance) = false;
            if (!RimKataCaravanEquipment.CanEquip(pawn, weapon, out string reason))
            {
                Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                return false;
            }

            Action equip = () =>
            {
                // The caravan or source owner can change while a persona-weapon
                // confirmation is open. Validate again before moving anything.
                if (!RimKataCaravanEquipment.CanEquip(pawn, weapon, out string currentReason))
                    Messages.Message(currentReason, MessageTypeDefOf.RejectInput, false);
                else if (!RimKataCaravanEquipment.TryEquip(pawn, weapon))
                    Messages.Message("CannotEquip".Translate(weapon.LabelShort),
                        MessageTypeDefOf.RejectInput, false);
            };
            string confirmation = EquipmentUtility.GetPersonaWeaponConfirmationText(weapon, pawn);
            if (confirmation.NullOrEmpty())
                equip();
            else
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    confirmation, equip, false, null, WindowLayer.Dialog));
            return false;
        }
    }

    [HarmonyPatch(typeof(WITab_Caravan_Gear), "DoPawnRow", new[] { typeof(Rect), typeof(Pawn) })]
    internal static class Patch_WITabCaravanGear_RimKataSecondarySlot
    {
        private const float IconSize = 32f;
        private static bool warned;

        private static void DrawSecondarySlot(Pawn pawn, ref float x)
        {
            if (!RimKataCaravanEquipment.CanUseSlot(pawn)
                || RimKataCaravanEquipment.HeldSecondary(pawn) != null)
                return;

            // An empty-slot indicator, not a separate drop target.
            Rect slot = new Rect(x, 4f, IconSize, IconSize);
            TextAnchor oldAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(slot, "+");
            Text.Anchor = oldAnchor;
            TooltipHandler.TipRegion(slot, "KRWF_RimKata_InspectSlotSecondary".Translate());
            x += IconSize;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo drawGear = AccessTools.Method(typeof(WITab_Caravan_Gear), "DoEquippedGear");
            FieldInfo apparel = AccessTools.Field(typeof(Pawn), nameof(Pawn.apparel));
            CodeInstruction cursor = null;
            int insertAt = -1;
            for (int i = 1; i < code.Count; i++)
            {
                if (code[i].Calls(drawGear) && cursor == null
                    && (code[i - 1].opcode == OpCodes.Ldloca_S || code[i - 1].opcode == OpCodes.Ldloca))
                    cursor = new CodeInstruction(code[i - 1].opcode, code[i - 1].operand);
                if (code[i].opcode == OpCodes.Ldfld && Equals(code[i].operand, apparel))
                {
                    if (code[i - 1].opcode == OpCodes.Ldarg_2 && cursor != null)
                        insertAt = i - 1;
                    break;
                }
            }
            if (insertAt < 0)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("[RimKata] Could not locate the caravan gear slot; the empty secondary indicator is unavailable.");
                }
                return code;
            }

            var first = new CodeInstruction(OpCodes.Ldarg_2);
            first.MoveLabelsFrom(code[insertAt]);
            first.MoveBlocksFrom(code[insertAt]);
            code.InsertRange(insertAt, new[]
            {
                first,
                cursor,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(
                    typeof(Patch_WITabCaravanGear_RimKataSecondarySlot), nameof(DrawSecondarySlot)))
            });
            return code;
        }
    }
}
