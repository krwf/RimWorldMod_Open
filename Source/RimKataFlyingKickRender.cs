using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal struct RimKataFlyingKickVisual
    {
        internal Rot4 facing, strikeFacing;
        internal float angle, height, lieAngle;
        internal Vector3 offset;
        internal bool footPinned, striking, falling;
        internal Vector3 footPosition;
        internal Thing aimTarget;
        internal RimKataFlyingKickRender.FrameSlot frame;
    }

    internal static class RimKataFlyingKickRender
    {
        internal sealed class FrameSlot
        {
            internal Geometry geometry;
            internal bool ready;
            internal bool registered;
            internal RimKataFlyingKickVisual visual;
            internal RimKataGroundPoseGeometry.BodySample bodySample;
        }

        internal struct Geometry
        {
            internal Vector3 anchorOffset, footOffset, bodyOffset;
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

        private static readonly AccessTools.FieldRef<PawnRenderTree, PawnDrawParms> LastDrawParms =
            AccessTools.FieldRefAccess<PawnRenderTree, PawnDrawParms>("oldParms");
        private static readonly AccessTools.FieldRef<PawnRenderTree, List<PawnGraphicDrawRequest>> DrawRequests =
            AccessTools.FieldRefAccess<PawnRenderTree, List<PawnGraphicDrawRequest>>("drawRequests");

        internal static bool EquipmentActive => equipment.active;

        internal static Rot4 RenderFacing(RimKataFlyingKickVisual visual)
            => visual.falling || visual.facing == Rot4.North
                ? RimKataGroundPoseState.FacingAt(visual.lieAngle + 180f, visual.angle)
                : visual.facing == Rot4.South && visual.striking ? visual.strikeFacing : visual.facing;

        internal static void Publish(Pawn pawn, RimKataFlyingKickVisual visual)
        {
            FrameSlot frame = visual.frame;
            frame.bodySample ??= RimKataGroundPoseGeometry.GetBodySample(pawn);
            lock (frame.bodySample) frame.visual = visual;
            if (frame.registered) return;
            RimKataResponseVisualParticipantCache.PublishFlyingKick(pawn, visual);
            frame.registered = true;
        }

        internal static void Remove(Pawn pawn, FrameSlot frame)
        {
            RimKataResponseVisualParticipantCache.PublishFlyingKick(pawn, null);
            frame.registered = false;
        }

        internal static RimKataFlyingKickVisual ReadPublished(RimKataFlyingKickVisual seed, out Geometry geometry, out bool ready)
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
            RimKataFlyingKickVisual visual)
        {
            if (visual.frame == null || parms.Portrait || parms.dead) return;
            Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 foot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            Vector3 body = anchor;
            Vector3 headPosition = default;
            bool hasBody = false, hasHead = false;
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                if (!hasBody && request.node.Props.tagDef == RimWorld.PawnRenderNodeTagDefOf.Body)
                {
                    body = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                    hasBody = true;
                }
                else if (!hasHead && request.node.Props.tagDef == RimWorld.PawnRenderNodeTagDefOf.Head)
                {
                    headPosition = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                    hasHead = true;
                }
                if (hasBody && hasHead) break;
            }
            Vector3 root = parms.pawn.DrawPos;
            Geometry geometry = new Geometry
            {
                anchorOffset = anchor - root, footOffset = foot - root, bodyOffset = body - root
            };
            if (!hasHead && RimKataGroundPoseHead.TryGetHeadMatrix(parms, out Matrix4x4 head))
            {
                headPosition = head.MultiplyPoint3x4(Vector3.zero);
                hasHead = true;
            }
            FrameSlot frame = visual.frame;
            frame.bodySample ??= RimKataGroundPoseGeometry.GetBodySample(parms.pawn);
            lock (frame.bodySample)
            {
                frame.geometry = geometry;
                frame.ready = true;
                RimKataGroundPoseGeometry.StoreBodyUnderLock(frame.bodySample, parms, foot, anchor, body,
                    hasHead ? Mathf.Max(0f, headPosition.z - foot.z) * 0.5f : 0f,
                    hasHead ? headPosition : (Vector3?)null, root);
            }
            RimKataWorldRenderContext.UpdateFlyingKickGeometry(frame, geometry);
            Matrix4x4 transform = Transform(foot, visual);
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                request.preDrawnComputedMatrix = transform * request.preDrawnComputedMatrix;
                requests[i] = request;
            }
        }

        private static Vector3 Translation(Vector3 foot, RimKataFlyingKickVisual visual)
        {
            if (!visual.footPinned) return visual.offset + new Vector3(0f, 0f, visual.height);
            return new Vector3(visual.footPosition.x - foot.x, 0f, visual.footPosition.z - foot.z);
        }

        private static Matrix4x4 Transform(Vector3 foot, RimKataFlyingKickVisual visual)
            => Matrix4x4.Translate(Translation(foot, visual))
                * Matrix4x4.Translate(foot)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(visual.angle, Vector3.up))
                * Matrix4x4.Translate(-foot);

        private static Geometry ReadGeometry(RimKataFlyingKickVisual visual)
            => ReadGeometry(visual, out _);

        private static Geometry ReadGeometry(RimKataFlyingKickVisual visual, out bool ready)
        {
            if (RimKataWorldRenderContext.TryFlyingKickGeometry(visual.frame, out var geometry, out ready))
                return ready ? geometry : new Geometry { footOffset = new Vector3(0f, 0f, -0.5f) };
            ready = true;
            if (visual.frame?.bodySample != null)
                lock (visual.frame.bodySample)
                    if (visual.frame.ready) return visual.frame.geometry;
            ready = false;
            return new Geometry { footOffset = new Vector3(0f, 0f, -0.5f) };
        }

        internal static Vector3 NaturalFoot(Pawn pawn, RimKataFlyingKickVisual visual)
            => pawn.DrawPos + ReadGeometry(visual).footOffset;

        internal static bool CaptureContact(Pawn pawn, Thing target, RimKataFlyingKickVisual visual,
            out Vector3 start, out Vector3 end)
        {
            Vector3 footOffset = ReadGeometry(visual, out bool ready).footOffset;
            if (!ready) footOffset += pawn.ageTracker?.CurLifeStage?.bodyDrawOffset ?? Vector3.zero;
            Vector3 foot = pawn.DrawPos + footOffset;
            start = foot + Translation(foot, visual);
            end = start;
            if (!(target is Pawn targetPawn) || !targetPawn.Spawned || targetPawn.Dead) return false;

            end = targetPawn.DrawPos;
            PawnRenderer renderer = targetPawn.Drawer?.renderer;
            PawnRenderTree tree = renderer?.renderTree;
            if (tree?.rootNode != null)
            {
                PawnDrawParms parms = LastDrawParms(tree);
                if (parms.pawn == targetPawn && !parms.Portrait && !parms.Cache && !parms.dead)
                {
                    List<PawnGraphicDrawRequest> requests = DrawRequests(tree);
                    for (int i = 0; i < requests.Count; i++)
                        if (requests[i].node.Props.tagDef == RimWorld.PawnRenderNodeTagDefOf.Head)
                        {
                            end = requests[i].preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                            return true;
                        }
                }
            }
            if (renderer != null && targetPawn.story?.bodyType != null && targetPawn.ageTracker != null)
                end += targetPawn.ageTracker.CurLifeStage.bodyDrawOffset
                    + renderer.BaseHeadOffsetAt(targetPawn.Rotation);
            return true;
        }

        private static Vector3 Displacement(Vector3 root, RimKataFlyingKickVisual visual, Geometry geometry)
        {
            return geometry.footOffset + (geometry.anchorOffset - geometry.footOffset).RotatedBy(visual.angle)
                - geometry.anchorOffset + Translation(root + geometry.footOffset, visual);
        }

        internal static EquipmentScope BeginEquipment(Pawn pawn, PawnRenderFlags flags,
            RimKataFlyingKickVisual? visual)
        {
            EquipmentScope previous = equipment;
            equipment = default;
            if (visual.HasValue
                && (flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache | PawnRenderFlags.Invisible)) == 0)
            {
                Geometry geometry = ReadGeometry(visual.Value);
                equipment = new EquipmentScope { pawn = pawn, active = true,
                    anchorOffset = geometry.anchorOffset,
                    displacement = Displacement(pawn.DrawPos, visual.Value, geometry) };
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
            if (!RimKataWorldRenderContext.TryFlyingKick(pawn, out var visual)) return matrix;
            Vector3 delta = Displacement(pawn.DrawPos, visual, ReadGeometry(visual));
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
            if (!RimKataWorldRenderContext.TryFlyingKick(pawn, out var visual)) return false;
            Geometry geometry = ReadGeometry(visual);
            Vector3 root = pawn.DrawPos;
            origin = root + geometry.anchorOffset + Displacement(root, visual, geometry);
            return true;
        }

        internal static void PlaceShadow(Pawn pawn, RimKataFlyingKickVisual visual, ref Vector3 drawLoc)
        {
            Geometry geometry = ReadGeometry(visual);
            Vector3 root = pawn.DrawPos;
            Vector3 center = Transform(root + geometry.footOffset, visual)
                .MultiplyPoint3x4(root + geometry.bodyOffset);
            drawLoc.x = center.x;
            drawLoc.z = center.z - visual.height * 1.5f;
        }

        internal static ShotScope BeginShot(RimKataNativeAttack request)
        {
            ShotScope previous = shot;
            shot = default;
            RimKataFlyingKickState flying = request.state?.flyingKick;
            if (request.verb?.IsMeleeAttack != false || flying == null) return previous;
            RimKataFlyingKickVisual visual = flying.visual;
            Geometry geometry = ReadGeometry(visual, out bool ready);
            Vector3 root = request.pawn.DrawPos;
            Vector3 displacement = Displacement(root, visual, geometry);
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
