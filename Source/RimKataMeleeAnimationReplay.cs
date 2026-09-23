using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;
using static KRWF.RimKata.RimKataMeleeAnimationCompat;
using Pose = KRWF.RimKata.RimKataMeleeAnimationCompat.Pose;

namespace KRWF.RimKata
{
    // RimKata owns only the secondary clock and pose buffers. Installed MA data
    // and its part evaluator supply the motion; no source animator is advanced,
    // registered, drawn again, or asked to emit sound/damage/animation events.
    internal static class RimKataMeleeAnimationReplay
    {
        private static readonly ConditionalWeakTable<Pawn, Playback> playback = new ConditionalWeakTable<Pawn, Playback>();
        private static Func<object, Map, object> createRenderer;
        private static Action<object, float, bool, bool> evaluate;
        private static Action<object, Matrix4x4> setRoot;
        private static Action<object, object> copyHand;
        private static Action<object, Pawn, int> configureHands;
        private static Func<object, Rot4, IList> attacks;
        private static Func<object, object> idle, moveHorizontal, moveVertical;
        private static Func<object, float> duration, probability, returnStart, returnEnd, angleOffset, idleFrame, currentTime;
        private static Func<object, bool> pointAtTarget, overrideFlipX, overrideFlipY;
        private static Func<object, int> idleKind, pawnCount;
        private static Func<object, Type> rendererWorker;
        private static Func<object, int, bool> visible;
        private static Func<object, int, Texture> texture;
        private static Func<object, int, Color> color;
        private static Func<object, Camera> camera;
        private static Func<bool> animationsEnabled;
        private static Func<Pawn, bool> leftHanded;
        private static Func<Pawn, float> sourceAim;
        private static Func<object, int, Material> handMaterial;
        private static int attackNorth, attackSouth, attackHorizontal;
        private static int mainTexture, mainColor;
        private static bool enabled;
        internal static bool Active => enabled && animationsEnabled();

        internal static void Apply(Harmony harmony)
        {
            // Called only after the optional MA renderer API has been bound.
            try
            {
                Type renderer = AccessTools.TypeByName("AM.AnimRenderer");
                if (renderer == null) return;
                BindApi(renderer);
                mainTexture = Shader.PropertyToID("_MainTex"); mainColor = Shader.PropertyToID("_Color");
                RimKataMeleeAnimationAttackBridge.Apply(harmony);
                enabled = true;
            }
            catch (Exception exception) { Disable(exception); }
        }

