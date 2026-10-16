using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal struct RimKataKickVisual
    {
        internal Rot4 facing, strikeFacing;
        internal float angle;
        internal RimKataKickRender.FrameSlot frame;
    }

    internal static class RimKataKickRender
    {
        internal sealed class FrameSlot
        {
            internal Geometry geometry;
            internal bool ready;
            internal bool registered;
            internal RimKataKickVisual visual;
            internal RimKataGroundPoseGeometry.BodySample bodySample;
        }

        internal struct Geometry
        {
            internal Vector3 anchorOffset, footOffset, neckOffset, bodyOffset;
        }

        internal struct EquipmentScope
        {
            internal Pawn pawn;
            internal Vector3 displacement, anchorOffset;
            internal bool active;
        }

        internal struct ShotScope
        {
            internal Verb verb;
            internal Vector3 center;
        }

        [ThreadStatic] private static EquipmentScope equipment;
        [ThreadStatic] private static ShotScope shot;

        internal static bool EquipmentActive => equipment.active;

        internal static Rot4 RenderFacing(RimKataKickVisual visual)
            => visual.facing == Rot4.South ? visual.strikeFacing : visual.facing;

        internal static bool TryInitialize(Pawn pawn, Thing target, Rot4 facing, Rot4 renderFacing, FrameSlot frame,
            out float targetAngle)
        {
            targetAngle = 0f;
            if (frame == null || !RimKataGroundPoseHead.Supports(pawn)) return false;
            Vector3 root = pawn.DrawPos;
            PawnDrawParms parms = PawnDrawParms.DefaultFor(pawn);
            parms.facing = facing;
            parms.matrix = Matrix4x4.Translate(root + pawn.ageTracker.CurLifeStage.bodyDrawOffset);
            if (!RimKataGroundPoseHead.TryGetHeadMatrix(parms, out Matrix4x4 head)) return false;
            Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 foot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            Vector3 neck = head.MultiplyPoint3x4(Vector3.zero);
            if (target != null)
            {
                Vector3 from = foot - neck;
                Vector3 toward = target.Position.ToVector3Shifted() - neck;
                if (from.x * from.x + from.z * from.z < 0.0001f
                    || toward.x * toward.x + toward.z * toward.z < 0.0001f) return false;
                targetAngle = Angle(from, toward);
            }
            if (renderFacing != facing)
            {
                parms.facing = renderFacing;
                if (!RimKataGroundPoseHead.TryGetHeadMatrix(parms, out head)) return false;
                neck = head.MultiplyPoint3x4(Vector3.zero);
            }
            Vector3 body = RimKataGroundPoseHead.TryGetBodyMatrix(parms, out Matrix4x4 bodyMatrix)
                ? bodyMatrix.MultiplyPoint3x4(Vector3.zero) : anchor;
            Store(frame, parms, root, anchor, foot, neck, body);
            return true;
        }

        private static float Angle(Vector3 from, Vector3 toward)
            => Mathf.DeltaAngle(Mathf.Atan2(from.x, from.z) * Mathf.Rad2Deg,
                Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg);

        internal static void Publish(Pawn pawn, RimKataKickVisual visual)
        {
            FrameSlot frame = visual.frame;
            frame.bodySample ??= RimKataGroundPoseGeometry.GetBodySample(pawn);
            lock (frame.bodySample) frame.visual = visual;
            if (frame.registered) return;
            RimKataResponseVisualParticipantCache.PublishKick(pawn, visual);
            frame.registered = true;
        }

        internal static void Remove(Pawn pawn, FrameSlot frame)
        {
            RimKataResponseVisualParticipantCache.PublishKick(pawn, null);
            frame.registered = false;
        }

        internal static RimKataKickVisual ReadPublished(RimKataKickVisual seed, out Geometry geometry, out bool ready)
        {
            geometry = default;
            ready = false;
            FrameSlot frame = seed.frame;
            if (frame?.bodySample == null) return seed;
            lock (frame.bodySample)
            {
                geometry = frame.geometry;
                ready = frame.ready;
                return frame.visual;
            }
        }

        internal static void Prepare(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests,
            RimKataKickVisual visual)
        {
            if (visual.frame == null || parms.Portrait || parms.dead) return;
            Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 foot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            Vector3 body = anchor, neck = default;
            bool hasNeck = false;
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                if (request.node.Props.tagDef == PawnRenderNodeTagDefOf.Body)
                    body = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                else if (request.node.Props.tagDef == PawnRenderNodeTagDefOf.Head)
                {
                    neck = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                    hasNeck = true;
                }
            }
            Vector3 root = parms.pawn.DrawPos;
            if (!hasNeck) neck = root + ReadGeometry(visual).neckOffset;
            Geometry geometry = Store(visual.frame, parms, root, anchor, foot, neck, body);
            RimKataWorldRenderContext.UpdateKickGeometry(visual.frame, geometry);
            Matrix4x4 transform = Transform(neck, visual.angle);
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                request.preDrawnComputedMatrix = transform * request.preDrawnComputedMatrix;
                requests[i] = request;
            }
        }

        private static Geometry Store(FrameSlot frame, PawnDrawParms parms, Vector3 root, Vector3 anchor,
            Vector3 foot, Vector3 neck, Vector3 body)
        {
            frame.bodySample ??= RimKataGroundPoseGeometry.GetBodySample(parms.pawn);
            Geometry geometry = new Geometry { anchorOffset = anchor - root, footOffset = foot - root,
                neckOffset = neck - root, bodyOffset = body - root };
            lock (frame.bodySample)
            {
                frame.geometry = geometry;
                frame.ready = true;
                RimKataGroundPoseGeometry.StoreBodyUnderLock(frame.bodySample, parms, foot, anchor, body,
                    Mathf.Max(0f, neck.z - foot.z) * 0.5f, neck, root);
            }
            return geometry;
        }

        private static Geometry ReadGeometry(RimKataKickVisual visual)
            => ReadGeometry(visual, out _);

        private static Geometry ReadGeometry(RimKataKickVisual visual, out bool ready)
        {
            if (RimKataWorldRenderContext.TryKickGeometry(visual.frame, out var geometry, out ready))
                return ready ? geometry : default;
            ready = true;
            if (visual.frame?.bodySample != null)
                lock (visual.frame.bodySample)
                    if (visual.frame.ready) return visual.frame.geometry;
            ready = false;
            return default;
        }

        private static Matrix4x4 Transform(Vector3 neck, float angle)
            => Matrix4x4.Translate(neck)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(angle, Vector3.up))
                * Matrix4x4.Translate(-neck);

        private static Vector3 Displacement(RimKataKickVisual visual, Geometry geometry)
            => geometry.neckOffset + (geometry.anchorOffset - geometry.neckOffset).RotatedBy(visual.angle)
                - geometry.anchorOffset;

        internal static EquipmentScope BeginEquipment(Pawn pawn, PawnRenderFlags flags,
            RimKataKickVisual? visual)
        {
            EquipmentScope previous = equipment;
            equipment = default;
            if (visual.HasValue
                && (flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache | PawnRenderFlags.Invisible)) == 0)
            {
                Geometry geometry = ReadGeometry(visual.Value);
                equipment = new EquipmentScope { pawn = pawn, active = true,
                    anchorOffset = geometry.anchorOffset, displacement = Displacement(visual.Value, geometry) };
            }
            return previous;
        }

        internal static void EndEquipment(EquipmentScope previous) => equipment = previous;

        internal static Matrix4x4 TransformEquipment(Matrix4x4 matrix)
        {
            if (!equipment.active) return matrix;
            matrix.m03 += equipment.displacement.x;
            matrix.m23 += equipment.displacement.z;
            return matrix;
        }

        internal static Matrix4x4 TransformEquipment(Pawn pawn, Matrix4x4 matrix)
        {
            if (equipment.active && equipment.pawn == pawn) return TransformEquipment(matrix);
            if (!RimKataWorldRenderContext.TryKick(pawn, out var visual)) return matrix;
            Vector3 delta = Displacement(visual, ReadGeometry(visual));
            matrix.m03 += delta.x;
            matrix.m23 += delta.z;
            return matrix;
        }

        internal static void TransformEquipment(ref Vector3 position)
        {
            if (!equipment.active) return;
            position.x += equipment.displacement.x;
            position.z += equipment.displacement.z;
        }

        internal static bool TryGetAimOrigin(Pawn pawn, out Vector3 origin)
        {
            origin = default;
            if (equipment.active && equipment.pawn == pawn)
            {
                origin = pawn.DrawPos + equipment.anchorOffset + equipment.displacement;
                return true;
            }
            if (!RimKataWorldRenderContext.TryKick(pawn, out var visual)) return false;
            Geometry geometry = ReadGeometry(visual);
            origin = pawn.DrawPos + geometry.anchorOffset + Displacement(visual, geometry);
            return true;
        }

        internal static void PlaceShadow(Pawn pawn, RimKataKickVisual visual, ref Vector3 drawLoc)
        {
            Geometry geometry = ReadGeometry(visual);
            Vector3 root = pawn.DrawPos;
            Vector3 center = Transform(root + geometry.neckOffset, visual.angle)
                .MultiplyPoint3x4(root + geometry.bodyOffset);
            drawLoc.x = center.x;
            drawLoc.z = center.z;
        }

        internal static ShotScope BeginShot(RimKataNativeAttack request)
        {
            ShotScope previous = shot;
            shot = default;
            RimKataKickState kick = request.state?.kick;
            if (request.verb?.IsMeleeAttack != false || kick?.motionActive != true) return previous;
            RimKataKickVisual visual = kick.visual;
            Geometry geometry = ReadGeometry(visual, out bool ready);
            Vector3 displacement = Displacement(visual, geometry);
            Vector3 root = request.pawn.DrawPos;
            Vector3 anchor = root + geometry.anchorOffset;
            Vector3 origin = anchor + displacement;
            shot = new ShotScope { verb = request.verb,
                center = (ready ? RimKataGroundPoseGeometry.StandingCenter(request.verb, origin,
                    root + geometry.anchorOffset.Yto0())
                    : RimKataGroundPoseGeometry.StandingCenter(request.verb, origin)) + displacement };
            return previous;
        }

        internal static void EndShot(ShotScope previous) => shot = previous;

        internal static bool TryGetShotCenter(Verb verb, out Vector3 center)
        {
            center = shot.center;
            return shot.verb != null && shot.verb == verb;
        }
    }
}
