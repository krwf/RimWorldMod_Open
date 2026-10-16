using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataDirectionalTargeting
    {
        internal static bool CanHitGroupedTarget(Verb verb, LocalTargetInfo target, Targeter targeter)
        {
            if (!target.IsValid || target.HasThing
                || !(targeter.targetingSource is RimKataDirectionalTargetingSource source))
                return verb.CanHitTarget(target);

            bool canHit = RimKataDirectionalFire.CanUseCommand(verb)
                ? RimKataDirectionalFire.CanOrderCell(verb.CasterPawn, verb, target.Cell)
                : verb.CanHitTarget(target);
            return canHit || source.CanHitTarget(target);
        }
    }

    [HarmonyPatch(typeof(Targeter), "CurrentTargetUnderMouse")]
    internal static class Patch_Targeter_RimKataDirectionalGroupedTarget
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(Verb), nameof(Verb.CanHitTarget),
                new[] { typeof(LocalTargetInfo) });
            MethodInfo replacement = AccessTools.Method(typeof(RimKataDirectionalTargeting),
                nameof(RimKataDirectionalTargeting.CanHitGroupedTarget));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!instruction.Calls(original))
                {
                    yield return instruction;
                    continue;
                }
                yield return new CodeInstruction(OpCodes.Ldarg_0)
                    .MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call, replacement);
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata grouped targeting call site changed: " + replaced);
        }
    }
}
