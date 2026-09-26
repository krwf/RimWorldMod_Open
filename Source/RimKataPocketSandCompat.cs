using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataPocketSandCompat
    {
        private static readonly MethodInfo PrimaryGetter = AccessTools.PropertyGetter(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));
        private static readonly MethodInfo MakeRoom = AccessTools.Method(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.MakeRoomFor),
            new[] { typeof(ThingWithComps) });
        private static readonly MethodInfo AddEquipment = AccessTools.Method(
            typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.AddEquipment));

        internal static void Apply()
        {
            Type driver = AccessTools.TypeByName("PocketSand.JobDriver_Equip");
            if (driver == null) return;
            var harmony = new Harmony("krwf.rimkata.pocketsand");
            try
            {
                MethodInfo exchange = null;
                foreach (MethodInfo method in AccessTools.GetDeclaredMethods(driver))
                {
                    if (method.IsStatic || method.ReturnType != typeof(void)
                        || method.GetParameters().Length != 0) continue;
                    int primaryCalls = 0, roomCalls = 0, addCalls = 0;
                    foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
                    {
                        if (instruction.Calls(PrimaryGetter)) primaryCalls++;
                        if (instruction.Calls(MakeRoom)) roomCalls++;
                        if (instruction.Calls(AddEquipment)) addCalls++;
                    }
                    // Match the final exchange action, not its generated method name.
                    if (primaryCalls != 1 || roomCalls != 1 || addCalls != 1) continue;
                    if (exchange != null)
                        throw new InvalidOperationException("Multiple PocketSand equipment exchanges matched.");
                    exchange = method;
                }
                if (exchange == null || !typeof(JobDriver).IsAssignableFrom(driver))
                    throw new InvalidOperationException("PocketSand equipment exchange API did not match.");
                harmony.Patch(exchange, transpiler: new HarmonyMethod(
                    typeof(RimKataPocketSandCompat), nameof(ExchangeTranspiler)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Warning("[RimKata] PocketSand equipment exchange integration was not applied.\n" + exception);
            }
        }

        private static IEnumerable<CodeInstruction> ExchangeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo primary = AccessTools.Method(typeof(RimKataPocketSandCompat), nameof(PrimaryAfterStoring));
            MethodInfo room = AccessTools.Method(typeof(RimKataPocketSandCompat), nameof(MakeRoomForReplacement));
            int primaryCalls = 0, roomCalls = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(PrimaryGetter))
                {
                    // Keep branch labels on the first replacement instruction.
                    var driver = new CodeInstruction(OpCodes.Ldarg_0);
                    driver.MoveLabelsFrom(instruction);
                    driver.MoveBlocksFrom(instruction);
                    yield return driver;
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = primary;
                    primaryCalls++;
                }
                else if (instruction.Calls(MakeRoom))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = room;
                    roomCalls++;
                }
                yield return instruction;
            }
            if (primaryCalls != 1 || roomCalls != 1)
                throw new InvalidOperationException("PocketSand equipment exchange calls changed.");
        }

        private static ThingWithComps PrimaryAfterStoring(Pawn_EquipmentTracker tracker, JobDriver driver)
        {
            return CanCompleteReplacement(tracker, driver.job?.targetA.Thing as ThingWithComps)
                ? null : tracker.Primary;
        }

        private static void MakeRoomForReplacement(Pawn_EquipmentTracker tracker, ThingWithComps incoming)
        {
            // AddEquipment consumes the existing handoff and validates both weapons.
            if (!CanCompleteReplacement(tracker, incoming)) tracker.MakeRoomFor(incoming);
        }

        private static bool CanCompleteReplacement(Pawn_EquipmentTracker tracker, ThingWithComps incoming)
        {
            return incoming != null && !incoming.Destroyed
                && incoming.def?.equipmentType == EquipmentType.Primary
                && incoming != tracker.Primary
                && RimKataSecondaryWeaponRegistry.CurrentRegistry
                    ?.HasPendingPrimaryReplacement(tracker.pawn) == true;
        }
    }
}