        private static void BindApi(Type renderer)
        {
            Type def = AccessTools.TypeByName("AM.AnimDef");
            Type tweak = AccessTools.TypeByName("AM.Tweaks.ItemTweakData");
            Type part = AccessTools.TypeByName("AnimPartData");
            Type snapshot = AccessTools.TypeByName("AnimPartSnapshot");
            Type ov = AccessTools.TypeByName("AnimPartOverrideData");
            createRenderer = Bind<Func<object, Map, object>>(renderer.GetConstructor(new[] { def, typeof(Map) }));
            attacks = Bind<Func<object, Rot4, IList>>(AccessTools.Method(tweak, "GetAttackAnimations"));
            idle = Bind<Func<object, object>>(AccessTools.Method(tweak, "GetIdleAnimation"));
            moveHorizontal = Bind<Func<object, object>>(AccessTools.Method(tweak, "GetMoveHorizontalAnimation"));
            moveVertical = Bind<Func<object, object>>(AccessTools.Method(tweak, "GetMoveVerticalAnimation"));
            duration = Getter<float>(renderer, "Duration"); camera = Getter<Camera>(renderer, "Camera");
            currentTime = Getter<float>(renderer, "CurrentTime");
            probability = Getter<float>(def, "Probability"); idleKind = Getter<int>(def, "idleType");
            pawnCount = Getter<int>(def, "pawnCount"); rendererWorker = Getter<Type>(def, "rendererWorker");
            pointAtTarget = Getter<bool>(def, "pointAtTarget"); angleOffset = Getter<float>(def, "pointAtTargetAngleOffset");
            returnStart = Getter<float>(def, "returnToIdleStart"); returnEnd = Getter<float>(def, "returnToIdleEnd");
            idleFrame = Getter<float>(def, "idleFrame");
            overrideFlipX = Getter<bool>(ov, "FlipX"); overrideFlipY = Getter<bool>(ov, "FlipY");
            Type kinds = AccessTools.Field(def, "idleType").FieldType;
            attackNorth = Convert.ToInt32(Enum.Parse(kinds, "AttackNorth"));
            attackSouth = Convert.ToInt32(Enum.Parse(kinds, "AttackSouth"));
            attackHorizontal = Convert.ToInt32(Enum.Parse(kinds, "AttackHorizontal"));
            var r = Expression.Parameter(typeof(object), "renderer");
            var m = Expression.Parameter(typeof(Matrix4x4), "matrix");
            setRoot = Expression.Lambda<Action<object, Matrix4x4>>(Expression.Assign(
                Expression.Field(Expression.Convert(r, renderer), "RootTransform"), m), r, m).Compile();
            var pawn = Expression.Parameter(typeof(Pawn), "pawn");
            Type controller = AccessTools.TypeByName("AM.Idle.IdleControllerComp");
            animationsEnabled = Expression.Lambda<Func<bool>>(Expression.Field(Expression.Field(null,
                AccessTools.Field(AccessTools.TypeByName("AM.Core"), "Settings")), "AnimateAtIdle")).Compile();
            var handIndex = Expression.Parameter(typeof(int), "handIndex");
            configureHands = Expression.Lambda<Action<object, Pawn, int>>(Expression.Call(
                Expression.Convert(r, renderer), AccessTools.Method(renderer, "ConfigureHandsForPawn"), pawn, handIndex),
                r, pawn, handIndex).Compile();
            Expression comp = Expression.Call(pawn, AccessTools.Method(typeof(ThingWithComps), "GetComp").MakeGenericMethod(controller));
            leftHanded = Expression.Lambda<Func<Pawn, bool>>(Expression.Call(comp,
                AccessTools.Method(controller, "IsLeftHanded")), pawn).Compile();
            sourceAim = Expression.Lambda<Func<Pawn, float>>(Expression.Field(comp,
                AccessTools.Field(controller, "pauseAngle")), pawn).Compile();
            BuildPoseAccess(renderer, part, snapshot, ov);
        }

        private static T Bind<T>(MethodBase method) where T : Delegate
        {
            if (method == null) throw new MissingMethodException("Melee Animation playback API did not match.");
            var signature = typeof(T).GetMethod("Invoke");
            var inputs = signature.GetParameters();
            var p = new ParameterExpression[inputs.Length];
            for (int i = 0; i < p.Length; i++) p[i] = Expression.Parameter(inputs[i].ParameterType, "p" + i);
            var args = method.GetParameters();
            int skip = method is MethodInfo info && !info.IsStatic ? 1 : 0;
            var converted = new Expression[args.Length];
            for (int i = 0; i < args.Length; i++) converted[i] = Expression.Convert(p[i + skip], args[i].ParameterType);
            Expression call = method is ConstructorInfo ctor ? (Expression)Expression.New(ctor, converted)
                : Expression.Call(skip == 0 ? null : Expression.Convert(p[0], method.DeclaringType), (MethodInfo)method, converted);
            return Expression.Lambda<T>(Expression.Convert(call, signature.ReturnType), p).Compile();
        }

