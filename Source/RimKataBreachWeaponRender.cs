using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    // Breach owns its carried pose. It never feeds an idle angle back through
    // DrawCarriedWeapon, DrawEquipmentAiming, or the normal dual-weapon renderer.
    internal static class RimKataBreachWeaponRender
    {
        internal struct Scope
        {
            internal Pawn pawn;
            internal ThingWithComps primary, secondary;
            internal Vector3 root;
            internal RimKataBreachVisual visual;
            internal PawnRenderFlags flags;
            internal bool drawn;
        }

        [ThreadStatic] private static Scope current;
        private static Mesh secondaryPlane, secondaryFlippedPlane;

        internal static bool Active => current.pawn != null;

        internal static bool Owns(Pawn pawn)
        {
            if (pawn == null) return false;
            if (current.pawn == pawn) return true;
            return RimKataBreachUtility.TryGetVisual(pawn, out var visual) && UsesCarriedPose(pawn, visual);
        }

        private static bool UsesCarriedPose(Pawn pawn, RimKataBreachVisual visual)
        {
            // Only published breach participants reach loadout or target checks.
            if ((!visual.poseActive && !visual.protectedPose)
                || pawn.Dead || pawn.Downed) return false;
            if (visual.holdWeapons) return true;

            // After control is released, actual attacks keep their existing
            // per-weapon animations. Only the fixed-facing idle pose stays here.
            if (pawn.stances?.curStance is Stance_Busy busy
                && !busy.neverAimWeapon && busy.focusTarg.IsValid) return false;
            return !RimKataDualWeaponController.TryGetNextAim(pawn, out _, out _);
        }

        internal static Scope Begin(Pawn pawn, Vector3 root, PawnRenderFlags flags,
            bool participant, RimKataBreachVisual visual)
        {
            Scope previous = current;
            current = default;
            if ((flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache | PawnRenderFlags.Invisible)) != 0
                || !participant || !UsesCarriedPose(pawn, visual)
                || !RimKataVisualUtility.TryGetCachedWorldLoadout(pawn, out var primary, out var secondary)
                || primary == null) return previous;
            current = new Scope
            {
                pawn = pawn, primary = primary,
                secondary = RimKataVisualUtility.IsSecondaryUsable(pawn, primary, secondary) ? secondary : null,
                root = root, visual = visual, flags = flags
            };
            return previous;
        }

        internal static void End(Scope previous) => current = previous;

        internal static bool SuppressNative(Thing weapon)
            => Active && !RimKataWeaponRenderProbe.Probing
                && (weapon == current.primary || weapon == current.secondary);

        internal static void Draw()
        {
            if (!Active || current.drawn) return;
            current.drawn = true;
            DrawWeapon(current.primary, false);
            if (current.secondary == null) return;
            DrawWeapon(current.secondary, true);
            // Weapons belong to this renderer; SYS/Miho sheaths remain separate.
            RimKataWeaponRenderProbe.DrawSecondaryExtras(current.pawn, current.primary, current.secondary,
                current.root, current.visual.facing, current.flags);
        }

        private static void DrawWeapon(ThingWithComps weapon, bool secondary)
        {
            Rot4 facing = current.visual.facing;
            bool side = facing == Rot4.East || facing == Rot4.West;
            float factor = current.pawn.ageTracker?.CurLifeStage?.equipmentDrawDistanceFactor ?? 1f;
            Vector3 offset = facing == Rot4.North ? new Vector3(0f, 0f, -0.11f)
                : facing == Rot4.East ? new Vector3(0.22f, 0f, -0.22f)
                : facing == Rot4.West ? new Vector3(-0.22f, 0f, -0.22f)
                : new Vector3(0f, 0f, -0.22f);
            Vector3 position = current.root + offset * factor;

            // East swaps the screen heights; west retains the ordinary slot
            // order. North/south put the second weapon just below the first.
            if (side && secondary == (facing == Rot4.West))
                position.z = current.root.z + (position.z - current.root.z) * 0.5f;
            if (secondary)
                position.y = side && !current.visual.poseActive
                    ? 2f * Altitudes.AltitudeFor(AltitudeLayer.Pawn) - position.y
                    : position.y - 0.001f;

            // Choose west once, directly. 217 degrees must never be turned
            // back into the east-facing 143-degree pose by a later adjustment.
            bool west = facing == Rot4.West;
            float angle = west ? -53f - weapon.def.equippedAngleOffset
                : 53f + weapon.def.equippedAngleOffset;
            Mesh mesh = west ? MeshPool.plane10Flip : MeshPool.plane10;
            if (secondary && !side)
            {
                angle = 2f * facing.AsAngle - angle - 180f;
                mesh = SecondaryMesh(west);
            }
            Graphic graphic = weapon.Graphic;
            Material material = graphic is Graphic_StackCount stack
                ? stack.SubGraphicForStackCount(1, weapon.def).MatSingleFor(weapon)
                : graphic.MatSingleFor(weapon);
            Matrix4x4 matrix = Matrix4x4.TRS(position, Quaternion.AngleAxis(angle, Vector3.up),
                new Vector3(graphic.drawSize.x, 0f, graphic.drawSize.y));
            // This is the breach transform only, not the fall/prone pipeline.
            Graphics.DrawMesh(mesh, RimKataBreachRender.TransformEquipment(current.pawn, matrix), material, 0);
        }

        private static Mesh SecondaryMesh(bool flipped)
        {
            Mesh cached = flipped ? secondaryFlippedPlane : secondaryPlane;
            if (cached != null) return cached;
            cached = UnityEngine.Object.Instantiate(flipped ? MeshPool.plane10Flip : MeshPool.plane10);
            Vector2[] uv = cached.uv;
            for (int i = 0; i < uv.Length; i++) uv[i].y = 1f - uv[i].y;
            cached.uv = uv;
            if (flipped) secondaryFlippedPlane = cached;
            else secondaryPlane = cached;
            return cached;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    internal static class Patch_PawnRenderUtility_RimKataBreachWeapon
    {
        [HarmonyPriority(Priority.First + 100)]
        private static bool Prefix(Thing eq) => !RimKataBreachWeaponRender.SuppressNative(eq);
    }
}
