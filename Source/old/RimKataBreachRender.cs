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
            internal Matrix4x4 transform, equipmentTransform;
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
            internal RimKataFlyingKickRender.EquipmentScope flyingKick;
            internal RimKataKickRender.EquipmentScope kick;
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
            internal RimKataFlyingKickVisual? flyingKick;
            internal RimKataKickVisual? kick;
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
            Matrix4x4 transform = Matrix4x4.Translate(new Vector3(0f, 0f, visual.height))
                * Matrix4x4.Translate(foot)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(visual.angle, Vector3.up))
                * Matrix4x4.Translate(-foot);
            float weaponRotation = Mathf.Clamp01(visual.weaponRotation);
            Matrix4x4 equipmentTransform = weaponRotation >= 1f ? transform
                : Matrix4x4.Translate(transform.MultiplyPoint3x4(anchor))
                    * Matrix4x4.Rotate(Quaternion.AngleAxis(visual.angle * weaponRotation, Vector3.up))
                    * Matrix4x4.Translate(-anchor);
            var frame = new Frame { anchor = anchor, foot = foot, transform = transform,
                equipmentTransform = equipmentTransform,
                shadow = transform.MultiplyPoint3x4(anchor),
                // A northward fall has no additional tilt in the sprite plane.
                northLift = Mathf.Max(0f, anchor.z - foot.z) * 0.5f
                    * Mathf.Max(0f, Mathf.Cos((visual.facing.AsAngle + 180f) * Mathf.Deg2Rad))
                    * Mathf.Clamp01(visual.progress) * weaponRotation };
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
                    * (top - anchor.y + PawnRenderUtility.AltitudeForLayer(100f)) * weaponRotation;
            }
            frame.shadow.z -= visual.height * 1.5f;
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
            matrix = frame.equipmentTransform * matrix;
            matrix.m13 += frame.layer;
            matrix.m23 += frame.northLift;
            return matrix;
        }

        internal static void TransformEquipment(ref Vector3 position, ref Quaternion rotation)
        {
            if (!TryFrame(equipment.pawn, out Frame frame)) return;
            position = frame.equipmentTransform.MultiplyPoint3x4(position);
            position.y += frame.layer;
            position.z += frame.northLift;
            rotation = frame.equipmentTransform.rotation * rotation;
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
            {
                Vector3 foot = TryReadFrame(parms.pawn, out Frame frame) ? frame.foot
                    : parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
                IntVec3 direction = visual.facing.FacingCell;
                Vector3 offset = foot - visual.doorOrigin;
                // Cell entry precedes the tweened foot reaching the door.
                if (offset.x * direction.x + offset.z * direction.z >= 0f)
                    location = foot;
            }
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
            __state.kick = body?.kick.HasValue == true ? RimKataWorldRenderContext.KickFor(parms.pawn) : null;
            if (__state.kick.HasValue)
            {
                parms.facing = RimKataKickRender.RenderFacing(__state.kick.Value);
                return;
            }
            __state.flyingKick = body?.flyingKick.HasValue == true
                ? RimKataWorldRenderContext.FlyingKickFor(parms.pawn) : null;
            if (__state.flyingKick.HasValue)
            {
                parms.facing = RimKataFlyingKickRender.RenderFacing(__state.flyingKick.Value);
                return;
            }
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
            if (__state.active) parms.facing = __state.visual.bodyFacing.IsValid
                ? __state.visual.bodyFacing : __state.visual.facing;
        }

        [HarmonyPriority(Priority.Last)]
        internal static void Postfix(PawnDrawParms parms, List<PawnGraphicDrawRequest> ___drawRequests,
            RimKataBreachRender.BodyScope __state)
        {
            if (__state.kick.HasValue)
                RimKataKickRender.Prepare(parms, ___drawRequests, __state.kick.Value);
            else if (__state.flyingKick.HasValue)
                RimKataFlyingKickRender.Prepare(parms, ___drawRequests, __state.flyingKick.Value);
            else if (__state.subdue.HasValue)
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
            var kick = body?.kick.HasValue == true ? RimKataWorldRenderContext.KickFor(___pawn) : null;
            var flyingKick = !kick.HasValue && body?.flyingKick.HasValue == true
                ? RimKataWorldRenderContext.FlyingKickFor(___pawn) : null;
            if (kick.HasValue) rotOverride = RimKataKickRender.RenderFacing(kick.Value);
            else if (flyingKick.HasValue) rotOverride = RimKataFlyingKickRender.RenderFacing(flyingKick.Value);
            else if (body?.subdue.HasValue == true) rotOverride = body.subdue.Value.BodyFacing;
            else if (body?.reactive.HasValue == true)
            {
                rotOverride = body.reactive.Value.facing;
                RimKataReactiveRender.PlaceBody(body.reactive.Value, ref drawLoc);
            }
            else if (body?.breach.HasValue == true
                && (body.breach.Value.poseActive || body.breach.Value.protectedPose))
                rotOverride = body.breach.Value.bodyFacing.IsValid
                    ? body.breach.Value.bodyFacing : body.breach.Value.facing;
        }
    }

    internal static class Patch_PawnRenderer_RimKataBreachCache
    {
        internal static void Prefix(Pawn ___pawn, ref bool disableCache)
        {
            var body = RimKataWorldRenderContext.BodyFor(___pawn);
            if (body?.subdue.HasValue == true || body?.breach?.poseActive == true
                || body?.reactive.HasValue == true || body?.flyingKick.HasValue == true
                || body?.kick.HasValue == true) disableCache = true;
        }
    }

    internal static class RimKataSpecialEquipmentRender
    {
        internal static void Begin(Pawn pawn, Vector3 drawPos, ref Rot4 facing, PawnRenderFlags flags,
            out RimKataBreachRender.EquipmentScope __state)
        {
            __state = default;
            __state.context = RimKataWorldRenderContext.Begin(pawn, (flags & PawnRenderFlags.Portrait) != 0);
            var body = RimKataWorldRenderContext.BodyFor(pawn);
            var kick = body?.kick.HasValue == true ? RimKataWorldRenderContext.KickFor(pawn) : null;
            var flyingKick = !kick.HasValue && body?.flyingKick.HasValue == true
                ? RimKataWorldRenderContext.FlyingKickFor(pawn) : null;
            bool kicking = kick.HasValue;
            bool flying = flyingKick.HasValue;
            bool isolated = kicking || flying;
            __state.token = RimKataBreachRender.PushEquipment(isolated ? null : pawn, flags).token;
            __state.kick = RimKataKickRender.BeginEquipment(pawn, flags, kick);
            __state.flyingKick = RimKataFlyingKickRender.BeginEquipment(pawn, flags, flyingKick);
            RimKataSubdueVisual? subdue = isolated ? null : body?.subdue;
            RimKataReactiveVisual? reactive = isolated || subdue.HasValue ? null : body?.reactive;
            __state.reactive = RimKataReactiveRender.Begin(pawn, drawPos, flags, reactive);
            __state.subdueWeapons = RimKataSubdueWeaponRender.Begin(pawn, drawPos, flags, subdue);
            bool participant = RimKataBreachRender.TryGetEquipmentVisual(pawn, out var visual);
            __state.weapons = RimKataBreachWeaponRender.Begin(pawn, drawPos, flags,
                participant && !subdue.HasValue && !reactive.HasValue, visual);
            if ((flags & PawnRenderFlags.Portrait) == 0 && kicking)
                facing = kick.Value.facing;
            else if ((flags & PawnRenderFlags.Portrait) == 0 && flying)
                facing = flyingKick.Value.facing == Rot4.South ? Rot4.South
                    : RimKataFlyingKickRender.RenderFacing(flyingKick.Value);
            else if ((flags & PawnRenderFlags.Portrait) == 0
                && subdue.HasValue) facing = subdue.Value.facing;
            else if ((flags & PawnRenderFlags.Portrait) == 0 && reactive.HasValue) facing = reactive.Value.facing;
            else if ((flags & PawnRenderFlags.Portrait) == 0
                && participant && (visual.poseActive || visual.protectedPose)) facing = visual.facing;
        }

        internal static void End(RimKataBreachRender.EquipmentScope __state)
        {
            RimKataBreachWeaponRender.End(__state.weapons);
            RimKataSubdueWeaponRender.End(__state.subdueWeapons);
            RimKataReactiveRender.End(__state.reactive);
            RimKataFlyingKickRender.EndEquipment(__state.flyingKick);
            RimKataKickRender.EndEquipment(__state.kick);
            RimKataBreachRender.PopEquipment(__state);
            RimKataWorldRenderContext.End(__state.context);
        }
    }
}
