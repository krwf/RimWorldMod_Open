using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Keep Ratkin's native mesh, scale and recoil, then finish the existing RimKata render scope.
    internal static class RimKataRatkinRenderCompat
    {
        internal const string HarmonyId = "com.NewRatkin.rimworld.mod.genedrawscale";
        private static Func<Pawn, float> drawScale;

        internal static float HeldDrawScale(Pawn pawn) => drawScale?.Invoke(pawn) ?? 1f;

        internal static void Apply(Harmony harmony)
        {
            Type type = RimKataActiveModTypes.Find("NewRatkin.GeneDrawScaleRenderPatches");
            if (type == null) return;
            MethodInfo owner = AccessTools.Method(type, "PawnForEquippedThing", new[] { typeof(Thing) });
            MethodInfo draw = AccessTools.Method(type, "DrawEquipmentAimingScaled",
                new[] { typeof(Thing), typeof(Vector3), typeof(float), typeof(float) });
            if (owner?.ReturnType != typeof(Pawn) || !owner.IsStatic
                || draw?.ReturnType != typeof(void) || !draw.IsStatic)
            {
                Log.Warning("[RimKata] Ratkin weapon scaling API did not match; secondary scaling integration was not applied.");
                return;
            }
            var adapter = new Harmony(harmony.Id + ".ratkinWeaponScale");
            try
            {
                MethodInfo scale = AccessTools.Method(type.Assembly.GetType("NewRatkin.GeneDrawScaleCache"),
                    "Get", new[] { typeof(Pawn) });
                if (scale?.ReturnType != typeof(float) || !scale.IsStatic)
                    throw new InvalidOperationException("Ratkin's cached draw scale API was not found.");
                var scaleGetter = (Func<Pawn, float>)Delegate.CreateDelegate(typeof(Func<Pawn, float>), scale);
                RimKataStartupPatches.Patch(adapter, draw,
                    transpiler: new HarmonyMethod(typeof(RimKataRatkinRenderCompat), nameof(DrawTranspiler)));
                RimKataStartupPatches.Patch(adapter, owner,
                    postfix: new HarmonyMethod(typeof(RimKataRatkinRenderCompat), nameof(OwnerPostfix)));
                drawScale = scaleGetter;
            }
            catch (Exception exception)
            {
                adapter.UnpatchAll(adapter.Id);
                Log.Warning("[RimKata] Ratkin secondary weapon scaling integration failed: " + exception);
            }
        }

        private static void OwnerPostfix(Thing eq, ref Pawn __result)
        {
            if (__result != null || !RimKataDualWeaponRenderUtility.DrawingSecondary
                || RimKataWeaponRenderProbe.Probing) return;
            ref readonly RimKataCarryDrawContext carry = ref RimKataCarryDrawUtility.Current;
            if (carry.active && carry.secondary == eq)
            {
                __result = carry.pawn;
                return;
            }
            ref readonly RimKataGunReadyDrawContext context = ref RimKataGunReadyDrawUtility.Current;
            if (context.active && !context.portrait && context.secondary == eq)
                __result = context.pawn;
        }

        private static IEnumerable<CodeInstruction> DrawTranspiler(IEnumerable<CodeInstruction> instructions,
            ILGenerator generator)
        {
            MethodInfo original = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh),
                new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int) });
            MethodInfo replacement = AccessTools.Method(typeof(RimKataRatkinRenderCompat), nameof(DrawMesh));
            LocalBuilder nativeAngle = generator.DeclareLocal(typeof(float));
            bool found = false, angleFound = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(original))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0)
                        .MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    yield return new CodeInstruction(OpCodes.Ldarg_2);
                    yield return new CodeInstruction(OpCodes.Ldloc, nativeAngle);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    found = true;
                }
                yield return instruction;
                if (!angleFound && instruction.opcode == OpCodes.Rem)
                {
                    // Capture the native mesh angle before recoil; reflection must not reverse recoil.
                    yield return new CodeInstruction(OpCodes.Dup);
                    yield return new CodeInstruction(OpCodes.Stloc, nativeAngle);
                    angleFound = true;
                }
            }
            if (!found || !angleFound)
                throw new InvalidOperationException("Ratkin's weapon angle or final mesh call was not found.");
        }

        private static void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int layer,
            Thing weapon, float aim, float nativeAngle)
        {
            bool secondary = RimKataDualWeaponRenderUtility.DrawingSecondary;
            if (!secondary && (!RimKataWorldRenderContext.HasEquipmentParticipant
                    && !RimKataCrawlFireRender.Drawing
                || RimKataImmersiveWarfareCompat.OwnsPrimary(weapon)))
            {
                Graphics.DrawMesh(mesh, matrix, material, layer);
                return;
            }
            float offset = Patch_PawnRenderUtility_RimKataDeflection.ApplyVisualAngleOffset(nativeAngle) - nativeAngle;
            if (offset != 0f)
            {
                Vector4 position = matrix.GetColumn(3);
                matrix = Matrix4x4.Rotate(Quaternion.AngleAxis(offset, Vector3.up)) * matrix;
                matrix.SetColumn(3, position);
            }
            var scope = RimKataGroundPoseRender.BeginWeapon(weapon, aim);
            try
            {
                if (secondary)
                {
                    if (mesh == MeshPool.plane10) mesh = RimKataDualWeaponRenderUtility.Plane10ForContext();
                    else if (mesh == MeshPool.plane10Flip) mesh = RimKataDualWeaponRenderUtility.Plane10FlipForContext();
                    RimKataDualWeaponRenderUtility.DrawSecondaryEquipmentMesh(mesh, matrix, material, layer);
                }
                else RimKataGroundPoseRender.DrawPrimaryMesh(mesh, matrix, material, layer);
            }
            finally { RimKataGroundPoseRender.EndWeapon(scope); }
        }
    }
}

