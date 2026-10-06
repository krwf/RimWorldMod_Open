using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataCombatExtendedDirectionalFire
    {
        internal static void Apply(Harmony harmony)
        {
            Type launcher = RimKataActiveModTypes.Find("CombatExtended.Verb_LaunchProjectileCE");
            if (launcher == null) return;
            MethodInfo method = AccessTools.DeclaredMethod(launcher, "CanHitCellFromCellIgnoringRange",
                new[] { typeof(Vector3), typeof(IntVec3), typeof(Thing) });
            if (method?.ReturnType != typeof(bool) || method.IsStatic)
                throw new InvalidOperationException("CE directional-fire line-of-sight API does not match.");
            RimKataStartupPatches.Patch(harmony, method, transpiler: new HarmonyMethod(
                typeof(RimKataCombatExtendedDirectionalFire), nameof(LineOfSightTranspiler)));
        }

        private static IEnumerable<CodeInstruction> LineOfSightTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            FieldInfo requireLineOfSight = AccessTools.Field(typeof(VerbProperties), nameof(VerbProperties.requireLineOfSight));
            FieldInfo activeVerb = AccessTools.Field(typeof(RimKataDirectionalFire), nameof(RimKataDirectionalFire.activeVerb));
            FieldInfo endpoint = AccessTools.Field(typeof(RimKataDirectionalFire), nameof(RimKataDirectionalFire.activeEndpoint));
            MethodInfo sameCell = AccessTools.Method(typeof(IntVec3), "op_Equality", new[] { typeof(IntVec3), typeof(IntVec3) });
            int count = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, requireLineOfSight)) continue;
                count++;
                Label unchanged = generator.DefineLabel();
                yield return new CodeInstruction(OpCodes.Ldsfld, activeVerb);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Bne_Un, unchanged);
                yield return new CodeInstruction(OpCodes.Ldarg_3);
                yield return new CodeInstruction(OpCodes.Brtrue, unchanged);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Ldsfld, endpoint);
                yield return new CodeInstruction(OpCodes.Call, sameCell);
                yield return new CodeInstruction(OpCodes.Brfalse, unchanged);
                yield return new CodeInstruction(OpCodes.Pop);
                yield return new CodeInstruction(OpCodes.Ldc_I4_0);
                var resume = new CodeInstruction(OpCodes.Nop);
                resume.labels.Add(unchanged);
                yield return resume;
            }
            if (count != 1)
                throw new InvalidOperationException("CE directional-fire line-of-sight check does not match the supported shape.");
        }
    }
}
