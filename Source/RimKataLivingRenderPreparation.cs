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
    internal static class RimKataLivingRenderPreparation
    {
        internal static void Prepare(Pawn pawn, Vector3 drawLoc, Rot4? rotOverride,
            ref PawnDrawParms parms, ref Vector3 bodyPos, ref float bodyAngle, ref bool useCached)
        {
            Vector3 adjustedDrawLoc = drawLoc;
            RimKataWorldRenderContext.Scope scope = default;
            RimKataWorldRenderContext.Scope cacheScope = default;
            try
            {
                Patch_PawnRenderer_RimKataDodgeOffset.Prefix(pawn, DrawPhase.ParallelPreDraw,
                    ref adjustedDrawLoc, ref rotOverride, out scope);
                Patch_PawnRenderer_RimKataBreachFacing.Prefix(pawn, DrawPhase.ParallelPreDraw,
                    ref adjustedDrawLoc, ref rotOverride);

                Vector3 delta = adjustedDrawLoc - drawLoc;
                if (parms.posture != PawnPosture.Standing)
                {
                    if (parms.bed != null && pawn.RaceProps.Humanlike)
                        delta = Vector3.zero;
                    else if (pawn.ParentHolder is IThingHolderWithDrawnPawn
                        || pawn.ParentHolder?.ParentHolder is IThingHolderWithDrawnPawn
                        || pawn.CarriedBy == null && !(pawn.ParentHolder is PawnFlyer))
                        delta.y = 0f;
                }
                bodyPos += delta;
                parms.matrix.m03 += delta.x;
                parms.matrix.m13 += delta.y;
                parms.matrix.m23 += delta.z;
                if (rotOverride.HasValue) parms.facing = rotOverride.Value;

                float angle = bodyAngle;
                RimKataCrawlFireRender.AdjustFacing(pawn, bodyPos, parms.flags,
                    ref angle, ref parms.facing);
                if (angle != bodyAngle)
                {
                    Matrix4x4 rotated = Matrix4x4.Rotate(
                        Quaternion.AngleAxis(angle - bodyAngle, Vector3.up)) * parms.matrix;
                    rotated.m03 = parms.matrix.m03;
                    rotated.m13 = parms.matrix.m13;
                    rotated.m23 = parms.matrix.m23;
                    parms.matrix = rotated;
                    bodyAngle = angle;
                }

                bool disableCache = false;
                Patch_PawnRenderer_RimKataDynamicRotationCache.Prefix(pawn,
                    ref disableCache, out cacheScope);
                Patch_PawnRenderer_RimKataBreachCache.Prefix(pawn, ref disableCache);
                if (disableCache) useCached = false;
            }
            finally
            {
                RimKataWorldRenderContext.End(cacheScope);
                RimKataWorldRenderContext.End(scope);
            }
        }

        internal static void PrepareShadow(Pawn pawn, ref Vector3 drawLoc, bool adjustWorldPosition)
        {
            RimKataWorldRenderContext.Scope scope = default;
            try
            {
                if (adjustWorldPosition)
                {
                    Rot4? facing = null;
                    Patch_PawnRenderer_RimKataDodgeOffset.Prefix(pawn, DrawPhase.Draw,
                        ref drawLoc, ref facing, out scope);
                    Patch_PawnRenderer_RimKataBreachFacing.Prefix(pawn, DrawPhase.Draw,
                        ref drawLoc, ref facing);
                }
                else scope = RimKataWorldRenderContext.Begin(pawn);
                RimKataGroundPoseRender.PlaceShadow(pawn, ref drawLoc);
                RimKataBreachRender.PlaceShadow(pawn, ref drawLoc);
                RimKataReactiveRender.PlaceShadow(pawn, ref drawLoc);
            }
            finally
            {
                RimKataWorldRenderContext.End(scope);
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    internal static class Patch_PawnRenderer_RimKataLivingPreparation
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, MethodBase original)
        {
            Type resultType = AccessTools.Inner(typeof(PawnRenderer), "PreRenderResults");
            FieldInfo parmsField = AccessTools.Field(resultType, "parms");
            FieldInfo deadField = AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.dead));
            FieldInfo pawnField = AccessTools.Field(typeof(PawnRenderer), "pawn");
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            CodeInstruction resultAddress = null;
            MethodInfo prepare = AccessTools.Method(typeof(RimKataLivingRenderPreparation),
                nameof(RimKataLivingRenderPreparation.Prepare));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S)
                {
                    int index = instruction.operand is LocalBuilder builder ? builder.LocalIndex
                        : instruction.operand is LocalVariableInfo local ? local.LocalIndex
                        : Convert.ToInt32(instruction.operand);
                    if (index < locals.Count && locals[index].LocalType == resultType)
                        resultAddress = instruction;
                }
                yield return instruction;
                if (instruction.opcode != OpCodes.Stfld || !Equals(instruction.operand, parmsField)) continue;
                if (resultAddress == null)
                    throw new InvalidOperationException("RimKata living render preparation could not identify the native result address.");
                replaced++;
                Label resume = generator.DefineLabel();
                yield return new CodeInstruction(resultAddress.opcode, resultAddress.operand);
                yield return new CodeInstruction(OpCodes.Ldflda, parmsField);
                yield return new CodeInstruction(OpCodes.Ldfld, deadField);
                yield return new CodeInstruction(OpCodes.Brtrue, resume);
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, pawnField);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                foreach (string name in new[] { "parms", "bodyPos", "bodyAngle", "useCached" })
                {
                    yield return new CodeInstruction(resultAddress.opcode, resultAddress.operand);
                    yield return new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(resultType, name));
                }
                yield return new CodeInstruction(OpCodes.Call, prepare);
                yield return new CodeInstruction(OpCodes.Nop).WithLabels(resume);
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata living render preparation expected one native draw-parameter assignment.");
        }
    }

    [HarmonyPatch]
    internal static class Patch_PawnRenderer_RimKataLivingShadow
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt));
            yield return AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.RenderShadowOnlyAt));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator, MethodBase original)
        {
            MethodInfo drawShadow = AccessTools.Method(typeof(PawnRenderer), "DrawShadowInternal");
            FieldInfo pawnField = AccessTools.Field(typeof(PawnRenderer), "pawn");
            FieldInfo resultsField = AccessTools.Field(typeof(PawnRenderer), "results");
            FieldInfo parmsField = AccessTools.Field(resultsField.FieldType, "parms");
            FieldInfo deadField = AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.dead));
            MethodInfo prepare = AccessTools.Method(typeof(RimKataLivingRenderPreparation),
                nameof(RimKataLivingRenderPreparation.PrepareShadow));
            bool worldRender = original.Name == nameof(PawnRenderer.RenderPawnAt);
            LocalBuilder position = generator.DeclareLocal(typeof(Vector3));
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!instruction.Calls(drawShadow))
                {
                    yield return instruction;
                    continue;
                }
                replaced++;
                Label resume = generator.DefineLabel();
                var storePosition = new CodeInstruction(OpCodes.Stloc, position);
                storePosition.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                storePosition.blocks.AddRange(instruction.blocks);
                instruction.blocks.Clear();
                yield return storePosition;
                if (worldRender)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldflda, resultsField);
                    yield return new CodeInstruction(OpCodes.Ldflda, parmsField);
                    yield return new CodeInstruction(OpCodes.Ldfld, deadField);
                    yield return new CodeInstruction(OpCodes.Brtrue, resume);
                }
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldfld, pawnField);
                yield return new CodeInstruction(OpCodes.Ldloca, position);
                yield return new CodeInstruction(worldRender ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                yield return new CodeInstruction(OpCodes.Call, prepare);
                yield return new CodeInstruction(OpCodes.Ldloc, position).WithLabels(resume);
                yield return instruction;
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata living shadow preparation expected one native shadow call.");
        }
    }
}