        private static void BuildPoseAccess(Type renderer, Type part, Type snapshot, Type ov)
        {
            var r = Expression.Parameter(typeof(object), "renderer");
            var t = Expression.Parameter(typeof(float), "time");
            var mx = Expression.Parameter(typeof(bool), "mirrorX");
            var my = Expression.Parameter(typeof(bool), "mirrorY");
            var i = Expression.Variable(typeof(int), "i");
            Expression self = Expression.Convert(r, renderer);
            Expression array = Expression.Field(self, AccessTools.Field(renderer, "snapshots"));
            Expression parts = Expression.Property(Expression.Property(self, "Data"), "Parts");
            Expression element = Expression.ArrayAccess(array, i);
            Expression create = Expression.Assign(element, Expression.New(snapshot.GetConstructor(new[] { part, renderer, typeof(float) }),
                Expression.MakeIndex(parts, parts.Type.GetProperty("Item"), new[] { i }), self, t));
            var updated = Expression.Variable(snapshot, "updated");
            // Explicitly store evaluated structs back into the snapshot array;
            // both the submitter and child parts must see the updated matrices.
            Expression update = Expression.Block(Expression.Assign(updated, element),
                Expression.Call(updated, AccessTools.Method(snapshot, "UpdateWorldMatrix"), mx, my),
                Expression.Assign(element, updated));
            evaluate = Expression.Lambda<Action<object, float, bool, bool>>(Expression.Block(new[] { i, updated },
                Expression.Assign(Expression.Field(self, "MirrorHorizontal"), mx),
                Expression.Assign(Expression.Field(self, "MirrorVertical"), my),
                Expression.Assign(Expression.Field(self, AccessTools.Field(renderer, "time")), t),
                Loop(i, Expression.ArrayLength(array), create), Loop(i, Expression.ArrayLength(array), update)), r, t, mx, my).Compile();

            var index = Expression.Parameter(typeof(int), "index");
            var ss = Expression.Variable(snapshot, "snapshot");
            Expression load = Expression.Assign(ss, Expression.ArrayIndex(array, index));
            visible = Expression.Lambda<Func<object, int, bool>>(Expression.Block(new[] { ss }, load,
                Expression.Call(self, AccessTools.Method(renderer, "ShouldDraw", new[] { snapshot.MakeByRefType() }), ss)), r, index).Compile();
            texture = Expression.Lambda<Func<object, int, Texture>>(Expression.Block(new[] { ss }, load,
                Expression.Convert(Expression.Call(AccessTools.Method(renderer, "ResolveTexture", new[] { snapshot.MakeByRefType() }), ss), typeof(Texture))), r, index).Compile();
            color = Expression.Lambda<Func<object, int, Color>>(Expression.Block(new[] { ss }, load,
                Expression.Property(ss, "FinalColor")), r, index).Compile();
            var forceMPB = Expression.Variable(typeof(bool), "forceMPB");
            handMaterial = Expression.Lambda<Func<object, int, Material>>(Expression.Block(new[] { ss, forceMPB }, load,
                Expression.Call(self, AccessTools.Method(renderer, "GetMaterialFor"), ss,
                    Expression.Constant(true), forceMPB)), r, index).Compile();
            var from = Expression.Parameter(typeof(object), "from");
            var to = Expression.Parameter(typeof(object), "to");
            var assignments = new List<Expression>();
            foreach (string field in new[] { "Texture", "Material", "PreventDraw", "ColorOverride", "ColorTint", "FlipX", "FlipY", "UseMPB", "UseDefaultTransparentMaterial" })
                assignments.Add(Expression.Assign(Expression.Field(Expression.Convert(to, ov), field), Expression.Field(Expression.Convert(from, ov), field)));
            copyHand = Expression.Lambda<Action<object, object>>(Expression.Block(assignments), from, to).Compile();
        }

        private static Expression Loop(ParameterExpression index, Expression count, Expression action)
        {
            var done = Expression.Label();
            return Expression.Block(Expression.Assign(index, Expression.Constant(0)),
                Expression.Loop(Expression.IfThenElse(Expression.LessThan(index, count),
                    Expression.Block(action, Expression.PostIncrementAssign(index), Expression.Empty()), Expression.Break(done)), done));
        }

        internal static bool CanHandle(Pawn pawn, ThingWithComps secondary)
        {
            if (!enabled || secondary?.def.IsRangedWeapon != false) return false;
            try
            {
                if (!TryGetCombatState(pawn, secondary, out Playback state)) return false;
                return state.Candidate(pawn.Rotation, false) != null;
            }
            catch (Exception exception) { Disable(exception); return false; }
        }

        internal static void NotifyAttack(Pawn pawn, ThingWithComps secondary, LocalTargetInfo target, int cooldownTicks)
        {
            if (!enabled || secondary?.def.IsRangedWeapon != false) return;
            try
            {
                if (!TryGetCombatState(pawn, secondary, out Playback state)) return;
                int tick = Find.TickManager.TicksGame;
                if (state.AttackDef != null && state.StartTick == tick && state.Target.Equals(target))
                {
                    state.Cooldown = Math.Max(1, cooldownTicks);
                    return;
                }
                object selected = state.Candidate(pawn.Rotation, true);
                if (selected == null) return;
                Sample sample = state.GetSample(selected);
                if (sample == null) return;
                state.AttackDef = selected;
                state.StartTick = tick;
                state.Cooldown = Math.Max(1, cooldownTicks);
                state.AttackFacing = pawn.Rotation;
                state.Target = target;
            }
            catch (Exception exception) { Disable(exception); }
        }

