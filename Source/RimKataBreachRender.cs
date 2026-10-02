using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataBreachRender
    {
        internal struct Frame
        {
            internal Vector3 anchor, foot, shadow;
            internal Matrix4x4 transform;
            internal float layer, northLift;
        }

        private sealed class FrameSlot
        {
            internal Frame value;
            internal bool ready;
        }

        internal struct EquipmentScope
        {
            internal int token;
            internal RimKataWorldRenderContext.Scope context;
            internal RimKataBreachWeaponRender.Scope weapons;
            internal RimKataSubdueWeaponRender.Scope subdueWeapons;
            internal RimKataReactiveRender.Scope reactive;
        }

        private struct EquipmentContext
        {
            internal Pawn pawn;
            internal RimKataBreachVisual visual;
            internal Frame frame;
            internal bool frameActive;
        }

        internal struct BodyScope
        {
            internal bool active;
            internal RimKataBreachVisual visual;
            internal RimKataSubdueVisual? subdue;
            internal RimKataReactiveVisual? reactive;
        }

        private static readonly ConcurrentDictionary<Pawn, FrameSlot> Frames = new ConcurrentDictionary<Pawn, FrameSlot>();
        private static readonly Func<Pawn, FrameSlot> CreateFrameSlot = _ => new FrameSlot();
        [ThreadStatic] private static EquipmentContext equipment;
        [ThreadStatic] private static int equipmentDepth;
        [ThreadStatic] private static EquipmentContext[] nestedEquipment;

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
                // A northward fall has no additional tilt in the sprite plane.
                northLift = Mathf.Max(0f, anchor.z - foot.z) * 0.5f
                    * Mathf.Max(0f, Mathf.Cos((visual.facing.AsAngle + 180f) * Mathf.Deg2Rad))
                    * Mathf.Clamp01(visual.progress) };
            float top = anchor.y;
            if (visual.poseActive)
            {
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
            FrameSlot slot = Frames.GetOrAdd(parms.pawn, CreateFrameSlot);
            lock (slot)
            {
                slot.value = frame;
                slot.ready = true;
            }
        }

        private static bool TryReadFrame(Pawn pawn, out Frame frame)
        {
            frame = default;
            if (pawn == null || !Frames.TryGetValue(pawn, out FrameSlot slot)) return false;
            lock (slot)
            {
                if (!slot.ready) return false;
                frame = slot.value;
            }
            return true;
        }

        internal static EquipmentScope PushEquipment(Pawn pawn, PawnRenderFlags flags)
        {
            RimKataBreachVisual visual = default;
            Pawn next = (flags & PawnRenderFlags.Portrait) == 0
                && pawn != null && RimKataBreachUtility.TryGetVisual(pawn, out visual) ? pawn : null;
            if (next == null && equipment.pawn == null) return default;
            if (equipmentDepth > 0)
            {
                if (nestedEquipment == null || nestedEquipment.Length < equipmentDepth)
                    Array.Resize(ref nestedEquipment, Math.Max(4, equipmentDepth * 2));
                nestedEquipment[equipmentDepth - 1] = equipment;
            }
            var scope = new EquipmentScope { token = ++equipmentDepth };
            equipment = default;
            try
            {
                equipment.pawn = next;
                equipment.visual = visual;
                equipment.frameActive = next != null && visual.poseActive
                    && TryReadFrame(next, out equipment.frame);
                return scope;
            }
            catch
            {
                PopEquipment(scope);
                throw;
            }
        }

        internal static void PopEquipment(EquipmentScope scope)
        {
            if (scope.token == 0) return;
            if (scope.token != equipmentDepth)
            {
                equipment = default;
                equipmentDepth = 0;
                if (nestedEquipment != null) Array.Clear(nestedEquipment, 0, nestedEquipment.Length);
                return;
            }
            if (--equipmentDepth > 0)
            {
                equipment = nestedEquipment[equipmentDepth - 1];
                nestedEquipment[equipmentDepth - 1] = default;
            }
            else equipment = default;
        }

        internal static bool TryGetEquipmentVisual(Pawn pawn, out RimKataBreachVisual visual)
        {
            visual = equipment.visual;
            return pawn != null && pawn == equipment.pawn;
        }

        private static bool TryFrame(Pawn pawn, out Frame frame)
        {
            frame = default;
            if (pawn != null && pawn == equipment.pawn)
            {
                frame = equipment.frame;
                return equipment.frameActive;
            }
            return pawn != null && RimKataBreachUtility.TryGetVisual(pawn, out RimKataBreachVisual visual)
                && visual.poseActive && TryReadFrame(pawn, out frame);
        }

        internal static Matrix4x4 TransformEquipment(Matrix4x4 matrix)
            => TransformEquipment(equipment.pawn, matrix);

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
            if (!TryFrame(equipment.pawn, out Frame frame)) return;
            position = frame.transform.MultiplyPoint3x4(position);
            position.y += frame.layer;
            position.z += frame.northLift;
            rotation = frame.transform.rotation * rotation;
        }

        internal static bool WeaponsAboveBody(Pawn pawn = null)
            => TryFrame(pawn ?? equipment.pawn, out Frame frame) && frame.layer > 0f;

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
                // Carried.PostDraw skips equipment extras when drawing a pawn.
                RimKataSubdueWeaponRender.DrawParticipant(parms, body.subdue.Value);
                return;
            }
            if (body?.breach.HasValue != true) return;
            RimKataBreachVisual visual = body.breach.Value;
            if (visual.door == null) return;
            Vector3 location = visual.doorOrigin;
            if (visual.carryDoor)
                location = TryReadFrame(parms.pawn, out Frame frame) ? frame.foot
                    : parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
            visual.door.Draw(location);
        }
    }

    internal static class Patch_PawnRenderTree_RimKataBreach
    {
        [HarmonyPriority(Priority.Last)]
        internal static void Prefix(ref PawnDrawParms parms, out RimKataBreachRender.BodyScope __state)
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
            __state.reactive = body?.reactive;
            if (__state.reactive.HasValue)
            {
                parms.facing = __state.reactive.Value.facing;
                return;
            }
            __state.visual = body?.breach ?? default;
            __state.active = body?.breach.HasValue == true
                && (__state.visual.poseActive || __state.visual.protectedPose);
            if (__state.active) parms.facing = __state.visual.facing;
        }

        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(PawnDrawParms parms, List<PawnGraphicDrawRequest> ___drawRequests,
            RimKataBreachRender.BodyScope __state)
        {
            if (__state.subdue.HasValue)
                RimKataSubdueRender.Prepare(parms, ___drawRequests, __state.subdue.Value);
            else if (__state.reactive.HasValue)
                RimKataReactiveRender.Prepare(parms, ___drawRequests, __state.reactive.Value);
            else if (__state.active && __state.visual.poseActive)
                RimKataBreachRender.Prepare(parms, ___drawRequests, __state.visual);
        }
    }

    internal static class Patch_PawnRenderer_RimKataBreachFacing
    {
        [HarmonyPriority(Priority.Last)]
        internal static void Prefix(Pawn ___pawn, DrawPhase phase, ref Vector3 drawLoc, ref Rot4? rotOverride)
        {
            if (phase == DrawPhase.EnsureInitialized) return;
            var body = RimKataWorldRenderContext.BodyFor(___pawn);
            if (body?.subdue.HasValue == true) rotOverride = body.subdue.Value.BodyFacing;
            else if (body?.reactive.HasValue == true)
            {
                rotOverride = body.reactive.Value.facing;
                RimKataReactiveRender.PlaceBody(body.reactive.Value, ref drawLoc);
            }
            else if (body?.breach.HasValue == true
                && (body.breach.Value.poseActive || body.breach.Value.protectedPose))
                rotOverride = body.breach.Value.facing;
        }
    }

    internal static class Patch_PawnRenderer_RimKataBreachCache
    {
        internal static void Prefix(Pawn ___pawn, ref bool disableCache)
        {
            var body = RimKataWorldRenderContext.BodyFor(___pawn);
            if (body?.subdue.HasValue == true || body?.breach?.poseActive == true
                || body?.reactive.HasValue == true) disableCache = true;
        }
    }

    [HarmonyPatch(typeof(RimKataEquipmentRenderHooks), nameof(RimKataEquipmentRenderHooks.DrawSpecialEquipmentAndApparelExtras))]
    internal static class Patch_PawnRenderUtility_RimKataBreachEquipment
    {
        [HarmonyPriority(Priority.First + 200)]
        private static void Prefix(Pawn pawn, Vector3 drawPos, ref Rot4 facing, PawnRenderFlags flags,
            out RimKataBreachRender.EquipmentScope __state)
        {
            __state = default;
            __state.context = RimKataWorldRenderContext.Begin(pawn, (flags & PawnRenderFlags.Portrait) != 0);
            __state.token = RimKataBreachRender.PushEquipment(pawn, flags).token;
            var body = RimKataWorldRenderContext.BodyFor(pawn);
            RimKataSubdueVisual? subdue = body?.subdue;
            RimKataReactiveVisual? reactive = subdue.HasValue ? null : body?.reactive;
            __state.reactive = RimKataReactiveRender.Begin(pawn, drawPos, flags, reactive);
            __state.subdueWeapons = RimKataSubdueWeaponRender.Begin(pawn, drawPos, flags, subdue);
            bool participant = RimKataBreachRender.TryGetEquipmentVisual(pawn, out var visual);
            __state.weapons = RimKataBreachWeaponRender.Begin(pawn, drawPos, flags,
                participant && !subdue.HasValue && !reactive.HasValue, visual);
            if ((flags & PawnRenderFlags.Portrait) == 0
                && subdue.HasValue) facing = subdue.Value.facing;
            else if ((flags & PawnRenderFlags.Portrait) == 0 && reactive.HasValue) facing = reactive.Value.facing;
            else if ((flags & PawnRenderFlags.Portrait) == 0
                && participant && (visual.poseActive || visual.protectedPose)) facing = visual.facing;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            RimKataBreachWeaponRender.Draw();
            RimKataReactiveRender.Draw();
        }

        [HarmonyPriority(Priority.Last)]
        private static void Finalizer(RimKataBreachRender.EquipmentScope __state)
        {
            RimKataBreachWeaponRender.End(__state.weapons);
            RimKataSubdueWeaponRender.End(__state.subdueWeapons);
            RimKataReactiveRender.End(__state.reactive);
            RimKataBreachRender.PopEquipment(__state);
            RimKataWorldRenderContext.End(__state.context);
        }
    }
}
