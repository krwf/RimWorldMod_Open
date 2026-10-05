using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal struct RimKataReactiveVisual
    {
        internal RimKataReactiveKind kind;
        internal Vector3 drawPosition;
        internal Rot4 facing;
        internal float progress, bodyAngle, primaryAngle, secondaryAngle;
        internal Vector3? primaryAimPoint, secondaryAimPoint;
        internal ThingWithComps primary, secondary;
        internal bool Falling => kind == RimKataReactiveKind.Sliding || kind == RimKataReactiveKind.StandingUp;
        internal bool OwnsWeapons => kind == RimKataReactiveKind.Sliding || kind == RimKataReactiveKind.ShakeOff;
    }

    internal static class RimKataReactiveRender
    {
        private struct Frame
        {
            internal Matrix4x4 transform;
            internal Vector3 shadow;
            internal float layer, northLift;
        }

        internal struct Scope
        {
            internal bool pushed;
        }

        private struct RenderContext
        {
            internal Pawn pawn;
            internal Vector3 root;
            internal PawnRenderFlags flags;
            internal RimKataReactiveVisual visual;
            internal Frame frame;
            internal bool drawn, frameActive;
        }

        private sealed class FrameSlot
        {
            internal Frame value;
            internal bool ready;
        }

        private static readonly ConcurrentDictionary<Pawn, FrameSlot> Frames = new ConcurrentDictionary<Pawn, FrameSlot>();
        private static readonly Func<Pawn, FrameSlot> CreateFrameSlot = _ => new FrameSlot();
        [ThreadStatic] private static RenderContext current;
        [ThreadStatic] private static Stack<RenderContext> parents;
        internal static bool Active => current.pawn != null && current.visual.OwnsWeapons;

        internal static void Publish(RimKataReactiveMotionState state)
        {
            if (state?.pawn == null) return;
            if (state.kind == RimKataReactiveKind.None)
            {
                Remove(state.pawn);
                return;
            }
            int elapsed = Math.Max(0, Find.TickManager.TicksGame - state.startTick);
            float progress = state.kind == RimKataReactiveKind.Sliding ? Mathf.Clamp01(elapsed / 6f)
                : state.kind == RimKataReactiveKind.StandingUp ? 1f - Mathf.Clamp01(elapsed / 6f) : 0f;
            Vector3 destination = state.destination.ToVector3Shifted();
            destination.y = state.startDrawPos.y;
            var visual = new RimKataReactiveVisual
            {
                kind = state.kind,
                drawPosition = state.kind == RimKataReactiveKind.Sliding
                    ? RimKataSlidingAttackOrigin.Center(state, Find.TickManager.TicksGame)
                    : state.kind == RimKataReactiveKind.StandingUp ? destination : state.startDrawPos,
                facing = state.kind == RimKataReactiveKind.ShakeOff ? Rot4.FromAngleFlat(state.primaryAngle) : state.facing,
                progress = progress,
                // Travel has eight directions even though body sprites have four.
                bodyAngle = Mathf.DeltaAngle(0f, state.startAngle + 180f) * progress,
                primary = state.primaryWeapon, secondary = state.secondaryWeapon,
                primaryAngle = state.primaryAngle, secondaryAngle = state.secondaryAngle,
                primaryAimPoint = state.kind == RimKataReactiveKind.Sliding && !state.primaryMelee
                    && state.primaryTarget?.Spawned == true ? state.primaryTarget.DrawPos : (Vector3?)null,
                secondaryAimPoint = state.kind == RimKataReactiveKind.Sliding && !state.secondaryMelee
                    && state.secondaryTarget?.Spawned == true ? state.secondaryTarget.DrawPos : (Vector3?)null
            };
            RimKataResponseVisualParticipantCache.PublishReactive(state.pawn, visual);
        }

        internal static void Remove(Pawn pawn)
        {
            if (pawn == null) return;
            RimKataResponseVisualParticipantCache.PublishReactive(pawn, null);
            Frames.TryRemove(pawn, out _);
        }

        internal static bool Owns(Pawn pawn)
        {
            if (pawn == null) return false;
            if (current.pawn == pawn) return current.visual.OwnsWeapons;
            var body = RimKataWorldRenderContext.BodyFor(pawn);
            return body?.kick.HasValue != true && body?.flyingKick.HasValue != true && body?.reactive?.OwnsWeapons == true;
        }

        internal static void PlaceBody(RimKataReactiveVisual visual, ref Vector3 drawLoc)
        {
            if (visual.kind == RimKataReactiveKind.StandingUp) return;
            drawLoc.x = visual.drawPosition.x;
            drawLoc.z = visual.drawPosition.z;
        }

        internal static void Prepare(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests,
            RimKataReactiveVisual visual)
        {
            if (!visual.Falling) return;
            Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 foot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            Matrix4x4 transform = Matrix4x4.Translate(foot)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(visual.bodyAngle, Vector3.up))
                * Matrix4x4.Translate(-foot);
            var frame = new Frame
            {
                transform = transform, shadow = transform.MultiplyPoint3x4(anchor),
                northLift = Mathf.Max(0f, anchor.z - foot.z) * 0.5f
                    * Mathf.Max(0f, Mathf.Cos((visual.progress > 0.001f
                        ? visual.bodyAngle / visual.progress
                        : visual.facing.AsAngle + 180f) * Mathf.Deg2Rad)) * visual.progress
            };
            float top = anchor.y;
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                request.preDrawnComputedMatrix = transform * request.preDrawnComputedMatrix;
                top = Mathf.Max(top, request.preDrawnComputedMatrix.m13);
                if (request.node.Props.tagDef == PawnRenderNodeTagDefOf.Body)
                    frame.shadow = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                requests[i] = request;
            }
            frame.layer = visual.progress * (top - anchor.y + PawnRenderUtility.AltitudeForLayer(100f));
            FrameSlot slot = Frames.GetOrAdd(parms.pawn, CreateFrameSlot);
            lock (slot)
            {
                slot.value = frame;
                slot.ready = true;
            }
        }

        internal static Scope Begin(Pawn pawn, Vector3 root, PawnRenderFlags flags, RimKataReactiveVisual? visual)
        {
            bool active = pawn != null && visual.HasValue
                && (flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache | PawnRenderFlags.Invisible)) == 0;
            if (!active && current.pawn == null) return default;
            (parents ??= new Stack<RenderContext>(2)).Push(current);
            try
            {
                current = default;
                if (active)
                {
                    current.pawn = pawn;
                    current.root = root;
                    current.flags = flags;
                    current.visual = visual.Value;
                    if (visual.Value.Falling)
                        current.frameActive = TryReadFrame(pawn, out current.frame);
                }
                return new Scope { pushed = true };
            }
            catch
            {
                current = parents.Pop();
                throw;
            }
        }

        internal static void End(Scope scope)
        {
            if (scope.pushed) current = parents.Pop();
        }

        private static bool TryReadFrame(Pawn pawn, out Frame frame)
        {
            frame = default;
            if (!Frames.TryGetValue(pawn, out FrameSlot slot)) return false;
            lock (slot)
            {
                if (!slot.ready) return false;
                frame = slot.value;
                return true;
            }
        }
        internal static bool SuppressNative(Thing weapon)
            => Active && !RimKataWeaponRenderProbe.Probing
                && (weapon == current.visual.primary || weapon == current.visual.secondary);

        // Weapon aim stays in world space while placement follows the rising body.
        internal static Matrix4x4 TransformEquipment(Matrix4x4 matrix)
        {
            if (!current.frameActive) return matrix;
            Vector3 position = PlaceWeapon(new Vector3(matrix.m03, matrix.m13, matrix.m23), in current.frame);
            matrix.m03 = position.x; matrix.m13 = position.y; matrix.m23 = position.z;
            return matrix;
        }

        internal static Matrix4x4 TransformEquipment(Pawn pawn, Matrix4x4 matrix)
        {
            if (pawn == current.pawn) return TransformEquipment(matrix);
            if (RimKataWorldRenderContext.BodyFor(pawn)?.reactive?.Falling != true
                || !TryReadFrame(pawn, out Frame frame)) return matrix;
            Vector3 position = PlaceWeapon(new Vector3(matrix.m03, matrix.m13, matrix.m23), in frame);
            matrix.m03 = position.x; matrix.m13 = position.y; matrix.m23 = position.z;
            return matrix;
        }

        internal static void TransformEquipment(ref Vector3 position)
        {
            if (current.frameActive) position = PlaceWeapon(position, in current.frame);
        }

        internal static bool WeaponsAboveBody(Pawn pawn = null)
        {
            if (pawn == null || pawn == current.pawn) return current.frameActive && current.frame.layer > 0f;
            return RimKataWorldRenderContext.BodyFor(pawn)?.reactive?.Falling == true
                && TryReadFrame(pawn, out Frame frame) && frame.layer > 0f;
        }

        private static Vector3 PlaceWeapon(Vector3 position, in Frame frame)
        {
            position = frame.transform.MultiplyPoint3x4(position);
            position.y += frame.layer;
            position.z += frame.northLift;
            return position;
        }

        internal static void PlaceShadow(Pawn pawn, ref Vector3 drawLoc)
        {
            if (RimKataWorldRenderContext.BodyFor(pawn)?.reactive?.Falling != true
                || !TryReadFrame(pawn, out Frame frame)) return;
            drawLoc.x = frame.shadow.x;
            drawLoc.z = frame.shadow.z;
        }

        internal static void Draw()
        {
            if (!Active || current.drawn) return;
            current.drawn = true;
            DrawWeapon(current.visual.primary, current.visual.primaryAngle, false);
            DrawWeapon(current.visual.secondary, current.visual.secondaryAngle, true);
            if (current.visual.secondary != null
                && RimKataWeaponRenderProbe.HasSpecialRenderer(current.visual.secondary.def))
                RimKataWeaponRenderProbe.DrawSecondaryExtras(current.pawn, current.visual.primary,
                    current.visual.secondary, current.root, current.visual.facing, current.flags);
        }

        private static void DrawWeapon(ThingWithComps weapon, float aim, bool secondary)
        {
            if (weapon == null || weapon.Destroyed) return;
            float factor = current.pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f;
            Vector3 pivot = current.frameActive ? PlaceWeapon(current.root, in current.frame) : current.root;
            Vector3? aimPoint = secondary ? current.visual.secondaryAimPoint : current.visual.primaryAimPoint;
            if (aimPoint.HasValue) aim = (aimPoint.Value - pivot).AngleFlat();
            Vector3 origin = pivot;
            if (current.visual.kind != RimKataReactiveKind.ShakeOff)
                origin += new Vector3((secondary ? -0.1f : 0.1f) * factor, 0f, 0f).RotatedBy(aim);
            if (aimPoint.HasValue) aim = (aimPoint.Value - origin).AngleFlat();
            Vector3 position = origin + new Vector3(0f, 0f,
                (0.4f + weapon.def.equippedDistanceOffset) * factor).RotatedBy(aim);
            if (secondary) position.y -= 0.001f;
            float normalized = Mathf.Repeat(aim, 360f);
            bool flipped = normalized > 200f && normalized < 340f;
            float rotation = aim - 90f + (flipped ? 180f - weapon.def.equippedAngleOffset : weapon.def.equippedAngleOffset);
            if (RimKataWeaponRenderProbe.DrawSpecialHeldPrimary(current.pawn, weapon, current.root,
                current.visual.facing, current.flags, position, rotation)) return;
            Graphic graphic = weapon.Graphic;
            Material material = graphic is Graphic_StackCount stack
                ? stack.SubGraphicForStackCount(1, weapon.def).MatSingleFor(weapon) : graphic.MatSingleFor(weapon);
            Graphics.DrawMesh(flipped ? MeshPool.plane10Flip : MeshPool.plane10,
                Matrix4x4.TRS(position, Quaternion.AngleAxis(rotation, Vector3.up),
                    new Vector3(graphic.drawSize.x, 0f, graphic.drawSize.y)), material, 0);
        }
    }
}
