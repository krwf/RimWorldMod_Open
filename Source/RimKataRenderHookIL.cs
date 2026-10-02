using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataRenderHookIL
    {
        internal static IEnumerable<CodeInstruction> LivingOnly(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, int parmsArgument, IEnumerable<CodeInstruction> before,
            IEnumerable<CodeInstruction> after, IEnumerable<CodeInstruction> finish)
        {
            LocalBuilder living = generator.DeclareLocal(typeof(bool));
            Label body = generator.DefineLabel(), completed = generator.DefineLabel();
            Label leave = generator.DefineLabel(), finalEnd = generator.DefineLabel(), done = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldarga, (short)parmsArgument);
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.dead)));
            yield return new CodeInstruction(OpCodes.Ldc_I4_0);
            yield return new CodeInstruction(OpCodes.Ceq);
            yield return new CodeInstruction(OpCodes.Stloc, living);
            var start = new CodeInstruction(OpCodes.Ldloc, living);
            start.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            yield return start;
            yield return new CodeInstruction(OpCodes.Brfalse, body);
            foreach (CodeInstruction code in before) yield return code;
            yield return new CodeInstruction(OpCodes.Nop).WithLabels(body);
            foreach (CodeInstruction code in instructions)
            {
                if (code.opcode == OpCodes.Ret)
                {
                    code.opcode = OpCodes.Br;
                    code.operand = completed;
                }
                yield return code;
            }
            yield return new CodeInstruction(OpCodes.Ldloc, living).WithLabels(completed);
            yield return new CodeInstruction(OpCodes.Brfalse, leave);
            foreach (CodeInstruction code in after) yield return code;
            yield return new CodeInstruction(OpCodes.Leave, done).WithLabels(leave);
            var final = new CodeInstruction(OpCodes.Ldloc, living);
            final.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
            yield return final;
            yield return new CodeInstruction(OpCodes.Brfalse, finalEnd);
            foreach (CodeInstruction code in finish) yield return code;
            var end = new CodeInstruction(OpCodes.Nop).WithLabels(finalEnd);
            end.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
            yield return end;
            yield return new CodeInstruction(OpCodes.Ret).WithLabels(done);
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.ParallelPreDraw))]
    internal static class Patch_PawnRenderTree_RimKataLivingPreDraw
    {
        internal struct Scope
        {
            internal Patch_PawnRenderTree_RimKataTumbleRotation.RenderScope tumble;
            internal RimKataBreachRender.BodyScope breach;
        }

        private static void Begin(ref PawnDrawParms parms, List<PawnGraphicDrawRequest> requests, ref Scope scope)
        {
            Patch_PawnRenderTree_RimKataTumbleRotation.Prefix(ref parms, requests, out scope.tumble);
            Patch_PawnRenderTree_RimKataBreach.Prefix(ref parms, out scope.breach);
        }

        private static void Prepared(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests, Scope scope)
        {
            Patch_PawnRenderTree_RimKataTumbleRotation.Postfix(parms, requests, scope.tumble);
            Patch_PawnRenderTree_RimKataBreach.Postfix(parms, requests, scope.breach);
        }

        private static void End(Scope scope)
            => Patch_PawnRenderTree_RimKataTumbleRotation.Finalizer(scope.tumble);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            LocalBuilder scope = generator.DeclareLocal(typeof(Scope));
            FieldInfo requests = AccessTools.Field(typeof(PawnRenderTree), "drawRequests");
            return RimKataRenderHookIL.LivingOnly(instructions, generator, 1,
                new[] {
                    new CodeInstruction(OpCodes.Ldarga_S, (byte)1),
                    new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, requests),
                    new CodeInstruction(OpCodes.Ldloca, scope),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_PawnRenderTree_RimKataLivingPreDraw), nameof(Begin)))
                }, new[] {
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, requests),
                    new CodeInstruction(OpCodes.Ldloc, scope),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_PawnRenderTree_RimKataLivingPreDraw), nameof(Prepared)))
                }, new[] {
                    new CodeInstruction(OpCodes.Ldloc, scope),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_PawnRenderTree_RimKataLivingPreDraw), nameof(End)))
                });
        }
    }
}
