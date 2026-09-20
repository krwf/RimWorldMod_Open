using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedReload
    {
        private static readonly MethodInfo PrimaryGetter =
            AccessTools.PropertyGetter(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Primary));
        private static MethodInfo weaponGetter;

        internal static bool Apply(Harmony harmony, Type reloadDriverType)
        {
            if (harmony == null || reloadDriverType == null
                || !typeof(JobDriver).IsAssignableFrom(reloadDriverType))
            {
                return false;
            }

            MethodInfo equippedGetter = AccessTools.PropertyGetter(reloadDriverType, "weaponEquipped");
            MethodInfo failureCheck = AccessTools.Method(reloadDriverType, "HasNoGunOrAmmo", Type.EmptyTypes);
            weaponGetter = AccessTools.PropertyGetter(reloadDriverType, "weapon");
            if (equippedGetter == null || failureCheck == null || weaponGetter == null
                || equippedGetter.ReturnType != typeof(bool) || failureCheck.ReturnType != typeof(bool)
                || weaponGetter.ReturnType != typeof(ThingWithComps) || PrimaryGetter == null)
            {
                return false;
            }

            MethodInfo equippedPostfix = AccessTools.Method(typeof(RimKataCombatExtendedReload), nameof(WeaponEquippedPostfix));
            MethodInfo failureTranspiler = AccessTools.Method(typeof(RimKataCombatExtendedReload), nameof(FailureCheckTranspiler));
            try
            {
                List<CodeInstruction> equippedCodes = PatchProcessor.GetOriginalInstructions(equippedGetter);
                List<CodeInstruction> failureCodes = PatchProcessor.GetOriginalInstructions(failureCheck);
                if (CountCalls(equippedCodes, PrimaryGetter) != 1
                    || CountCalls(equippedCodes, weaponGetter) != 1
                    || FindEquipmentComparison(failureCodes) < 0)
                {
                    return false;
                }

                harmony.Patch(failureCheck, transpiler: new HarmonyMethod(failureTranspiler));
                harmony.Patch(equippedGetter, postfix: new HarmonyMethod(equippedPostfix));
                return true;
            }
            catch (Exception exception)
            {
                harmony.Unpatch(equippedGetter, equippedPostfix);
                harmony.Unpatch(failureCheck, failureTranspiler);
                Log.Warning("[RimKata] CE secondary reload compatibility could not be applied: " + exception.Message);
                return false;
            }
        }

        private static void WeaponEquippedPostfix(JobDriver __instance, ref bool __result)
        {
            if (!__result)
            {
                __result = RimKataCombatExtendedCompat.IsHeldSecondary(
                    __instance.pawn, __instance.job?.targetB.Thing as ThingWithComps);
            }
        }

        private static IEnumerable<CodeInstruction> FailureCheckTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int comparisonIndex = FindEquipmentComparison(codes);
            if (comparisonIndex < 0)
            {
                throw new InvalidOperationException("CE reload equipment comparison does not match the supported shape.");
            }

            // Extend only the equipped-weapon equality check. CE still verifies that the
            // primary exists and has not changed, that inventory weapons remain held,
            // and that ammunition is available; its reload toils remain untouched.
            CodeInstruction branch = codes[comparisonIndex];
            CodeInstruction loadDriver = new CodeInstruction(OpCodes.Ldarg_0);
            loadDriver.labels.AddRange(branch.labels);
            branch.labels.Clear();
            branch.opcode = OpCodes.Brfalse;
            codes.Insert(comparisonIndex, loadDriver);
            codes.Insert(comparisonIndex + 1, new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(RimKataCombatExtendedReload), nameof(IsReloadEquipment))));
            return codes;
        }

        private static bool IsReloadEquipment(ThingWithComps primary, ThingWithComps weapon, JobDriver driver)
        {
            return primary == weapon || RimKataCombatExtendedCompat.IsHeldSecondary(driver.pawn, weapon);
        }

        private static int FindEquipmentComparison(List<CodeInstruction> codes)
        {
            // The first primary read is CE's null guard; the second compares target B;
            // the third compares the starting primary and must never be redirected.
            if (CountCalls(codes, PrimaryGetter) != 3)
            {
                return -1;
            }

            int primaryRead = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!Calls(codes[i], PrimaryGetter) || ++primaryRead != 2)
                {
                    continue;
                }

                if (i + 3 < codes.Count
                    && codes[i + 1].opcode == OpCodes.Ldarg_0
                    && Calls(codes[i + 2], weaponGetter)
                    && (codes[i + 3].opcode == OpCodes.Bne_Un || codes[i + 3].opcode == OpCodes.Bne_Un_S)
                    && codes[i + 3].blocks.Count == 0)
                {
                    return i + 3;
                }
                return -1;
            }
            return -1;
        }

        private static int CountCalls(List<CodeInstruction> codes, MethodInfo method)
        {
            int count = 0;
            foreach (CodeInstruction code in codes)
            {
                if (Calls(code, method))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool Calls(CodeInstruction code, MethodInfo method)
        {
            return (code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt)
                && Equals(code.operand, method);
        }
    }
}
