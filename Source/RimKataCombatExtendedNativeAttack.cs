using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    // Installed only after CE has been detected. Without CE the native request
    // contains no compatibility calls, including no per-shot availability check.
    internal static class RimKataCombatExtendedNativeAttack
    {
        internal static void Apply(Harmony harmony)
        {
            Patch(harmony, nameof(RimKataNativeAttack.Queue), nameof(QueueTranspiler));
            Patch(harmony, nameof(RimKataNativeAttack.CanBeginNativeTick), nameof(BeginTranspiler));
            Patch(harmony, nameof(RimKataNativeAttack.FinishNativeCast), nameof(FinishTranspiler));
            Patch(harmony, nameof(RimKataNativeAttack.Cancel), nameof(CancelTranspiler));
            harmony.Patch(AccessTools.Method(typeof(RimKataNativeAttack), "CompleteRequest"),
                prefix: new HarmonyMethod(typeof(RimKataCombatExtendedNativeAttack), nameof(CompletePrefix)));
        }

        private static void Patch(Harmony harmony, string target, string transpiler)
            => harmony.Patch(AccessTools.Method(typeof(RimKataNativeAttack), target),
                transpiler: new HarmonyMethod(typeof(RimKataCombatExtendedNativeAttack), transpiler));

        private static bool PrepareAmmo(RimKataNativeAttack request)
        {
            if (!RimKataCombatExtendedCompat.EnsureAmmoReady(request.pawn, request.verb)) return false;
            request.extraAimTicks = -1;
            return true;
        }

        private static bool PrepareOrContinueAim(RimKataNativeAttack request)
        {
            Verb verb = request.verb;
            if (request.extraAimTicks >= 0)
            {
                if (RimKataCombatExtendedFire.ContinueExtraAim(verb)
                    && --request.extraAimTicks > 0) return false;
                request.extraAimTicks = -1;
                RimKataCombatExtendedFire.ResumeExtraAim(verb);
                return request.PrepareShot();
            }
            if (RimKataCombatExtendedFire.BeginsExtraAim(verb)) return true;
            return request.PrepareShot();
        }

        private static bool DeferAim(RimKataNativeAttack request, Exception exception)
        {
            if (request.Cancelled || exception != null || request.HasFired
                || request.verb.state == VerbState.Bursting
                || !RimKataCombatExtendedFire.SuspendExtraAim(request.verb, out request.extraAimTicks))
                return false;
            request.RestoreAimAfterShot();
            request.Started = false;
            return true;
        }

        private static void CompletePrefix(RimKataNativeAttack __instance)
            => RimKataCombatExtendedFire.ClearExtraAim(__instance.verb);

        private static void ResetCancelledVerb(Verb verb)
        {
            RimKataCombatExtendedFire.ClearExtraAim(verb);
            verb.Reset();
        }

        private static IEnumerable<CodeInstruction> QueueTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo reset = AccessTools.Method(typeof(Verb), nameof(Verb.Reset));
            FieldInfo verb = AccessTools.Field(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.verb));
            int call = codes.FindIndex(c => c.Calls(reset));
            if (call < 2 || codes[call - 2].opcode != OpCodes.Ldarg_0
                || codes[call - 1].opcode != OpCodes.Ldfld || !Equals(codes[call - 1].operand, verb))
                throw new InvalidOperationException("RimKata CE ammo request insertion point was not found.");
            Label ready = generator.DefineLabel();
            var inserted = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataCombatExtendedNativeAttack), nameof(PrepareAmmo))),
                new CodeInstruction(OpCodes.Brtrue, ready),
                new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Ret)
            };
            InsertBefore(codes, call - 2, inserted, ready);
            return codes;
        }

        private static IEnumerable<CodeInstruction> BeginTranspiler(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions,
                AccessTools.Method(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.PrepareShot)),
                nameof(PrepareOrContinueAim));

        private static IEnumerable<CodeInstruction> CancelTranspiler(IEnumerable<CodeInstruction> instructions)
            => ReplaceCall(instructions, AccessTools.Method(typeof(Verb), nameof(Verb.Reset)), nameof(ResetCancelledVerb));

        private static IEnumerable<CodeInstruction> ReplaceCall(
            IEnumerable<CodeInstruction> instructions, MethodInfo original, string replacement)
        {
            var codes = new List<CodeInstruction>(instructions);
            int count = 0;
            foreach (CodeInstruction code in codes)
            {
                if (!code.Calls(original)) continue;
                code.opcode = OpCodes.Call;
                code.operand = AccessTools.Method(typeof(RimKataCombatExtendedNativeAttack), replacement);
                count++;
            }
            if (count != 1)
                throw new InvalidOperationException("RimKata CE native request layout did not match: " + replacement);
            return codes;
        }

        private static IEnumerable<CodeInstruction> FinishTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo restore = AccessTools.Method(typeof(RimKataNativeAttack), nameof(RimKataNativeAttack.RestoreAimAfterShot));
            int call = codes.FindIndex(c => c.Calls(restore));
            if (call < 1 || codes[call - 1].opcode != OpCodes.Ldarg_0)
                throw new InvalidOperationException("RimKata CE deferred aim insertion point was not found.");
            Label finish = generator.DefineLabel();
            var inserted = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RimKataCombatExtendedNativeAttack), nameof(DeferAim))),
                new CodeInstruction(OpCodes.Brfalse, finish),
                new CodeInstruction(OpCodes.Ret)
            };
            InsertBefore(codes, call - 1, inserted, finish);
            return codes;
        }

        private static void InsertBefore(List<CodeInstruction> codes, int index,
            List<CodeInstruction> inserted, Label proceed)
        {
            inserted[0].labels.AddRange(codes[index].labels);
            codes[index].labels.Clear();
            codes[index].labels.Add(proceed);
            codes.InsertRange(index, inserted);
        }
    }
}
