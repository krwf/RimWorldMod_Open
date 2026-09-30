using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Breach poses deliberately have no dependency on the fall/prone lifecycle.
    // Only immutable tick snapshots are consumed by parallel pawn rendering.
    internal static class RimKataBreachRender
    {
        internal sealed class Frame
        {
            internal Vector3 anchor, foot, shadow;
            internal Matrix4x4 transform;
            internal float layer, northLift;
        }

        internal struct EquipmentScope
        {
            internal Pawn previous;
            internal RimKataBreachVisual previousVisual;
            internal Frame previousFrame;
            internal RimKataWorldRenderContext.Scope context;
            internal RimKataBreachWeaponRender.Scope weapons;
            internal RimKataSubdueWeaponRender.Scope subdueWeapons;
            internal bool pushed;
        }

        internal struct BodyScope
        {
            internal bool active;
            internal RimKataBreachVisual visual;
            internal RimKataSubdueVisual? subdue;
        }

        private static readonly ConcurrentDictionary<Pawn, Frame> Frames = new ConcurrentDictionary<Pawn, Frame>();
        [ThreadStatic] private static Pawn equipmentPawn;
        [ThreadStatic] private static RimKataBreachVisual equipmentVisual;
        [ThreadStatic] private static Frame equipmentFrame;

        internal static void Clear(Pawn pawn)
        {
            if (pawn != null) Frames.TryRemove(pawn, out _);
        }

        internal static bool TryPose(Pawn pawn, out RimKataBreachVisual visual)
            => RimKataBreachUtility.TryGetVisual(pawn, out visual)
                && (visual.poseActive || visual.protectedPose);

        internal static void Prepare(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests,
            RimKataBreachVisual visual)
        {
            Vector3 foot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            Matrix4x4 transform = Matrix4x4.Translate(foot)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(visual.angle, Vector3.up))
                * Matrix4x4.Translate(-foot);
            var frame = new Frame { anchor = anchor, foot = foot, transform = transform,
                shadow = transform.MultiplyPoint3x4(anchor),
                // A northward fall cannot tip further in the sprite plane.
                // As with the existing lying visual, its carried weapons advance
                // toward the head while keeping their own facing and depth.
                northLift = Mathf.Max(0f, anchor.z - foot.z) * 0.5f
                    * Mathf.Max(0f, Mathf.Cos((visual.facing.AsAngle + 180f) * Mathf.Deg2Rad))
                    * Mathf.Clamp01(visual.progress) };
            float top = anchor.y;
            if (visual.poseActive)
            {
                // The complete render tree is transformed, so animals do not need
                // separate head/body nodes to take part in a breach.
                for (int i = 0; i < requests.Count; i++)
                {
                    PawnGraphicDrawRequest request = requests[i];
                    request.preDrawnComputedMatrix = transform * request.preDrawnComputedMatrix;
                    top = Mathf.Max(top, request.preDrawnComputedMatrix.m13);
                    if (request.node.Props.tagDef == PawnRenderNodeTagDefOf.Body)
                        frame.shadow = request.preDrawnComputedMatrix.MultiplyPoint3x4(Vector3.zero);
                    requests[i] = request;
                }
                frame.layer = Mathf.Clamp01(visual.progress)
                    * (top - anchor.y + PawnRenderUtility.AltitudeForLayer(100f));
            }
            Frames[parms.pawn] = frame;
        }

        internal static EquipmentScope PushEquipment(Pawn pawn, PawnRenderFlags flags)
        {
            RimKataBreachVisual visual = default;
            Pawn next = (flags & PawnRenderFlags.Portrait) == 0
                && pawn != null && RimKataBreachUtility.TryGetVisual(pawn, out visual) ? pawn : null;
            if (next == null && equipmentPawn == null) return default;
            var scope = new EquipmentScope { previous = equipmentPawn,
                previousVisual = equipmentVisual, previousFrame = equipmentFrame, pushed = true };
            equipmentPawn = next;
            equipmentVisual = visual;
            equipmentFrame = next != null && visual.poseActive
                && Frames.TryGetValue(next, out Frame frame) ? frame : null;
            return scope;
        }

        internal static void PopEquipment(EquipmentScope scope)
        {
            if (!scope.pushed) return;
            equipmentPawn = scope.previous;
            equipmentVisual = scope.previousVisual;
            equipmentFrame = scope.previousFrame;
        }

        internal static bool TryGetEquipmentVisual(Pawn pawn, out RimKataBreachVisual visual)
        {
            visual = equipmentVisual;
            return pawn != null && pawn == equipmentPawn;
        }

        private static bool TryFrame(Pawn pawn, out Frame frame)
        {
            frame = null;
            if (pawn != null && pawn == equipmentPawn)
            {
                frame = equipmentFrame;
                return frame != null;
            }
            return pawn != null && RimKataBreachUtility.TryGetVisual(pawn, out RimKataBreachVisual visual)
                && visual.poseActive && Frames.TryGetValue(pawn, out frame);
        }

        internal static Matrix4x4 TransformEquipment(Matrix4x4 matrix)
            => TransformEquipment(equipmentPawn, matrix);

        internal static Matrix4x4 TransformEquipment(Pawn pawn, Matrix4x4 matrix)
        {
            if (!TryFrame(pawn, out Frame frame)) return matrix;
            matrix = frame.transform * matrix;
            matrix.m13 += frame.layer;
            matrix.m23 += frame.northLift;
            return matrix;
        }

        internal static void TransformEquipment(ref Vector3 position, ref Quaternion rotation)
        {
            if (!TryFrame(equipmentPawn, out Frame frame)) return;
            position = frame.transform.MultiplyPoint3x4(position);
            position.y += frame.layer;
            position.z += frame.northLift;
            rotation = frame.transform.rotation * rotation;
        }

        internal static bool WeaponsAboveBody(Pawn pawn = null)
            => TryFrame(pawn ?? equipmentPawn, out Frame frame) && frame.layer > 0f;

        internal static void PlaceShadow(Pawn pawn, ref Vector3 position)
        {
            if (!TryFrame(pawn, out Frame frame)) return;
            position.x = frame.shadow.x;
            position.z = frame.shadow.z;
        }

        internal static void DrawDoor(PawnDrawParms parms)
        {
            if (parms.Portrait) return;
            var body = RimKataWorldRenderContext.BodyFor(parms.pawn);
            if (body?.subdue.HasValue == true)
            {
                // Carried.PostDraw skips equipment extras when it draws a pawn.
                // The carrier's completed body draw owns its weapon instead.
                RimKataSubdueWeaponRender.DrawParticipant(parms, body.subdue.Value);
                return;
            }
            if (body?.breach.HasValue != true) return;
            RimKataBreachVisual visual = body.breach.Value;
            if (visual.door == null) return;
            Vector3 location = visual.doorOrigin;
            if (visual.carryDoor)
                location = Frames.TryGetValue(parms.pawn, out Frame frame) ? frame.foot
                    : parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            visual.door.Draw(location);
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.ParallelPreDraw))]
    internal static class Patch_PawnRenderTree_RimKataBreach
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(ref PawnDrawParms parms, out RimKataBreachRender.BodyScope __state)
        {
            __state = default;
            if (parms.Portrait) return;
            var body = RimKataWorldRenderContext.BodyFor(parms.pawn);
            __state.subdue = body?.subdue;
            if (__state.subdue.HasValue)
            {
                parms.facing = __state.subdue.Value.BodyFacing;
                return;
            }
            __state.visual = body?.breach ?? default;
            __state.active = body?.breach.HasValue == true
                && (__state.visual.poseActive || __state.visual.protectedPose);
            if (__state.active) parms.facing = __state.visual.facing;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(PawnDrawParms parms, List<PawnGraphicDrawRequest> ___drawRequests,
            RimKataBreachRender.BodyScope __state)
        {
            // Standing protection only fixes facing. Its equipment/shadow no
            // longer consumes a sliding frame, so do not rebuild one per draw.
            if (__state.subdue.HasValue)
                RimKataSubdueRender.Prepare(parms, ___drawRequests, __state.subdue.Value);
            else if (__state.active && __state.visual.poseActive)
                RimKataBreachRender.Prepare(parms, ___drawRequests, __state.visual);
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), nameof(PawnRenderTree.Draw))]
    internal static class Patch_PawnRenderTree_RimKataBreachDoor
    {
        private static void Postfix(PawnDrawParms parms) => RimKataBreachRender.DrawDoor(parms);
    }

    [HarmonyPatch(typeof(PawnRenderer), nameof(PawnRenderer.DynamicDrawPhaseAt))]
    internal static class Patch_PawnRenderer_RimKataBreachFacing
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Pawn ___pawn, DrawPhase phase, ref Rot4? rotOverride)
        {
            if (phase == DrawPhase.EnsureInitialized) return;
            var body = RimKataWorldRenderContext.BodyFor(___pawn);
            if (body?.subdue.HasValue == true) rotOverride = body.subdue.Value.BodyFacing;
            else if (body?.breach.HasValue == true
                && (body.breach.Value.poseActive || body.breach.Value.protectedPose))
                rotOverride = body.breach.Value.facing;
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    internal static class Patch_PawnRenderer_RimKataBreachCache
    {
        private static void Prefix(Pawn ___pawn, ref bool disableCache)
        {
            var body = RimKataWorldRenderContext.BodyFor(___pawn);
            if (body?.subdue.HasValue == true || body?.breach?.poseActive == true) disableCache = true;
        }
    }

    [HarmonyPatch(typeof(PawnRenderer), "DrawShadowInternal")]
    internal static class Patch_PawnRenderer_RimKataBreachShadow
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Pawn ___pawn, ref Vector3 drawLoc)
            => RimKataBreachRender.PlaceShadow(___pawn, ref drawLoc);
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
    internal static class Patch_PawnRenderUtility_RimKataBreachEquipment
    {
        [HarmonyPriority(Priority.First + 200)]
        private static void Prefix(Pawn pawn, Vector3 drawPos, ref Rot4 facing, PawnRenderFlags flags,
            out RimKataBreachRender.EquipmentScope __state)
        {
            var context = RimKataWorldRenderContext.Begin(pawn, (flags & PawnRenderFlags.Portrait) != 0);
            __state = RimKataBreachRender.PushEquipment(pawn, flags);
            __state.context = context;
            RimKataSubdueVisual? subdue = RimKataWorldRenderContext.BodyFor(pawn)?.subdue;
            __state.subdueWeapons = RimKataSubdueWeaponRender.Begin(pawn, drawPos, flags, subdue);
            bool participant = RimKataBreachRender.TryGetEquipmentVisual(pawn, out var visual);
            __state.weapons = RimKataBreachWeaponRender.Begin(pawn, drawPos, flags,
                participant && !subdue.HasValue, visual);
            if ((flags & PawnRenderFlags.Portrait) == 0
                && subdue.HasValue) facing = subdue.Value.facing;
            else if ((flags & PawnRenderFlags.Portrait) == 0
                && participant && (visual.poseActive || visual.protectedPose)) facing = visual.facing;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            RimKataBreachWeaponRender.Draw();
        }

        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(RimKataBreachRender.EquipmentScope __state)
        {
            RimKataBreachWeaponRender.End(__state.weapons);
            RimKataSubdueWeaponRender.End(__state.subdueWeapons);
            RimKataBreachRender.PopEquipment(__state);
            RimKataWorldRenderContext.End(__state.context);
        }
    }
}
