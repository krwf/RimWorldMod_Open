using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed partial class RimKataSubdueState
    {
        // Transient weapon animation; combat timers remain in the combat owner.
        internal int visualAttackTick = -1, visualAttackDuration;
        internal bool visualAttackRanged;
        internal LocalTargetInfo visualAttackTarget = LocalTargetInfo.Invalid;
    }

    // Tick-published data only. Neither rendering nor ordinary pawn draws query
    // the hold registry, qualification, loadout, or attack controller.
    internal struct RimKataSubdueVisual
    {
        internal Pawn partner;
        internal Rot4 facing;
        internal bool held, attacking, ranged;
        internal float struggleAngle, weaponThrust, recoilAngle;
        internal Vector3 recoilOffset;
        internal LocalTargetInfo aimTarget;
        internal ThingWithComps weapon;

        // Keep the carrier's facing for placement and layer order; only the
        // held pawn's body turns back toward the carrier.
        internal Rot4 BodyFacing => held ? facing.Opposite : facing;
    }

    internal static class RimKataSubdueRender
    {
        private struct BodyFrame
        {
            internal Vector3 root, heldCenterOffset;
            internal float bottom, top;
        }

        private static readonly ConcurrentDictionary<Pawn, BodyFrame> Frames =
            new ConcurrentDictionary<Pawn, BodyFrame>();

        internal static void NotifyAttack(RimKataSubdueState state, bool ranged, LocalTargetInfo target)
        {
            state.visualAttackTick = Find.TickManager.TicksGame;
            state.visualAttackDuration = Math.Min(12, Math.Max(2, state.attackTicks));
            state.visualAttackRanged = ranged;
            state.visualAttackTarget = target;
        }

        internal static void Publish(RimKataSubdueState state)
        {
            if (state?.pawn == null || state.target == null) return;
            Map map = state.pawn.Map;
            var carrier = new RimKataSubdueVisual
            {
                partner = state.target, facing = state.facing,
                weapon = state.weapon, attacking = state.attackAllowed
                    && (state.attackEnabled || state.externalTarget.IsValid),
                ranged = state.attackVerb != null && !state.attackVerb.IsMeleeAttack,
                aimTarget = state.externalTarget
            };
            var held = new RimKataSubdueVisual
            {
                partner = state.pawn, facing = state.facing, held = true
            };
            if (state.hostile)
            {
                int elapsed = Math.Max(0, (Find.TickManager?.TicksGame ?? 0) - state.struggleStartTick);
                float phase = Mathf.Clamp01(elapsed / 20f);
                // Smooth out and back: zero velocity at the center and extremes.
                held.struggleAngle = 15f * (1f - Mathf.Cos(phase * Mathf.PI * 2f))
                    * state.struggleSign;
            }
            if (carrier.ranged && state.attackVerb is Verb_LaunchProjectile projectile)
                EquipmentUtility.Recoil(state.weapon.def, projectile, out carrier.recoilOffset,
                    out carrier.recoilAngle, 0f);
            else if (state.visualAttackTick >= 0 && !state.visualAttackRanged)
            {
                int elapsed = (Find.TickManager?.TicksGame ?? 0) - state.visualAttackTick;
                if (elapsed >= 0 && elapsed < state.visualAttackDuration)
                {
                    carrier.weaponThrust = Mathf.Sin(Mathf.PI * elapsed / state.visualAttackDuration);
                    // Finish an actual strike toward its original victim even if
                    // the next order switches between the held and external pawn.
                    carrier.aimTarget = state.visualAttackTarget;
                }
            }
            PublishIfChanged(state.pawn, map, carrier);
            PublishIfChanged(state.target, map, held);
        }

        private static void PublishIfChanged(Pawn pawn, Map map, RimKataSubdueVisual visual)
        {
            RimKataSubdueVisual? old = RimKataResponseVisualParticipantCache.BodyVisualFor(pawn)?.subdue;
            if (old.HasValue && old.Value.partner == visual.partner
                && old.Value.facing == visual.facing && old.Value.held == visual.held
                && old.Value.attacking == visual.attacking && old.Value.weapon == visual.weapon
                && old.Value.struggleAngle == visual.struggleAngle && old.Value.ranged == visual.ranged
                && old.Value.weaponThrust == visual.weaponThrust && old.Value.aimTarget == visual.aimTarget
                && old.Value.recoilOffset == visual.recoilOffset && old.Value.recoilAngle == visual.recoilAngle) return;
            RimKataResponseVisualParticipantCache.PublishSubdue(pawn, map, visual);
        }

        internal static void Remove(RimKataSubdueState state)
        {
            if (state == null) return;
            RimKataResponseVisualParticipantCache.PublishSubdue(state.pawn, null, null);
            RimKataResponseVisualParticipantCache.PublishSubdue(state.target, null, null);
            Frames.TryRemove(state.pawn, out _);
            Frames.TryRemove(state.target, out _);
        }

        internal static void Prepare(PawnDrawParms parms, List<PawnGraphicDrawRequest> requests,
            RimKataSubdueVisual visual)
        {
            Matrix4x4 body = parms.matrix;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            // Use the rendered body, including race/life-stage scale, as the
            // upper-body pivot. Whole-body animals need no separate head node.
            for (int i = 0; i < requests.Count; i++)
            {
                Matrix4x4 matrix = requests[i].preDrawnComputedMatrix;
                bottom = Mathf.Min(bottom, matrix.m13);
                top = Mathf.Max(top, matrix.m13);
                if (requests[i].node.Props.tagDef == PawnRenderNodeTagDefOf.Body) body = matrix;
            }
            Vector3 bodyCenter = body.MultiplyPoint3x4(Vector3.zero);
            Vector3 root = parms.matrix.MultiplyPoint3x4(Vector3.zero);
            if (requests.Count == 0) bottom = top = root.y;
            if (!visual.held)
            {
                // The carrier is prepared before its Carried worker draws the
                // held pawn. Cache actual layers, including apparel and hats.
                Frames[parms.pawn] = new BodyFrame { root = root, bottom = bottom, top = top };
                return;
            }

            // Carried position's native front/back offset is not enough: the
            // held pawn has its own head/apparel layers. North and south reserve
            // a weapon band between both bodies, with the carrier in front only
            // when facing north. East/west keep the held stack behind the carrier.
            bool hasCarrier = Frames.TryGetValue(visual.partner, out BodyFrame carrier);
            float carrierBottom = hasCarrier ? carrier.bottom : root.y;
            float carrierTop = hasCarrier ? carrier.top : root.y;
            float layerOffset = visual.facing == Rot4.South
                ? carrierTop + PawnRenderUtility.AltitudeForLayer(3f) - bottom
                : carrierBottom - PawnRenderUtility.AltitudeForLayer(
                    visual.facing == Rot4.North ? 3f : 1f) - top;
            Vector3 upperBody = body.MultiplyPoint3x4(new Vector3(0f, 0f, 0.25f));
            Matrix4x4 turn = Matrix4x4.Translate(new Vector3(0f, layerOffset, 0f))
                * Matrix4x4.Translate(upperBody)
                * Matrix4x4.Rotate(Quaternion.AngleAxis(visual.struggleAngle, Vector3.up))
                * Matrix4x4.Translate(-upperBody);
            for (int i = 0; i < requests.Count; i++)
            {
                PawnGraphicDrawRequest request = requests[i];
                request.preDrawnComputedMatrix = turn * request.preDrawnComputedMatrix;
                requests[i] = request;
            }
            Frames[parms.pawn] = new BodyFrame
            {
                root = root, bottom = bottom + layerOffset, top = top + layerOffset,
                heldCenterOffset = turn.MultiplyPoint3x4(bodyCenter)
                    - (hasCarrier ? carrier.root : root)
            };
        }

        // The native carried-pawn offsets, using the participant's saved facing
        // instead of live Rotation. Position and weapon aim share this anchor.
        internal static Vector3 CarriedPosition(Pawn pawn, Vector3 root, Rot4 facing)
        {
            Vector3 offset = new Vector3(0f, 0f, -0.1f);
            if (facing == Rot4.North) offset.z = 0f;
            else if (facing == Rot4.East) offset.x = 0.48f;
            else if (facing == Rot4.West) offset.x = -0.48f;
            if (pawn.DevelopmentalStage == DevelopmentalStage.Adult) offset.z -= 0.1f;
            offset.y = facing == Rot4.North ? -0.03658537f : 0.03658537f;
            offset.z += 0.3f;
            return root + offset;
        }

        internal static Vector3 HeldCenter(Pawn pawn, Vector3 root, RimKataSubdueVisual visual)
        {
            if (Frames.TryGetValue(visual.partner, out BodyFrame held)) return root + held.heldCenterOffset;
            return CarriedPosition(pawn, root, visual.facing);
        }

        internal static float WeaponAltitude(Pawn pawn, Vector3 root, Rot4 facing)
        {
            bool hasFrame = Frames.TryGetValue(pawn, out BodyFrame frame);
            return facing == Rot4.North
                ? (hasFrame ? frame.bottom : root.y) - PawnRenderUtility.AltitudeForLayer(2f)
                : (hasFrame ? frame.top : root.y) + PawnRenderUtility.AltitudeForLayer(1f);
        }

        internal static float WeaponMaximumAltitude(Pawn pawn, Vector3 root, Rot4 facing)
            => facing == Rot4.South || facing == Rot4.North
                ? WeaponAltitude(pawn, root, facing) + PawnRenderUtility.AltitudeForLayer(1f)
                : float.PositiveInfinity;

        internal static bool PlaceCarriedPawn(Pawn pawn, Thing carried, ref Vector3 position, ref bool flip)
        {
            var visual = RimKataWorldRenderContext.BodyFor(pawn)?.subdue;
            if (!visual.HasValue || visual.Value.held || visual.Value.partner != carried) return false;
            position = CarriedPosition(pawn, position, visual.Value.facing);
            flip = false;
            return true;
        }
    }

    internal static class RimKataSubdueWeaponRender
    {
        internal struct Scope
        {
            internal Pawn pawn;
            internal Vector3 root;
            internal PawnRenderFlags flags;
            internal RimKataSubdueVisual visual;
            internal bool drawn;
        }

        [ThreadStatic] private static Scope current;

        internal static bool Active => current.pawn != null;

        internal static Scope Begin(Pawn pawn, Vector3 root, PawnRenderFlags flags,
            RimKataSubdueVisual? visual)
        {
            Scope previous = current;
            current = default;
            if ((flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache | PawnRenderFlags.Invisible)) != 0
                || !visual.HasValue || visual.Value.held) return previous;
            current = new Scope { pawn = pawn, root = root, flags = flags, visual = visual.Value };
            return previous;
        }

        internal static void End(Scope previous) => current = previous;

        internal static void DrawParticipant(PawnDrawParms parms, RimKataSubdueVisual visual)
        {
            Scope previous = Begin(parms.pawn, parms.matrix.MultiplyPoint3x4(Vector3.zero), parms.flags, visual);
            try { Draw(); }
            finally { End(previous); }
        }

        internal static bool SuppressNative(Thing weapon)
            => Active && !RimKataWeaponRenderProbe.Probing;

        internal static void Draw()
        {
            if (!Active || current.drawn) return;
            current.drawn = true;
            ThingWithComps weapon = current.visual.weapon;
            if (weapon == null) return;
            Rot4 facing = current.visual.facing;

            float factor = current.pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f;
            Vector3 offset = facing == Rot4.North ? new Vector3(0f, 0f, -0.11f)
                : facing == Rot4.East ? new Vector3(0.22f, 0f, -0.22f)
                : facing == Rot4.West ? new Vector3(-0.22f, 0f, -0.22f)
                : new Vector3(0f, 0f, -0.22f);
            Vector3 position = current.root + offset * factor;
            // North hides the weapon behind the carrier's back, while keeping
            // its full attack animation between carrier and held pawn.
            position.y = RimKataSubdueRender.WeaponAltitude(current.pawn, current.root, facing);
            bool west = facing == Rot4.West;
            float rotation = west ? -53f - weapon.def.equippedAngleOffset
                : 53f + weapon.def.equippedAngleOffset;
            Mesh mesh = west ? MeshPool.plane10Flip : MeshPool.plane10;
            if (current.visual.ranged && current.visual.attacking)
            {
                Vector3 target = TargetCenter();
                float aim = (target - position).AngleFlat();
                bool flipped = aim > 200f && aim < 340f;
                rotation = aim - 90f + (flipped ? 180f - weapon.def.equippedAngleOffset
                    : weapon.def.equippedAngleOffset);
                position += current.visual.recoilOffset.RotatedBy(aim);
                rotation += current.visual.recoilAngle;
                mesh = flipped ? MeshPool.plane10Flip : MeshPool.plane10;
            }
            else if (!current.visual.ranged && current.visual.weaponThrust > 0f)
            {
                Vector3 target = TargetCenter();
                target.y = position.y;
                position = Vector3.Lerp(position, target, current.visual.weaponThrust);
            }
            // Native carried drawing does not run equipment extras. Invoke the
            // discovered renderer here and transform every captured weapon part.
            if (RimKataWeaponRenderProbe.DrawSpecialHeldPrimary(current.pawn, weapon,
                current.root, facing, current.flags, position, rotation,
                RimKataSubdueRender.WeaponMaximumAltitude(current.pawn, current.root, facing))) return;
            Graphic graphic = weapon.Graphic;
            Material material = graphic is Graphic_StackCount stack
                ? stack.SubGraphicForStackCount(1, weapon.def).MatSingleFor(weapon)
                : graphic.MatSingleFor(weapon);
            Graphics.DrawMesh(mesh, Matrix4x4.TRS(position,
                Quaternion.AngleAxis(rotation, Vector3.up),
                new Vector3(graphic.drawSize.x, 0f, graphic.drawSize.y)), material, 0);
        }

        private static Vector3 TargetCenter()
        {
            LocalTargetInfo target = current.visual.aimTarget;
            if (!target.IsValid || target.Thing == current.visual.partner)
                return RimKataSubdueRender.HeldCenter(current.pawn, current.root, current.visual);
            return target.HasThing ? target.Thing.DrawPos : target.CenterVector3;
        }
    }

    // This is reached only when vanilla is already drawing a carried thing.
    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.CalculateCarriedDrawPos))]
    internal static class Patch_PawnRenderUtility_RimKataSubdueCarriedPosition
    {
        private static bool Prefix(Pawn pawn, Thing carriedThing, ref Vector3 carryDrawPos, ref bool flip)
            => !RimKataSubdueRender.PlaceCarriedPawn(pawn, carriedThing, ref carryDrawPos, ref flip);
    }
}