        internal static Playback Prepare(Frame frame)
        {
            if (!enabled || frame.Secondary.def.IsRangedWeapon) return null;
            try
            {
                Playback state = StateFor(frame);
                ExpireAttack(state);
                // Outside combat, retain the already-working live duplication.
                if (state.AttackDef != null) return state;
                return IsAttack(definition(frame.Renderer)) && state.GetSample(RestDef(frame)) != null ? state : null;
            }
            catch (Exception exception) { Disable(exception); return null; }
        }

        private static Playback StateFor(Frame frame)
            => StateFor(frame.Pawn, frame.Primary, frame.Secondary);

        private static Playback StateFor(Pawn pawn, ThingWithComps primary, ThingWithComps secondary)
        {
            Playback state = playback.GetValue(pawn, p => new Playback());
            if (state.Primary != primary || state.Secondary != secondary || state.Map != pawn.Map)
            {
                state.Samples.Clear(); state.AttackDef = null;
                state.Primary = primary; state.Secondary = secondary; state.Map = pawn.Map;
                state.PrimaryTweak = primary.def.IsMeleeWeapon ? getTweak(primary.def) : null;
                state.Tweak = getTweak(secondary.def) ?? state.PrimaryTweak;
            }
            return state;
        }

        private static bool TryGetCombatState(Pawn pawn, ThingWithComps secondary, out Playback state)
        {
            state = null;
            if (!animationsEnabled() || pawn?.Spawned != true || pawn.Dead || pawn.Downed || pawn.carryTracker?.CarriedThing != null
                || !RimKataEligibilityCache.TryGetRegisteredSecondaryWeapon(pawn, out ThingWithComps registered)
                || registered != secondary || controller(pawn) == null
                || !RimKataVisualUtility.TryGetCachedWorldLoadout(pawn, out ThingWithComps primary, out ThingWithComps held)
                || held != secondary || !RimKataVisualUtility.IsSecondaryUsable(pawn, primary, secondary)) return false;
            state = StateFor(pawn, primary, secondary);
            return true;
        }

        private static void ExpireAttack(Playback state)
        {
            if (state.AttackDef == null) return;
            int elapsed = Find.TickManager.TicksGame - state.StartTick;
            Sample attack = state.GetSample(state.AttackDef);
            int ticks = attack == null ? 0 : Math.Min(state.Cooldown, Math.Max(1, Mathf.CeilToInt(duration(attack.Renderer) * 60f)));
            if (elapsed < 0 || elapsed >= ticks) state.AttackDef = null;
        }

        internal static bool TryDrawStandalone(Pawn pawn, ThingWithComps weapon, Vector3 drawRoot)
        {
            // Attack playback belongs to the slot, not to the external-renderer
            // probe. Vanilla weapons need no discovered renderer to animate.
            if (!enabled || weapon?.def.IsMeleeWeapon != true || pawn == null
                || !playback.TryGetValue(pawn, out Playback state) || state.AttackDef == null) return false;
            try
            {
                if (!TryGetCombatState(pawn, weapon, out state) || TryGetFrame(pawn, weapon, out _)) return false;
                ExpireAttack(state);
                if (state.AttackDef == null) return false;
                Sample sample = state.GetSample(state.AttackDef);
                if (sample == null) return false;
                Frame frame = sample.StandaloneFrame ?? (sample.StandaloneFrame = new Frame(sample.Renderer, pawn, state.Primary));
                if (!frame.PrepareWeapon(weapon)) return false;
                frame.Replay = state;
                frame.DrawRoot = drawRoot;
                return Draw(frame);
            }
            catch (Exception exception) { Disable(exception); return false; }
        }

        private static bool IsAttack(object def)
        {
            int kind = idleKind(def);
            return kind == attackNorth || kind == attackSouth || kind == attackHorizontal;
        }

        internal static bool UsesNativeRangedCombat(Frame frame)
        {
            // Idle still follows the primary animator. During an attack the gun
            // uses its own aim, never the primary's melee swing or replay clock.
            return frame.Secondary.def.IsRangedWeapon
                && ((enabled && IsAttack(definition(frame.Renderer)))
                    || RimKataDualWeaponController.TryGetVisualData(frame.Pawn, frame.Secondary, out _));
        }

