using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    [HarmonyPatch(typeof(TransferableOneWayWidget), "DoRow")]
    internal static class Patch_TransferableOneWayWidget_RimKataFormationWeapons
    {
        private const float WeaponColumnWidth = 30f;

        private delegate void DrawOriginalWeapon(
            TransferableOneWayWidget widget, Rect columnRect, Rect iconRect,
            TransferableOneWay transferable);

        private static DrawOriginalWeapon drawOriginalWeapon;
        private static bool warned;

        public static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var original = new List<CodeInstruction>(instructions);
            MethodInfo drawWeapon = AccessTools.Method(typeof(TransferableOneWayWidget),
                "DrawEquippedWeapon", new[] { typeof(Rect), typeof(Rect), typeof(TransferableOneWay) });
            int callIndex = -1;
            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction instruction = original[i];
                if ((instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
                    || !Equals(instruction.operand, drawWeapon))
                    continue;

                if (callIndex >= 0)
                    return KeepOriginal(original);
                callIndex = i;
            }

            // The existing call is inside drawEquippedWeapon's visibility gate.
            // Its following cursor subtraction reserves the native 30px column.
            // Identify that cursor instead of assuming a particular local number.
            if (drawWeapon == null || callIndex < 0 || callIndex + 4 >= original.Count
                || original[callIndex].blocks.Count != 0
                || !TryGetLocalIndex(original[callIndex + 1], false, out int cursorIndex)
                || original[callIndex + 2].opcode != OpCodes.Ldc_R4
                || !(original[callIndex + 2].operand is float width)
                || width != WeaponColumnWidth
                || original[callIndex + 3].opcode != OpCodes.Sub
                || !TryGetLocalIndex(original[callIndex + 4], true, out int storedIndex)
                || cursorIndex != storedIndex)
                return KeepOriginal(original);

            MethodBody body = __originalMethod?.GetMethodBody();
            if (body == null || cursorIndex < 0 || cursorIndex >= body.LocalVariables.Count
                || body.LocalVariables[cursorIndex].LocalType != typeof(float))
                return KeepOriginal(original);

            try
            {
                drawOriginalWeapon = (DrawOriginalWeapon)Delegate.CreateDelegate(
                    typeof(DrawOriginalWeapon), drawWeapon);
            }
            catch (Exception)
            {
                return KeepOriginal(original);
            }

            MethodInfo replacement = AccessTools.Method(
                typeof(Patch_TransferableOneWayWidget_RimKataFormationWeapons),
                nameof(DrawEquippedWeapons));
            if (replacement == null)
                return KeepOriginal(original);

            // The original instance and three arguments are already on the stack.
            // Add the cursor reference for this static replacement, leaving the
            // original subtraction and the remaining row layout unchanged.
            CodeInstruction loadCursor = cursorIndex <= byte.MaxValue
                ? new CodeInstruction(OpCodes.Ldloca_S, (byte)cursorIndex)
                : new CodeInstruction(OpCodes.Ldloca, (short)cursorIndex);
            loadCursor.MoveLabelsFrom(original[callIndex]);
            var patched = new List<CodeInstruction>(original.Count + 1);
            for (int i = 0; i < original.Count; i++)
            {
                if (i == callIndex)
                {
                    patched.Add(loadCursor);
                    patched.Add(new CodeInstruction(OpCodes.Call, replacement));
                }
                else
                {
                    patched.Add(original[i]);
                }
            }
            return patched;
        }

        private static void DrawEquippedWeapons(
            TransferableOneWayWidget widget, Rect columnRect, Rect iconRect,
            TransferableOneWay transferable, ref float remainingWidth)
        {
            // Reserve the same two columns on every row, including pawns with
            // only a primary weapon. The right-hand column is secondary-only.
            Rect primaryColumnRect = columnRect;
            Rect primaryIconRect = iconRect;
            primaryColumnRect.x -= WeaponColumnWidth;
            primaryIconRect.x -= WeaponColumnWidth;
            drawOriginalWeapon(widget, primaryColumnRect, primaryIconRect, transferable);
            remainingWidth -= WeaponColumnWidth;

            if (transferable?.HasAnyThing != true
                || !(transferable.AnyThing is Pawn pawn)
                || pawn.equipment == null)
                return;

            ThingWithComps secondary = RimKataSecondaryWeaponRegistry.CurrentRegistry
                ?.GetRegistered(pawn);
            if (secondary == null || secondary.Destroyed
                || secondary == pawn.equipment.Primary
                || !pawn.equipment.AllEquipmentListForReading.Contains(secondary)
                || !RimKataEligibility.HasRimKataAccess(pawn))
                return;

            Widgets.DrawHighlightIfMouseover(columnRect);
            Widgets.ThingIcon(iconRect, secondary);
            if (Mouse.IsOver(columnRect))
                TooltipHandler.TipRegion(columnRect, secondary.LabelCap);
        }

        private static bool TryGetLocalIndex(CodeInstruction instruction, bool store, out int index)
        {
            OpCode opcode = instruction.opcode;
            if (opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) index = 0;
            else if (opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) index = 1;
            else if (opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) index = 2;
            else if (opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) index = 3;
            else if (opcode == (store ? OpCodes.Stloc : OpCodes.Ldloc)
                || opcode == (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S))
            {
                if (instruction.operand is LocalBuilder local) index = local.LocalIndex;
                else if (instruction.operand is LocalVariableInfo variable) index = variable.LocalIndex;
                else if (instruction.operand is int integer) index = integer;
                else if (instruction.operand is byte small) index = small;
                else if (instruction.operand is short wide) index = wide;
                else
                {
                    index = -1;
                    return false;
                }
            }
            else
            {
                index = -1;
                return false;
            }
            return true;
        }

        private static IEnumerable<CodeInstruction> KeepOriginal(List<CodeInstruction> instructions)
        {
            if (!warned)
            {
                warned = true;
                Log.Warning("[RimKata] Could not locate the caravan formation weapon column; "
                    + "keeping the original weapon icons and row layout.");
            }
            return instructions;
        }
    }
}
