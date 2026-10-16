using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataMeleeAnimationCompat
    {
        private static readonly ConditionalWeakTable<object, Frame> frames =
            new ConditionalWeakTable<object, Frame>();
        private static readonly ConditionalWeakTable<Pawn, EquipmentFrame> equipmentFrames =
            new ConditionalWeakTable<Pawn, EquipmentFrame>();
        [ThreadStatic] private static Frame current;
        [ThreadStatic] private static Frame breachOwnedFrame;
        internal static Func<object, string, object> getPart;
        internal static Func<object, int, object> getOverride;
        internal static Func<object, int> partIndex, animationType;
        internal static Func<object, object> definition;
        private static Func<object, Thing> weapon;
        internal static Func<object, bool> destroyed, mirrorX, mirrorY;
        internal static Func<object, Matrix4x4> root;
        private static Func<object, Map> map;
        internal static Func<Pawn, object> currentAnimation, controller;
        internal static Func<object, int, Pose> readPose;
        internal static Func<ThingDef, object> getTweak;
        private static Func<object, float> offX, offY, scaleX, scaleY, rotation;
        private static Func<object, bool> flipX, flipY;
        internal static Func<bool, bool, Mesh> getMesh;
        private static MethodInfo shouldDraw, drawMesh, filterBridge;
        private static int idleType;
        private static bool failed;
        internal static bool EquipmentRenderingEnabled { get; private set; }

        internal static void Apply(Harmony harmony)
        {
            Type renderer = RimKataActiveModTypes.Find("AM.AnimRenderer");
            if (renderer == null) return;

            Type part = RimKataActiveModTypes.Find(renderer.Assembly, "AnimPartData");
            Type snapshot = RimKataActiveModTypes.Find(renderer.Assembly, "AnimPartSnapshot");
            Type ov = RimKataActiveModTypes.Find(renderer.Assembly, "AnimPartOverrideData");
            Type def = RimKataActiveModTypes.Find("AM.AnimDef");
            Type comp = RimKataActiveModTypes.Find("AM.Idle.IdleControllerComp");
            Type tweak = RimKataActiveModTypes.Find("AM.Tweaks.ItemTweakData");
            var r = Expression.Parameter(typeof(object), "renderer");
            var n = Expression.Parameter(typeof(string), "name");
            var i = Expression.Parameter(typeof(int), "index");
            getPart = Expression.Lambda<Func<object, string, object>>(
                Expression.Convert(Expression.Call(Expression.Convert(r, renderer),
                    AccessTools.Method(renderer, "GetPart", new[] { typeof(string) }), n), typeof(object)), r, n).Compile();
            getOverride = Expression.Lambda<Func<object, int, object>>(
                Expression.Convert(Expression.Call(Expression.Convert(r, renderer),
                    AccessTools.Method(renderer, "GetOverride", new[] { typeof(int) }), i), typeof(object)), r, i).Compile();
            partIndex = Getter<int>(part, "Index");
            definition = Getter<object>(renderer, "Def");
            animationType = Getter<int>(def, "type");
            idleType = Convert.ToInt32(Enum.Parse(AccessTools.Field(def, "type").FieldType, "Idle"));
            weapon = Getter<Thing>(ov, "Weapon");
            destroyed = Getter<bool>(renderer, "IsDestroyed");
            mirrorX = Getter<bool>(renderer, "MirrorHorizontal");
            mirrorY = Getter<bool>(renderer, "MirrorVertical");
            root = Getter<Matrix4x4>(renderer, "RootTransform");
            map = Getter<Map>(renderer, "Map");
            var pawn = Expression.Parameter(typeof(Pawn), "pawn");
            var c = Expression.Variable(comp, "comp");
            controller = Expression.Lambda<Func<Pawn, object>>(Expression.Convert(Expression.Call(pawn,
                AccessTools.Method(typeof(ThingWithComps), "GetComp").MakeGenericMethod(comp)), typeof(object)), pawn).Compile();
            currentAnimation = Expression.Lambda<Func<Pawn, object>>(Expression.Block(new[] { c },
                Expression.Assign(c, Expression.Call(pawn,
                    AccessTools.Method(typeof(ThingWithComps), "GetComp").MakeGenericMethod(comp))),
                Expression.Condition(Expression.Equal(c, Expression.Constant(null, comp)),
                    Expression.Constant(null, typeof(object)),
                    Expression.Convert(Expression.Property(c, "CurrentAnimation"), typeof(object)))), pawn).Compile();

            var ss = Expression.Variable(snapshot, "snapshot");
            readPose = Expression.Lambda<Func<object, int, Pose>>(Expression.Block(new[] { ss },
                Expression.Assign(ss, Expression.ArrayIndex(Expression.Field(Expression.Convert(r, renderer),
                    AccessTools.Field(renderer, "snapshots")), i)),
                Expression.New(typeof(Pose).GetConstructor(new[] { typeof(Matrix4x4), typeof(Matrix4x4), typeof(bool), typeof(bool) }),
                    Expression.Field(ss, "WorldMatrix"), Expression.Field(ss, "WorldMatrixNoOverride"),
                    Expression.Field(ss, "FlipX"), Expression.Field(ss, "FlipY"))), r, i).Compile();
            getTweak = (Func<ThingDef, object>)Delegate.CreateDelegate(typeof(Func<ThingDef, object>),
                AccessTools.Method(RimKataActiveModTypes.Find("AM.Tweaks.TweakDataManager"), "TryGetTweak", new[] { typeof(ThingDef) }));
            offX = Getter<float>(tweak, "OffX"); offY = Getter<float>(tweak, "OffY");
            scaleX = Getter<float>(tweak, "ScaleX"); scaleY = Getter<float>(tweak, "ScaleY");
            rotation = Getter<float>(tweak, "Rotation");
            flipX = Getter<bool>(tweak, "FlipX"); flipY = Getter<bool>(tweak, "FlipY");
            getMesh = (Func<bool, bool, Mesh>)Delegate.CreateDelegate(typeof(Func<bool, bool, Mesh>),
                AccessTools.Method(RimKataActiveModTypes.Find(renderer.Assembly, "AnimData"), "GetMesh"));
            shouldDraw = AccessTools.Method(renderer, "ShouldDraw", new[] { snapshot.MakeByRefType() });
            drawMesh = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), new[]
            {
                typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int),
                typeof(Camera), typeof(int), typeof(MaterialPropertyBlock)
            });
            filterBridge = CreatePartFilter(renderer, snapshot, part);
            RimKataMeleeAnimationWeaponDraw.BindApi(renderer, snapshot, ov);

            try
            {
                RimKataStartupPatches.Patch(harmony, AccessTools.Method(renderer, "Draw"), prefix: Hook(nameof(Begin)),
                    transpiler: Hook(nameof(Transpiler)), postfix: Hook(nameof(FinishDraw)), finalizer: Hook(nameof(End)));
                RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(RimKataDualWeaponRenderUtility), "TryDrawPair"),
                    prefix: Hook(nameof(PairPrefix)));
                RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(RimKataDualWeaponRenderUtility), "DrawWeapon"),
                    prefix: Hook(nameof(DrawSlot)));
                RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(RimKataWeaponRenderProbe), "NotifySecondaryDraw"),
                    postfix: Hook(nameof(SecondaryDrawn)));
                RimKataStartupPatches.Patch(harmony, AccessTools.Method(RimKataActiveModTypes.Find(
                    "AM.Patches.Patch_PawnRenderer_DrawEquipment"), "Prefix", new[] { typeof(Thing) }),
                    prefix: Hook(nameof(RangedEquipmentPrefix)));
            }
            catch
            {
                failed = true;
                throw;
            }
            EquipmentRenderingEnabled = true;
            RimKataMeleeAnimationReplay.Apply(harmony);
        }

        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(RimKataMeleeAnimationCompat), name);

        internal static Func<object, T> Getter<T>(Type type, string name)
        {
            var value = Expression.Parameter(typeof(object), "value");
            return Expression.Lambda<Func<object, T>>(Expression.Convert(
                Expression.PropertyOrField(Expression.Convert(value, type), name), typeof(T)), value).Compile();
        }

        private static MethodInfo CreatePartFilter(Type renderer, Type snapshot, Type part)
        {
            var method = new DynamicMethod("RimKata_MeleeAnimation_FilterPart", typeof(bool),
                new[] { renderer, snapshot.MakeByRefType() }, typeof(RimKataMeleeAnimationCompat), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, shouldDraw);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldfld, AccessTools.Field(snapshot, "Part"));
            il.Emit(OpCodes.Ldfld, AccessTools.Field(part, "Index"));
            il.Emit(OpCodes.Call, AccessTools.Method(typeof(RimKataMeleeAnimationCompat), nameof(FilterPart)));
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int filters = 0, draws = 0;
            foreach (CodeInstruction code in codes)
            {
                if (code.Calls(shouldDraw))
                {
                    code.opcode = OpCodes.Call; code.operand = filterBridge; filters++;
                }
                else if (code.Calls(drawMesh))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = AccessTools.Method(typeof(RimKataMeleeAnimationCompat), nameof(Submit)); draws++;
                }
            }
            if (filters != 1 || draws != 1)
                throw new InvalidOperationException("Melee Animation draw layout did not match; no render hooks applied.");
            return codes;
        }

        private static bool FilterPart(bool visible, int index)
        {
            if (breachOwnedFrame != null)
                return visible && index != breachOwnedFrame.Item
                    && index != breachOwnedFrame.MainHand && index != breachOwnedFrame.OtherHand;
            if (current == null) return visible;
            current.Part = index;
            return visible && index != current.OtherHand;
        }

        private static void Begin(object __instance, bool cullDraw, out DrawScope __state)
        {
            __state = new DrawScope { previous = current, previousBreach = breachOwnedFrame };
            current = null;
            breachOwnedFrame = null;
            if (failed || cullDraw || destroyed(__instance) || map(__instance) != Find.CurrentMap
                || Find.CurrentMap == null) return;
            try
            {
                Frame frame = frames.GetValue(__instance, value => new Frame(value));
                if (!frame.Prepare(out bool breachOwned, allowBreach: true)) return;
                if (breachOwned)
                {
                    breachOwnedFrame = frame;
                    return;
                }
                frame.Part = -1;
                frame.WeaponDrawn = false;
                frame.ReplayDrawn = false;
                frame.ReplayAttempted = false;
                frame.NativeSecondaryCombat = RimKataMeleeAnimationReplay.UsesNativeRangedCombat(frame);
                frame.Replay = RimKataMeleeAnimationReplay.Prepare(frame);
                current = frame;
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static Exception End(Exception __exception, DrawScope __state)
        {
            current = __state.previous;
            breachOwnedFrame = __state.previousBreach;
            return __exception;
        }

        private static void FinishDraw()
        {
            if (current?.NativeSecondaryCombat == true)
            {
                Vector3 root = EquipmentRoot(current.Pawn, out float bodyAltitude);
                RimKataDualWeaponRenderUtility.DrawSecondaryAfterExternalPrimary(current.Pawn,
                    current.Primary, current.Secondary, root, bodyAltitude, combat: true);
                return;
            }
            if (current?.Replay != null && !current.ReplayAttempted)
                RimKataMeleeAnimationReplay.Draw(current);
        }

        private static bool RangedEquipmentPrefix(Thing eq, ref bool __result)
        {
            // MA's original prefix tests Primary even when eq is the offhand gun.
            if (eq?.def.IsRangedWeapon != true) return true;
            Pawn pawn = RimKataVisualUtility.FindPawnOwner(eq);
            if (!RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn, out ThingWithComps secondary)
                || (eq != pawn.equipment?.Primary && eq != secondary)) return true;
            __result = true;
            return false;
        }

        internal static bool TryGetFrame(Pawn pawn, ThingWithComps secondary, out Frame frame)
        {
            frame = null;
            if (failed || pawn == null || secondary == null) return false;
            object renderer = currentAnimation(pawn);
            if (renderer == null || destroyed(renderer)) return false;
            Frame candidate = frames.GetValue(renderer, value => new Frame(value));
            if (!candidate.Prepare() || candidate.Secondary != secondary) return false;
            frame = candidate;
            return true;
        }

        private static bool OwnsRender(Pawn pawn)
        {
            if (failed || pawn?.Spawned != true || pawn.Dead || pawn.Downed
                || !RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn, out ThingWithComps secondary)
                || secondary == null) return false;
            try
            {
                object renderer = currentAnimation(pawn);
                return renderer != null && !destroyed(renderer)
                    && frames.GetValue(renderer, value => new Frame(value)).Prepare();
            }
            catch (Exception exception)
            {
                Fail(exception);
                return false;
            }
        }

        private static bool PairPrefix(Thing equipment, ref bool __result)
        {
            Pawn pawn = RimKataVisualUtility.FindPawnOwner(equipment);
            if (!OwnsRender(pawn)) return true;
            __result = false;
            return false;
        }

        private static Vector3 EquipmentRoot(Pawn pawn, out float bodyAltitude)
        {
            if (equipmentFrames.TryGetValue(pawn, out EquipmentFrame frame))
            {
                bodyAltitude = frame.BodyRoot.y;
                return frame.Root;
            }
            Vector3 root = pawn.DrawPos;
            bodyAltitude = root.y;
            return root;
        }

        internal static void BeginEquipment(Pawn pawn, Vector3 drawPos, Rot4 facing, PawnRenderFlags flags,
            out EquipmentFrame __state)
        {
            __state = null;
            if (failed || !RimKataMeleeAnimationReplay.Active || RimKataWeaponRenderProbe.Probing || (flags & PawnRenderFlags.Portrait) != 0
                || pawn?.Spawned != true || pawn.Dead || pawn.Downed) return;
            RimKataResponseVisualParticipantCache.BodyVisualEntry visual = RimKataWorldRenderContext.BodyFor(pawn);
            if (visual?.registeredQualified != true || visual.equipmentSecondary == null
                || RimKataBreachWeaponRender.Owns(pawn) || RimKataReactiveRender.Owns(pawn)) return;
            EquipmentFrame frame = equipmentFrames.GetValue(pawn, _ => new EquipmentFrame());
            frame.Pawn = pawn; frame.Primary = visual.equipmentPrimary; frame.Secondary = visual.equipmentSecondary;
            frame.Root = drawPos; frame.Facing = facing; frame.Flags = flags; frame.SecondaryDrawn = false;
            frame.BodyRoot = drawPos;
            frame.BodyRoot.y = RimKataWorldRenderContext.EquipmentBodyAltitude(pawn, drawPos, facing);
            frame.DrawingEquipment = true;
            __state = frame;
        }

        internal static void EndEquipment(bool completed, EquipmentFrame __state)
        {
            if (__state == null) return;
            __state.DrawingEquipment = false;
            if (!completed || failed) return;
            try
            {
                if (OwnsRender(__state.Pawn))
                {
                    DrawExtras(__state);
                    RimKataWeaponRenderProbe.NotifySecondaryDraw(__state.Secondary);
                }
                else if (!__state.SecondaryDrawn
                    && RimKataMeleeAnimationReplay.TryDrawStandalone(__state.Pawn, __state.Secondary, __state.BodyRoot))
                {
                    DrawExtras(__state);
                    RimKataWeaponRenderProbe.NotifySecondaryDraw(__state.Secondary);
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        private static void SecondaryDrawn(Thing weapon)
        {
            Pawn pawn = RimKataVisualUtility.FindPawnOwner(weapon);
            if (pawn != null && equipmentFrames.TryGetValue(pawn, out EquipmentFrame frame)
                && weapon == frame.Secondary) frame.SecondaryDrawn = true;
        }

        private static void DrawExtras(EquipmentFrame frame)
            => RimKataWeaponRenderProbe.DrawSecondaryExtras(frame.Pawn, frame.Primary, frame.Secondary,
                frame.Root, frame.Facing, frame.Flags);

        private static bool DrawSlot(Pawn pawn, ThingWithComps primary, ThingWithComps weapon, bool secondary,
            ref Vector3 primaryDrawLoc, ref Vector3 equipmentPivot, ref float fallbackAngle)
        {
            if (failed || RimKataWeaponRenderProbe.Probing || pawn == null
                || !equipmentFrames.TryGetValue(pawn, out EquipmentFrame frame)
                || !frame.DrawingEquipment || frame.Primary != primary || frame.Secondary == null) return true;
            if (secondary && weapon == frame.Secondary
                && RimKataMeleeAnimationReplay.TryDrawStandalone(pawn, weapon, frame.BodyRoot))
            {
                DrawExtras(frame);
                RimKataWeaponRenderProbe.NotifySecondaryDraw(weapon);
                return false;
            }
            if (!secondary && weapon?.def.IsRangedWeapon == true
                && frame.Secondary.def.IsMeleeWeapon && pawn.stances?.curStance is Stance_Busy busy
                && busy.verb?.EquipmentSource == frame.Secondary)
            {
                // The shared body stance may carry the other hand's thrust offset.
                equipmentPivot = frame.Root;
                if (RimKataWeaponRenderProbe.TryGetVanillaIdlePose(pawn, weapon, frame.Root,
                    out Vector3 idleLoc, out float idleAngle, frame.Facing))
                {
                    primaryDrawLoc = idleLoc;
                    fallbackAngle = idleAngle;
                }
            }
            return true;
        }

        private static void Submit(Mesh mesh, Matrix4x4 matrix, Material material, int layer,
            Camera camera, int submesh, MaterialPropertyBlock properties)
        {
            Graphics.DrawMesh(mesh, RimKataGroundPoseRender.TransformEquipment(current?.Pawn, matrix),
                material, layer, camera, submesh, properties);
            Frame frame = current;
            if (frame == null || failed || frame.NativeSecondaryCombat) return;
            try
            {
                if (frame.Replay != null && (frame.Part == frame.Item || frame.Part == frame.MainHand))
                {
                    int originalPart = frame.Part;
                    if (!frame.ReplayAttempted)
                    {
                        frame.ReplayAttempted = true;
                        frame.ReplayDrawn = RimKataMeleeAnimationReplay.Draw(frame);
                        frame.Part = originalPart;
                    }
                    if (frame.ReplayDrawn) return;
                }
                if (frame.Part == frame.MainHand)
                {
                    // Submit immediately: MA reuses/clears its MPB for the next part.
                    DrawMirrored(frame, mesh, matrix, material, layer, camera, submesh, properties);
                }
                else if (frame.Part == frame.Item && !frame.WeaponDrawn)
                {
                    frame.WeaponDrawn = true;
                    Pose pose = readPose(frame.Renderer, frame.Item);
                    bool mx = mirrorX(frame.Renderer), my = mirrorY(frame.Renderer);
                    bool fx = pose.FlipX ^ frame.FlipX, fy = pose.FlipY ^ frame.FlipY;
                    Matrix4x4 s = Matrix4x4.Scale(new Vector3(mx ? -1f : 1f, 1f, my ? -1f : 1f));
                    Matrix4x4 tweak = Matrix4x4.TRS(new Vector3(fx ? -frame.Offset.x : frame.Offset.x,
                        0f, fy ? -frame.Offset.y : frame.Offset.y),
                        Quaternion.AngleAxis(fx ^ fy ? -frame.Angle : frame.Angle, Vector3.up), frame.Scale);
                    Matrix4x4 basis = root(frame.Renderer);
                    Matrix4x4 adjustment = matrix * (basis * pose.Matrix).inverse;
                    Matrix4x4 secondaryMatrix = adjustment * basis * pose.WithoutTweak * s * tweak * s;
                    RimKataMeleeAnimationWeaponDraw.Draw(frame, frame.Renderer, frame.Item,
                        getMesh(fx ^ mx, fy ^ my), secondaryMatrix, layer, camera);
                }
            }
            catch (Exception exception) { Fail(exception); }
        }

        internal static void DrawMirrored(Frame frame, Mesh mesh, Matrix4x4 matrix, Material material,
            int layer, Camera camera, int submesh, MaterialPropertyBlock properties, object poseRenderer = null)
        {
            if (mesh == null || material == null
                || !RimKataWeaponDrawCapture.TryGetMirroredMesh(mesh, out Mesh mirrored)) return;
            poseRenderer = poseRenderer ?? frame.Renderer;
            Matrix4x4 basis = root(poseRenderer);
            float a = frame.Pawn.Rotation.AsAngle * Mathf.Deg2Rad;
            float x = Mathf.Sin(a), z = Mathf.Cos(a);
            Matrix4x4 reflection = Matrix4x4.identity;
            reflection.m00 = 2f * x * x - 1f;
            reflection.m02 = reflection.m20 = 2f * x * z;
            reflection.m22 = 2f * z * z - 1f;
            // Reflect in the animated pawn's space, including MA's target rotation.
            matrix = basis * reflection * basis.inverse * matrix;
            matrix.m00 = -matrix.m00; matrix.m10 = -matrix.m10;
            matrix.m20 = -matrix.m20; matrix.m30 = -matrix.m30;
            if (frame.Pawn.Rotation.IsHorizontal && !RimKataGroundPoseRender.WeaponsAboveBody(frame.Pawn))
            {
                int item = poseRenderer == frame.Renderer ? frame.Item : partIndex(getPart(poseRenderer, "ItemA"));
                float weaponDepth = (basis * readPose(poseRenderer, item).Matrix).m13;
                matrix.m13 += 2f * (basis.m13 - weaponDepth);
            }
            matrix.m13 -= 0.001f;
            Graphics.DrawMesh(mirrored, RimKataGroundPoseRender.TransformEquipment(frame.Pawn, matrix),
                material, layer, camera, submesh, properties);
        }

        private static void Fail(Exception exception)
        {
            if (failed) return;
            failed = true;
            current = null;
            Log.Warning("[RimKata] Melee Animation secondary rendering disabled after an error: " + exception);
        }

        internal readonly struct Pose
        {
            public readonly Matrix4x4 Matrix, WithoutTweak;
            public readonly bool FlipX, FlipY;

            public Pose(Matrix4x4 matrix, Matrix4x4 withoutTweak, bool flipX, bool flipY)
            {
                Matrix = matrix; WithoutTweak = withoutTweak;
                FlipX = flipX; FlipY = flipY;
            }
        }

        private struct DrawScope
        {
            internal Frame previous, previousBreach;
        }

        internal sealed class Frame
        {
            internal readonly object Renderer, ItemOverride;
            internal readonly int Item = -1, MainHand = -1, OtherHand = -1;
            internal Pawn Pawn;
            internal int Part;
            internal bool WeaponDrawn, FlipX, FlipY;
            internal bool ReplayDrawn, ReplayAttempted;
            internal bool NativeSecondaryCombat;
            internal Vector2 Offset;
            internal Vector3 Scale;
            internal Vector3 DrawRoot;
            internal float Angle;
            internal Material Material;
            internal RimKataMeleeAnimationReplay.Playback Replay;
            internal object SecondaryOverride;
            internal MaterialPropertyBlock WeaponProperties;
            private ThingWithComps lastSecondary;
            private readonly ThingWithComps standalonePrimary;
            internal ThingWithComps Secondary => lastSecondary;
            internal bool Standalone => standalonePrimary != null;
            internal ThingWithComps Primary => standalonePrimary ?? weapon(ItemOverride) as ThingWithComps;

            internal Frame(object renderer, bool secondaryOnly = false)
            {
                Renderer = renderer;
                if (animationType(definition(renderer)) != idleType) return;
                object item = getPart(renderer, "ItemA"), hand = getPart(renderer, "HandA"), other = getPart(renderer, "HandB");
                if (item == null) return;
                Item = partIndex(item); MainHand = hand == null ? -1 : partIndex(hand);
                OtherHand = other == null ? -1 : partIndex(other);
                ItemOverride = getOverride(renderer, Item);
            }

            internal Frame(object renderer, Pawn pawn, ThingWithComps primary) : this(renderer, true)
            {
                Pawn = pawn;
                standalonePrimary = primary;
            }

            internal bool Prepare()
                => Prepare(out _, allowBreach: false);

            internal bool Prepare(out bool breachOwned, bool allowBreach)
            {
                breachOwned = false;
                if (ItemOverride == null) return false;
                var primary = weapon(ItemOverride) as ThingWithComps;
                Pawn = RimKataVisualUtility.FindPawnOwner(primary);
                if (Pawn?.Spawned != true || Pawn.Dead || Pawn.Downed || Pawn.carryTracker?.CarriedThing != null)
                    return false;
                if (allowBreach && (RimKataBreachWeaponRender.Owns(Pawn) || RimKataReactiveRender.Owns(Pawn))
                    && primary == Pawn.equipment?.Primary)
                {
                    breachOwned = true;
                    return true;
                }
                if (MainHand < 0 || (!Standalone && OtherHand < 0)
                    || !RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(Pawn, out ThingWithComps registered)
                    || registered == null
                    || !ReferenceEquals(currentAnimation(Pawn), Renderer)
                    || !RimKataVisualUtility.TryGetCachedWorldLoadout(Pawn, out ThingWithComps held, out ThingWithComps secondary)
                    || held != primary || !RimKataVisualUtility.IsSecondaryUsable(Pawn, primary, secondary)) return false;
                return PrepareWeapon(secondary);
            }

            internal bool PrepareWeapon(ThingWithComps secondary)
            {
                if (ItemOverride == null || MainHand < 0 || (!Standalone && OtherHand < 0)) return false;
                if (lastSecondary != secondary)
                {
                    object tweak = getTweak(secondary.def);
                    Offset = tweak == null ? Vector2.zero : new Vector2(offX(tweak), offY(tweak));
                    Scale = tweak == null
                        ? new Vector3(secondary.def.graphicData?.drawSize.x ?? 1f, 1f, secondary.def.graphicData?.drawSize.y ?? 1f)
                        : new Vector3(scaleX(tweak), 1f, scaleY(tweak));
                    Angle = tweak == null ? 0f : rotation(tweak);
                    FlipX = tweak != null && flipX(tweak); FlipY = tweak != null && flipY(tweak);
                    lastSecondary = secondary;
                }
                Material = secondary.Graphic?.MatSingleFor(secondary);
                return Material != null && Scale.x != 0f && Scale.z != 0f;
            }
        }

        internal sealed class EquipmentFrame
        {
            internal Pawn Pawn;
            internal ThingWithComps Primary, Secondary;
            internal Vector3 Root, BodyRoot;
            internal Rot4 Facing;
            internal PawnRenderFlags Flags;
            internal bool SecondaryDrawn, DrawingEquipment;
        }
    }
}