        private static object RestDef(Frame frame)
        {
            object tweak = getTweak(frame.Primary.def);
            if (tweak == null) return null;
            Rot4 facing = frame.Pawn.Rotation;
            bool busy = frame.Pawn.stances.curStance is Stance_Busy stance && !stance.neverAimWeapon && stance.focusTarg.IsValid;
            return facing == Rot4.South && !busy ? idle(tweak)
                : facing.IsHorizontal ? moveHorizontal(tweak) : moveVertical(tweak);
        }

        internal static bool Draw(Frame frame)
        {
            if (!enabled || frame.Secondary.def.IsRangedWeapon) return false;
            try
            {
                Playback state = frame.Replay;
                bool attacking = state.AttackDef != null;
                Rot4 facing = frame.Pawn.Rotation;
                if (attacking && facing != state.AttackFacing)
                {
                    object next = state.Candidate(facing, false);
                    if (next != null) state.AttackDef = next;
                    state.AttackFacing = facing;
                }
                // While only the primary attacks, hold the secondary's idle pose.
                object def = attacking ? state.AttackDef : RestDef(frame);
                Sample sample = state.GetSample(def);
                if (sample == null) return false;
                float length = duration(sample.Renderer);
                float time = attacking ? length * Mathf.Clamp01((Find.TickManager.TicksGame - state.StartTick)
                    / (float)Math.Min(state.Cooldown, Math.Max(1, Mathf.CeilToInt(length * 60f))))
                    : Mathf.Clamp(idleFrame(def) / 60f, 0f, length);
                bool mx = attacking ? facing == Rot4.West
                    : facing == Rot4.West || facing == (leftHanded(frame.Pawn) ? Rot4.South : Rot4.North);
                if (frame.Standalone)
                {
                    if (sample.HandsTick != state.StartTick)
                    {
                        configureHands(sample.Renderer, frame.Pawn, 0);
                        sample.HandsTick = state.StartTick;
                    }
                }
                else copyHand(getOverride(frame.Renderer, frame.MainHand), getOverride(sample.Renderer, sample.Hand));
                Matrix4x4 basis = BodyMatrix(frame, def, state.Target, time, attacking, mx);
                setRoot(sample.Renderer, basis);
                evaluate(sample.Renderer, time, mx, false);
                Camera drawCamera = camera(frame.Renderer);

                if (!visible(sample.Renderer, sample.Item)) return false;
                Pose pose = readPose(sample.Renderer, sample.Item);
                if (Mathf.Abs(basis.m33) < 0.5f || Mathf.Abs(pose.Matrix.m33) < 0.5f) return false;
                {
                    bool fx = pose.FlipX ^ frame.FlipX, fy = pose.FlipY ^ frame.FlipY;
                    Matrix4x4 s = Matrix4x4.Scale(new Vector3(mx ? -1f : 1f, 1f, 1f));
                    Matrix4x4 grip = Matrix4x4.TRS(new Vector3(fx ? -frame.Offset.x : frame.Offset.x, 0f,
                        fy ? -frame.Offset.y : frame.Offset.y), Quaternion.AngleAxis(fx ^ fy ? -frame.Angle : frame.Angle, Vector3.up), frame.Scale);
                    frame.Part = frame.Item;
                    RimKataMeleeAnimationWeaponDraw.Draw(frame, sample.Renderer, sample.Item,
                        getMesh(fx ^ mx, fy), basis * pose.WithoutTweak * s * grip * s, 0, drawCamera);
                }
                if (visible(sample.Renderer, sample.Hand))
                {
                    Pose hand = readPose(sample.Renderer, sample.Hand);
                    object ov = getOverride(sample.Renderer, sample.Hand);
                    sample.HandProperties.Clear();
                    sample.HandProperties.SetTexture(mainTexture, texture(sample.Renderer, sample.Hand));
                    sample.HandProperties.SetColor(mainColor, color(sample.Renderer, sample.Hand));
                    frame.Part = frame.MainHand;
                    DrawMirrored(frame, getMesh(hand.FlipX ^ overrideFlipX(ov) ^ mx, hand.FlipY ^ overrideFlipY(ov)),
                        basis * hand.Matrix, handMaterial(sample.Renderer, sample.Hand), 0, drawCamera, 0, sample.HandProperties, sample.Renderer);
                }
                return true;
            }
            catch (Exception exception) { Disable(exception); return false; }
        }

