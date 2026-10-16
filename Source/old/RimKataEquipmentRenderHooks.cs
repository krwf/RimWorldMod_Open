using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataEquipmentRenderHooks
    {
        [ThreadStatic] private static int equipmentDepth;

        internal static void DrawUnmodifiedEquipment(Pawn pawn, Vector3 drawPos, Rot4 facing,
            PawnRenderFlags flags)
        {
            // Keep lower weapon hooks from rediscovering an already-known empty render path.
            int token = RimKataGunReadyDrawUtility.PushInactive(pawn, flags);
            try
            {
                PawnRenderUtility.DrawEquipmentAndApparelExtras(pawn, drawPos, facing, flags);
            }
            finally
            {
                RimKataGunReadyDrawUtility.Pop(token);
            }
        }

        internal static void DrawRegisteredEquipment(Pawn pawn, Vector3 drawPos, Rot4 facing,
            PawnRenderFlags flags, RimKataResponseVisualParticipantCache.BodyVisualEntry entry,
            float bodyAltitude)
        {
            var scope = RimKataWorldRenderContext.BeginRegistered(pawn, entry,
                (flags & PawnRenderFlags.Portrait) != 0, bodyAltitude);
            equipmentDepth++;
            try
            {
                if (equipmentDepth > 1 || entry == null
                    || entry.breach.HasValue || entry.subdue.HasValue || entry.reactive.HasValue
                    || entry.flyingKick.HasValue || entry.kick.HasValue)
                    DrawSpecialEquipmentAndApparelExtras(pawn, drawPos, facing, flags);
                else DrawEquipmentAndApparelExtras(pawn, drawPos, facing, flags);
            }
            finally
            {
                equipmentDepth--;
                RimKataWorldRenderContext.End(scope);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void DrawSpecialEquipmentAndApparelExtras(
            Pawn pawn, Vector3 drawPos, Rot4 facing, PawnRenderFlags flags)
        {
            RimKataBreachRender.EquipmentScope scope = default;
            try
            {
                RimKataSpecialEquipmentRender.Begin(pawn, drawPos, ref facing, flags, out scope);
                DrawEquipmentAndApparelExtras(pawn, drawPos, facing, flags);
                RimKataBreachWeaponRender.Draw();
                RimKataReactiveRender.Draw();
            }
            finally
            {
                RimKataSpecialEquipmentRender.End(scope);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void DrawEquipmentAndApparelExtras(
            Pawn pawn, Vector3 drawPos, Rot4 facing, PawnRenderFlags flags)
        {
            var entry = RimKataWorldRenderContext.BodyFor(pawn);
            bool nested = equipmentDepth > 1;
            bool pair = entry?.equipmentSecondary != null;
            bool meleeAnimation = pair && RimKataMeleeAnimationCompat.EquipmentRenderingEnabled;
            RimKataMeleeAnimationCompat.EquipmentFrame animationFrame = null;
            bool groundPose = false;
            int gunReady = 0, probe = 0;
            bool completed = false;
            try
            {
                if (meleeAnimation)
                    RimKataMeleeAnimationCompat.BeginEquipment(pawn, drawPos, facing, flags, out animationFrame);
                if (nested || entry?.groundPose == true)
                    groundPose = RimKataGroundPoseRender.PushEquipment(pawn, flags);
                gunReady = RimKataGunReadyDrawUtility.Push(pawn, flags, entry);
                if (nested || RimKataGunReadyDrawUtility.Current.secondary != null)
                    probe = RimKataWeaponRenderProbe.BeginFrame(pawn, drawPos, facing, flags);
                PawnRenderUtility.DrawEquipmentAndApparelExtras(pawn, drawPos, facing, flags);
                completed = true;
            }
            finally
            {
                try
                {
                    if (meleeAnimation)
                        RimKataMeleeAnimationCompat.EndEquipment(completed, animationFrame);
                }
                finally
                {
                    try
                    {
                        RimKataWeaponRenderProbe.EndFrame(probe, completed);
                    }
                    finally
                    {
                        RimKataGunReadyDrawUtility.Pop(gunReady);
                        RimKataGroundPoseRender.PopEquipment(groundPose);
                    }
                }
            }
        }

        internal static IEnumerable<CodeInstruction> ReplaceEquipmentCall(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, bool cached)
        {
            MethodInfo original = AccessTools.Method(typeof(PawnRenderUtility),
                nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras));
            MethodInfo registered = AccessTools.Method(typeof(RimKataEquipmentRenderHooks),
                nameof(DrawRegisteredEquipment));
            MethodInfo unmodified = AccessTools.Method(typeof(RimKataEquipmentRenderHooks),
                nameof(DrawUnmodifiedEquipment));
            FieldInfo dead = AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.dead));
            FieldInfo results = cached ? AccessTools.Field(typeof(PawnRenderer), "results") : null;
            FieldInfo parms = cached ? AccessTools.Field(results.FieldType, "parms") : null;
            int replaced = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!instruction.Calls(original))
                {
                    yield return instruction;
                    continue;
                }

                Label native = generator.DefineLabel();
                Label absent = generator.DefineLabel();
                Label finished = generator.DefineLabel();
                Label prepare = generator.DefineLabel();
                var first = new CodeInstruction(cached ? OpCodes.Ldarg_0 : OpCodes.Ldarga_S,
                    cached ? null : (object)(byte)2);
                first.labels.AddRange(instruction.labels);
                first.blocks.AddRange(instruction.blocks);
                yield return first;
                if (cached)
                {
                    yield return new CodeInstruction(OpCodes.Ldflda, results);
                    yield return new CodeInstruction(OpCodes.Ldflda, parms);
                }
                yield return new CodeInstruction(OpCodes.Ldfld, dead);
                yield return new CodeInstruction(OpCodes.Brtrue, native);
                var loadPawn = cached
                    ? new[] { new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PawnRenderer), "pawn")) }
                    : new[] { new CodeInstruction(OpCodes.Ldarga_S, (byte)2),
                        new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.pawn))) };
                LocalBuilder entry;
                foreach (var code in RimKataRegisteredPawnGate.Branch(generator, loadPawn, absent, false, out entry))
                    yield return code;
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(RimKataEquipmentRenderHooks), nameof(equipmentDepth)));
                yield return new CodeInstruction(OpCodes.Brtrue, prepare);
                yield return new CodeInstruction(OpCodes.Ldloc, entry);
                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(
                    typeof(RimKataResponseVisualParticipantCache.BodyVisualEntry), "equipmentNeeded"));
                yield return new CodeInstruction(OpCodes.Brtrue, prepare);
                yield return new CodeInstruction(OpCodes.Call, unmodified);
                yield return new CodeInstruction(OpCodes.Br, finished);
                yield return new CodeInstruction(OpCodes.Ldloc, entry).WithLabels(prepare);
                foreach (var code in LoadBodyAltitude(cached, results)) yield return code;
                yield return new CodeInstruction(OpCodes.Call, registered);
                yield return new CodeInstruction(OpCodes.Br, finished);
                yield return new CodeInstruction(OpCodes.Ldsfld,
                    AccessTools.Field(typeof(RimKataEquipmentRenderHooks), nameof(equipmentDepth))).WithLabels(absent);
                yield return new CodeInstruction(OpCodes.Brfalse, native);
                yield return new CodeInstruction(OpCodes.Ldnull);
                foreach (var code in LoadBodyAltitude(cached, results)) yield return code;
                yield return new CodeInstruction(OpCodes.Call, registered);
                yield return new CodeInstruction(OpCodes.Br, finished);
                var nativeCall = new CodeInstruction(instruction.opcode, instruction.operand);
                nativeCall.labels.Add(native);
                yield return nativeCall;
                var end = new CodeInstruction(OpCodes.Nop);
                end.labels.Add(finished);
                yield return end;
                replaced++;
            }
            if (replaced != 1)
                throw new InvalidOperationException("RimKata equipment render call site changed: " + replaced);
        }

        private static IEnumerable<CodeInstruction> LoadBodyAltitude(bool cached, FieldInfo results)
        {
            if (cached)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldflda, results);
                yield return new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(results.FieldType, "bodyPos"));
                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Vector3), nameof(Vector3.y)));
            }
            else
            {
                yield return new CodeInstruction(OpCodes.Ldarga_S, (byte)2);
                yield return new CodeInstruction(OpCodes.Ldflda, AccessTools.Field(typeof(PawnDrawParms), nameof(PawnDrawParms.matrix)));
                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Matrix4x4), nameof(Matrix4x4.m13)));
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt))]
    internal static class Patch_PawnRenderer_RimKataEquipmentEntry
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => RimKataEquipmentRenderHooks.ReplaceEquipmentCall(instructions, generator, true);
    }

    [HarmonyPatch(typeof(PawnRenderNodeWorker_Carried), nameof(PawnRenderNodeWorker_Carried.PostDraw))]
    internal static class Patch_PawnRenderNodeWorker_RimKataEquipmentEntry
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            => RimKataEquipmentRenderHooks.ReplaceEquipmentCall(instructions, generator, false);
    }
}
