using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // IW owns the primary's presentation; only an equipped RimKata secondary
    // consumes the stack pose. No combat or formation state is changed here.
    internal static class RimKataImmersiveWarfareCompat
    {
        internal struct Pose
        {
            internal Rot4 facing;
            internal int rank;
            internal float weaponDepth;

            internal Pose(Rot4 facing, int rank, float weaponDepth)
            {
                this.facing = facing;
                this.rank = rank;
                this.weaponDepth = weaponDepth;
            }
        }

        internal struct Scope
        {
            internal Pawn pawn;
            internal ThingWithComps primary, secondary;
            internal Pose pose;
            internal bool companion;
        }

        private delegate bool ReadPose(Pawn pawn, out Pose pose);
        private delegate void IdleGun(int rank, int facing, out float angle, out float x, out float z);
        private static ReadPose readPose;
        private static IdleGun idleGun;
        private static Func<float, float, float> depthInBand;
        [ThreadStatic] private static Scope current;

        internal static bool Enabled { get; private set; }
        internal static bool Active => current.pawn != null;
        internal static bool OwnsPawn(Pawn pawn) => pawn != null && current.pawn == pawn;
        internal static bool OwnsPrimary(Thing weapon) => weapon != null && current.primary == weapon;

        internal static void Initialize()
        {
            Type visuals = RimKataActiveModTypes.Find("SmartCombat.StackVisuals");
            if (visuals == null) return;
            try
            {
                Assembly owner = visuals.Assembly;
                Bind(visuals, RimKataActiveModTypes.Find(owner, "SmartCombat.StackPose"),
                    RimKataActiveModTypes.Find(owner, "SmartCombat.StackPresentationPolicy"));
            }
            catch (Exception exception)
            {
                Enabled = false;
                readPose = null;
                idleGun = null;
                depthInBand = null;
                Log.Warning("[RimKata] Immersive Warfare stack rendering integration was not applied: "
                    + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void Bind(Type visuals, Type poseType, Type policy)
        {
            if (poseType == null || !poseType.IsValueType || policy == null)
                throw new MissingMemberException("Immersive Warfare stack pose types");
            MethodInfo read = AccessTools.DeclaredMethod(visuals, "TryState",
                new[] { typeof(Pawn), poseType.MakeByRefType() });
            MethodInfo depth = AccessTools.DeclaredMethod(visuals, "DepthInBand",
                new[] { typeof(float), typeof(float) });
            MethodInfo idle = AccessTools.DeclaredMethod(policy, "IdleGun",
                new[] { typeof(int), typeof(int), typeof(float).MakeByRefType(),
                    typeof(float).MakeByRefType(), typeof(float).MakeByRefType() });
            if (read?.ReturnType != typeof(bool) || !read.IsStatic
                || depth?.ReturnType != typeof(float) || !depth.IsStatic
                || idle?.ReturnType != typeof(void) || !idle.IsStatic)
                throw new MissingMethodException("Immersive Warfare stack pose methods");

            var method = new DynamicMethod("RimKata_ReadImmersiveWarfarePose", typeof(bool),
                new[] { typeof(Pawn), typeof(Pose).MakeByRefType() },
                typeof(RimKataImmersiveWarfareCompat).Module, true);
            ILGenerator il = method.GetILGenerator();
            LocalBuilder native = il.DeclareLocal(poseType);
            Label missing = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Initobj, typeof(Pose));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloca, native);
            il.Emit(OpCodes.Call, read);
            il.Emit(OpCodes.Brfalse, missing);
            il.Emit(OpCodes.Ldarg_1);
            foreach (string name in new[] { "facing", "rank", "weaponDepth" })
            {
                FieldInfo destination = AccessTools.DeclaredField(typeof(Pose), name);
                FieldInfo source = AccessTools.DeclaredField(poseType, name);
                if (source == null || source.IsStatic || source.FieldType != destination.FieldType)
                    throw new MissingFieldException(poseType.FullName, name);
                il.Emit(OpCodes.Ldloca, native);
                il.Emit(OpCodes.Ldfld, source);
            }
            il.Emit(OpCodes.Newobj, AccessTools.DeclaredConstructor(typeof(Pose),
                new[] { typeof(Rot4), typeof(int), typeof(float) }));
            il.Emit(OpCodes.Stobj, typeof(Pose));
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(missing);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);

            ReadPose reader = (ReadPose)method.CreateDelegate(typeof(ReadPose));
            IdleGun idleReader = (IdleGun)Delegate.CreateDelegate(typeof(IdleGun), idle);
            var depthReader = (Func<float, float, float>)Delegate.CreateDelegate(
                typeof(Func<float, float, float>), depth);
            readPose = reader;
            idleGun = idleReader;
            depthInBand = depthReader;
            Enabled = true;
        }

        internal static Scope Begin(Pawn pawn,
            RimKataResponseVisualParticipantCache.BodyVisualEntry entry, PawnRenderFlags flags)
        {
            Scope previous = current;
            current = default;
            // Also mask a parent's scope for nested, ordinary or portrait draws.
            if (!Enabled || entry?.registeredQualified != true || entry.equipmentPrimary == null
                || (flags & PawnRenderFlags.Portrait) != 0 || pawn?.Spawned != true || pawn.Downed)
                return previous;
            try
            {
                if (readPose(pawn, out Pose pose))
                    current = new Scope { pawn = pawn, primary = entry.equipmentPrimary,
                        secondary = entry.equipmentSecondary, pose = pose };
            }
            catch
            {
                current = previous;
                throw;
            }
            return previous;
        }

        internal static void End(Scope previous) => current = previous;

        internal static void DrawSecondary(Pawn pawn, ThingWithComps primary,
            ThingWithComps secondary, Vector3 root, float bodyAltitude)
        {
            if (!OwnsPawn(pawn) || primary != current.primary
                || secondary == null || secondary != current.secondary) return;
            Scope previous = current;
            current.companion = true;
            try
            {
                Vector3? idleLocation = null;
                float? idleAngle = null;
                if (secondary.def.IsRangedWeapon)
                {
                    idleGun(current.pose.rank, current.pose.facing.AsInt,
                        out float angle, out float x, out float z);
                    float factor = pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f;
                    Vector3 offset = new Vector3(x, 0f, z)
                        + current.pose.facing.FacingCell.ToVector3() * secondary.def.equippedDistanceOffset;
                    idleLocation = root + offset * factor;
                    idleAngle = angle;
                }
                RimKataDualWeaponRenderUtility.DrawSecondaryAfterExternalPrimary(
                    pawn, primary, secondary, root, bodyAltitude, false,
                    idleLocation, idleAngle, current.pose.facing);
            }
            finally { current = previous; }
        }

        internal static void AdjustSecondaryDepth(ref Matrix4x4 matrix)
        {
            if (current.companion)
                matrix.m13 = depthInBand(matrix.m13, current.pose.weaponDepth);
        }

        private static Matrix4x4 PrimaryMatrix(Matrix4x4 matrix, Thing weapon)
            => OwnsPrimary(weapon) ? matrix : RimKataGroundPoseRender.TransformEquipment(matrix);

        // Leave a real native primary draw call in the patched method. IW's
        // later MeshCalls transpiler must see it to install its own adapter.
        internal static IEnumerable<CodeInstruction> EquipmentMeshCall(
            CodeInstruction original, ILGenerator generator, FieldInfo drawingSecondary,
            MethodInfo secondaryDraw, MethodInfo nativeDraw)
        {
            LocalBuilder layer = generator.DeclareLocal(typeof(int));
            LocalBuilder material = generator.DeclareLocal(typeof(Material));
            Label secondary = generator.DefineLabel();
            Label complete = generator.DefineLabel();
            yield return new CodeInstruction(OpCodes.Ldsfld, drawingSecondary)
                .MoveLabelsFrom(original).MoveBlocksFrom(original);
            yield return new CodeInstruction(OpCodes.Brtrue, secondary);
            yield return new CodeInstruction(OpCodes.Stloc, layer);
            yield return new CodeInstruction(OpCodes.Stloc, material);
            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.DeclaredMethod(typeof(RimKataImmersiveWarfareCompat), nameof(PrimaryMatrix)));
            yield return new CodeInstruction(OpCodes.Ldloc, material);
            yield return new CodeInstruction(OpCodes.Ldloc, layer);
            yield return new CodeInstruction(OpCodes.Call, nativeDraw);
            yield return new CodeInstruction(OpCodes.Br, complete);
            var secondaryCall = new CodeInstruction(OpCodes.Call, secondaryDraw);
            secondaryCall.labels.Add(secondary);
            yield return secondaryCall;
            var done = new CodeInstruction(OpCodes.Nop);
            done.labels.Add(complete);
            yield return done;
        }
    }
}