        private static Matrix4x4 BodyMatrix(Frame frame, object def, LocalTargetInfo target, float time, bool attack, bool mirrored)
        {
            // Use the actual MA draw root, not a pawn-render scratch buffer that
            // may already belong to a different cached draw. Remove only its aim.
            object source = frame.Standalone ? null : frame.Renderer;
            object original = source == null ? null : definition(source);
            Matrix4x4 basis = frame.Standalone
                ? Matrix4x4.TRS(frame.DrawRoot + new Vector3(0f,
                    frame.Pawn.Rotation == Rot4.North ? -0.8f : 0.1f, 0f), Quaternion.identity, Vector3.one)
                : root(frame.Renderer);
            if (original != null && pointAtTarget(original))
                basis *= Matrix4x4.Rotate(Quaternion.AngleAxis(-AimAngle(original,
                    sourceAim(frame.Pawn), currentTime(source), mirrorX(source)), Vector3.up));
            if (!attack || !pointAtTarget(def) || !target.IsValid) return basis;
            Pawn pawn = frame.Pawn;
            Vector3 difference = (target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted()) - pawn.DrawPos;
            float targetAngle = Mathf.Atan2(difference.z, difference.x) * Mathf.Rad2Deg;
            return basis * Matrix4x4.Rotate(Quaternion.AngleAxis(AimAngle(def, targetAngle, time, mirrored), Vector3.up));
        }

        private static float AimAngle(object def, float targetAngle, float time, bool mirrored)
        {
            float angle = -targetAngle;
            if (mirrored) angle -= 180f;
            int kind = idleKind(def);
            if (kind == attackNorth) angle += 90f;
            else if (kind == attackSouth) angle -= 90f;
            angle += angleOffset(def);
            float blend = Mathf.InverseLerp(returnStart(def), returnEnd(def), time * 60f);
            return Mathf.LerpAngle(angle, 0f, blend);
        }

        private static void Disable(Exception exception)
        {
            enabled = false;
            Log.Warning("[RimKata] Independent Melee Animation playback disabled; shared rendering remains available. " + exception);
        }

        internal sealed class Playback
        {
            internal readonly Dictionary<object, Sample> Samples = new Dictionary<object, Sample>();
            internal ThingWithComps Primary, Secondary;
            internal Map Map;
            internal object Tweak, PrimaryTweak, AttackDef;
            internal int StartTick, Cooldown, Sequence;
            internal Rot4 AttackFacing;
            internal LocalTargetInfo Target;

            internal object Candidate(Rot4 facing, bool advance)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    object tweak = pass == 0 ? Tweak : PrimaryTweak;
                    if (tweak == null || (pass == 1 && tweak == Tweak)) continue;
                    IList candidates = attacks(tweak, facing);
                    if (candidates == null || candidates.Count == 0) continue;
                    // Visual selection has its own sequence; never consume gameplay RNG.
                    int start = Sequence % candidates.Count;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        object candidate = candidates[(start + i) % candidates.Count];
                        if (candidate == null || pawnCount(candidate) != 1 || rendererWorker(candidate) != null
                            || !IsAttack(candidate) || probability(candidate) <= 0f || GetSample(candidate) == null) continue;
                        if (advance) Sequence = (start + i + 1) % candidates.Count;
                        return candidate;
                    }
                }
                return null;
            }

            internal Sample GetSample(object def)
            {
                if (def == null || pawnCount(def) != 1 || rendererWorker(def) != null) return null;
                if (Samples.TryGetValue(def, out Sample sample)) return sample;
                object renderer = createRenderer(def, Map);
                object item = getPart(renderer, "ItemA"), hand = getPart(renderer, "HandA");
                sample = item == null || hand == null || duration(renderer) <= 0f ? null
                    : new Sample(renderer, partIndex(item), partIndex(hand));
                if (Samples.Count >= 12) Samples.Clear();
                Samples.Add(def, sample);
                return sample;
            }
        }

        internal sealed class Sample
        {
            internal readonly object Renderer;
            internal readonly int Item, Hand;
            internal readonly MaterialPropertyBlock HandProperties = new MaterialPropertyBlock();
            internal Frame StandaloneFrame;
            internal int HandsTick = int.MinValue;

            internal Sample(object renderer, int item, int hand)
            {
                Renderer = renderer; Item = item; Hand = hand;
            }
        }
    }
}
