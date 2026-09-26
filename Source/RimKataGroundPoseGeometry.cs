using System.Runtime.CompilerServices;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Only relative geometry is retained. A shot never uses an old world-space
    // render frame or calls a weapon renderer to refresh it.
    internal static class RimKataGroundPoseGeometry
    {
        private sealed class Body
        {
            internal PawnRenderNode root;
            internal Vector3 foot;
            internal Vector3 anchor, center;
            internal Vector3? head;
            internal float northReach;
        }

        private sealed class Weapon
        {
            internal Pawn owner;
            internal Rot4 facing;
            internal Vector3 aimLocalCenter;
        }

        private static readonly ConditionalWeakTable<Pawn, Body> Bodies = new ConditionalWeakTable<Pawn, Body>();
        private static readonly ConditionalWeakTable<ThingWithComps, Weapon> Weapons = new ConditionalWeakTable<ThingWithComps, Weapon>();
        private static readonly object sync = new object();

        internal struct WeaponPlacement
        {
            internal Vector3 anchor, movedAnchor, bodyCenter;
            internal float progress;

            internal Vector3 BeforeTuck(Vector3 center) => movedAnchor + center - anchor;
            internal Vector3 Place(Vector3 center)
                => PlanarBlend(BeforeTuck(center), bodyCenter, progress * 0.2f);

            internal Vector3 PlaceAtHead(Vector3 center, Vector3 head)
            {
                Vector3 radial = center - anchor;
                float distanceFactor = 1f + Mathf.Clamp01(progress) * 0.5f;
                radial.x *= distanceFactor;
                radial.z *= distanceFactor;
                return PlanarBlend(movedAnchor, head, progress) + radial;
            }
        }

        internal static Vector3 PlanarBlend(Vector3 from, Vector3 to, float amount)
        {
            Vector3 result = Vector3.Lerp(from, to, Mathf.Clamp01(amount));
            result.y = from.y;
            return result;
        }

        internal static WeaponPlacement Placement(Vector3 anchor, Vector3 center, Vector3 foot,
            float angle, Vector3 offset, float lift, float progress)
        {
            return new WeaponPlacement
            {
                anchor = anchor,
                movedAnchor = foot + (anchor - foot).RotatedBy(angle) + offset + new Vector3(0f, 0f, lift),
                bodyCenter = foot + (center - foot).RotatedBy(angle) + offset,
                progress = progress
            };
        }

        internal static void ObserveBody(PawnDrawParms parms, Vector3 foot, Vector3 anchor,
            Vector3 center, float northReach, Vector3? head)
        {
            Body body = new Body
            {
                root = parms.pawn.Drawer.renderer.renderTree.rootNode,
                foot = (foot - parms.pawn.DrawPos).Yto0(),
                anchor = (anchor - parms.pawn.DrawPos).Yto0(),
                center = (center - parms.pawn.DrawPos).Yto0(),
                head = head.HasValue ? (head.Value - parms.pawn.DrawPos).Yto0() : (Vector3?)null,
                northReach = northReach
            };
            lock (sync)
            {
                Bodies.Remove(parms.pawn);
                Bodies.Add(parms.pawn, body);
            }
        }

        internal static void ObserveWeapon(Pawn pawn, ThingWithComps weapon, Vector3 center,
            Vector3 anchor, float aimAngle)
        {
            if (pawn == null || weapon == null || !weapon.def.IsRangedWeapon) return;
            Weapon sample = new Weapon
            {
                owner = pawn,
                facing = pawn.Rotation,
                aimLocalCenter = (center - anchor).Yto0().RotatedBy(-aimAngle)
            };
            lock (sync)
            {
                Weapons.Remove(weapon);
                Weapons.Add(weapon, sample);
            }
        }

        internal static Vector3 StandingCenter(Verb verb, Vector3 aimOrigin)
        {
            Pawn pawn = verb.CasterPawn;
            ThingWithComps weapon = verb.EquipmentSource;
            LocalTargetInfo target = verb.CurrentTarget;
            Vector3 direction = verb.CurrentTarget.IsValid
                ? (target.HasThing && target.Thing.Spawned ? target.Thing.DrawPos : target.Cell.ToVector3Shifted()) - aimOrigin
                : pawn.Rotation.FacingCell.ToVector3();
            float aim = direction.x * direction.x + direction.z * direction.z > 0.0001f
                ? Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg : pawn.Rotation.AsAngle;
            return StandingCenter(pawn, weapon, aim);
        }

        internal static Vector3 StandingCenter(Pawn pawn, ThingWithComps weapon, float aim)
        {
            Vector3 anchor = StandingAnchor(pawn);
            lock (sync)
            {
                if (Weapons.TryGetValue(weapon, out Weapon sample)
                    && sample.owner == pawn && sample.facing == pawn.Rotation)
                    return anchor + sample.aimLocalCenter.RotatedBy(aim);
            }
            // Before a weapon has ever been drawn, use its native aiming anchor.
            // Unknown custom render offsets are never guessed from a sheath.
            float distance = (0.4f + weapon.def.equippedDistanceOffset)
                * (pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f);
            return anchor + new Vector3(0f, 0f, distance).RotatedBy(aim);
        }

        private static Vector3 StandingAnchor(Pawn pawn)
        {
            lock (sync)
                return Bodies.TryGetValue(pawn, out Body body)
                    && body.root == pawn.Drawer.renderer.renderTree.rootNode
                    ? pawn.DrawPos + body.anchor : pawn.DrawPos;
        }

        internal static Vector3 AimOrigin(Pawn pawn, RimKataGroundPoseState pose)
        {
            Vector3 anchor = StandingAnchor(pawn);
            return anchor + Displacement(pawn, anchor, pose, headCentered: true);
        }

        internal static float AimAngle(Pawn pawn, ThingWithComps weapon)
        {
            if (weapon == null) return pawn.Rotation.AsAngle;
            if (RimKataDualWeaponController.TryGetVisualData(pawn, weapon, out var visual)
                && visual.target.IsValid)
            {
                Vector3 direction = visual.target.CenterVector3 - pawn.DrawPos;
                return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }
            if (pawn.stances?.curStance is Stance_Busy busy && busy.verb?.EquipmentSource == weapon
                && busy.focusTarg.IsValid)
            {
                Vector3 direction = busy.focusTarg.CenterVector3 - pawn.DrawPos;
                return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }
            return pawn.Rotation.AsAngle;
        }

        internal static bool HasUsableAnchor(Pawn pawn, ThingWithComps weapon)
        {
            if (!RimKataWeaponRenderProbe.HasSpecialRenderer(weapon.def)) return true;
            lock (sync) return Weapons.TryGetValue(weapon, out Weapon sample)
                && sample.owner == pawn && sample.facing == pawn.Rotation;
        }

        internal static Vector3 Displacement(Pawn pawn, Vector3 center, RimKataGroundPoseState pose,
            bool headCentered = false)
        {
            Body body;
            lock (sync) Bodies.TryGetValue(pawn, out body);
            if (body == null || body.root != pawn.Drawer.renderer.renderTree.rootNode)
            {
                // Rendering may not have run since loading. Read the initialized
                // head transform, without executing a draw or mutating combat.
                PawnDrawParms parms = PawnDrawParms.DefaultFor(pawn);
                if (headCentered && pose.DrawFacing.IsValid) parms.facing = pose.DrawFacing;
                parms.matrix = Matrix4x4.Translate(pawn.DrawPos);
                if (!RimKataGroundPoseHead.TryGetHeadMatrix(parms, out Matrix4x4 head)) return Vector3.zero;
                Vector3 foot = parms.matrix.MultiplyPoint3x4(new Vector3(0f, 0f, -0.5f));
                Vector3 anchor = parms.matrix.MultiplyPoint3x4(Vector3.zero);
                Vector3 bodyCenter = RimKataGroundPoseHead.TryGetBodyMatrix(parms, out Matrix4x4 bodyMatrix)
                    ? bodyMatrix.MultiplyPoint3x4(Vector3.zero) : anchor;
                body = new Body { foot = (foot - pawn.DrawPos).Yto0(),
                    anchor = (anchor - pawn.DrawPos).Yto0(), center = (bodyCenter - pawn.DrawPos).Yto0(),
                    head = (head.MultiplyPoint3x4(Vector3.zero) - pawn.DrawPos).Yto0(),
                    northReach = Mathf.Max(0f, head.m23 - foot.z) * 0.5f };
            }
            WeaponPlacement placement = Placement(pawn.DrawPos + body.anchor, pawn.DrawPos + body.center,
                pawn.DrawPos + body.foot, pose.DrawAngle, pose.DrawWeaponOffset,
                NorthLift(body.northReach, pose.angle, pose.DrawProgress), pose.DrawProgress);
            Vector3 moved = placement.Place(center);
            if (headCentered && body.head.HasValue)
            {
                Vector3 foot = pawn.DrawPos + body.foot;
                Vector3 weaponOffset = pose.DrawWeaponOffset;
                Vector3 head = foot + (pawn.DrawPos + body.head.Value - foot).RotatedBy(pose.DrawAngle)
                    + weaponOffset;
                moved = placement.PlaceAtHead(center, head);
            }
            return (moved - center).Yto0();
        }

        internal static float NorthLift(float reach, float direction, float progress)
            => reach * Mathf.Max(0f, Mathf.Cos(direction * Mathf.Deg2Rad)) * progress;
    }
}
