using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static partial class RimKataPerspectiveShiftCompat
    {
        [ThreadStatic] private static Pawn externalAimPawn;

        internal static void ApplyRender(Harmony harmony, Assembly psAssembly)
        {
            Type renderer = psAssembly.GetType(
                "PerspectiveShift.PawnRenderUtility_DrawEquipmentAndApparelExtras_Patch");
            MethodInfo render = renderer == null ? null : AccessTools.DeclaredMethod(renderer, "Prefix",
                new[] { typeof(Pawn), typeof(Vector3), typeof(Rot4), typeof(PawnRenderFlags) });
            if (render?.ReturnType != typeof(bool) || !render.IsStatic)
                throw new MissingMethodException("Perspective Shift equipment rendering API did not match.");

            RimKataStartupPatches.Patch(harmony, render,
                prefix: RenderHook(nameof(BeginExternalAim)), finalizer: RenderHook(nameof(EndExternalAim)));
            Patch(harmony, AccessTools.Method(psAssembly.GetType("PerspectiveShift.Avatar"), "UpdateCamera"),
                transpiler: nameof(FollowBreachRender));
            ApplyPlayerGizmos(harmony, psAssembly);
        }

        private static IEnumerable<CodeInstruction> FollowBreachRender(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            MethodInfo follow = AccessTools.Method(typeof(RimKataPerspectiveShiftCompat), nameof(BreachCameraPosition));
            int matches = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!(codes[i].operand is MethodInfo method) || method.DeclaringType != typeof(IntVec3)
                    || method.Name != nameof(IntVec3.ToVector3ShiftedWithAltitude)
                    || method.ReturnType != typeof(Vector3)) continue;
                // Replace only the cell fallback; PS camera locks and physics coordinates keep priority.
                codes.InsertRange(i + 1, new[] {
                    new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, follow) });
                i += 2;
                matches++;
            }
            if (matches != 1)
                throw new InvalidOperationException("Perspective Shift camera position fallback did not match.");
            return codes;
        }

        private static Vector3 BreachCameraPosition(Vector3 position, object avatar)
        {
            Pawn pawn = ControlledPawn;
            if (!ReferenceEquals(avatar, controlledAvatar)
                || !(pawn?.jobs?.curDriver is JobDriver_RimKataBreach) || !IsControlled(pawn)) return position;
            Vector3 rendered = pawn.DrawPos;
            rendered.y = position.y;
            return rendered;
        }

        private static HarmonyMethod RenderHook(string name)
            => new HarmonyMethod(typeof(RimKataPerspectiveShiftCompat), name);

        private static void BeginExternalAim(Pawn pawn, out Pawn __state)
        {
            __state = externalAimPawn;
            externalAimPawn = IsControlled(pawn) ? pawn : null;
        }

        private static void EndExternalAim(Pawn __state) => externalAimPawn = __state;

        // This exists only while PS submits its mouse-aimed weapon draw. It never creates a combat target.
        internal static bool HasExternalAim(Pawn pawn) => externalAimPawn != null && externalAimPawn == pawn;

        private static void DrawPlayerFocusedTargets()
        {
            Pawn pawn = ControlledPawn;
            if (!IsControlled(pawn) || HasManualFireInput(pawn) || !pawn.Spawned || pawn.Map != Find.CurrentMap
                || WorldRendererUtility.WorldSelected || Find.Selector.IsSelected(pawn)
                || RimKataTargetAccess.SettingsFor(pawn)?.showFocusedAttackLine == false
                || !RimKataCombatStatePresenceCache.TryGetOwner(pawn, out RimKataMapComponent owner)) return;
            // PS need not select its avatar. Read only that pawn's existing two weapon cycles.
            RimKataPawnCombatState state = owner.GetState(pawn, false);
            DrawPlayerFocusedTarget(pawn, state?.primaryWeaponCycle);
            DrawPlayerFocusedTarget(pawn, state?.secondaryWeaponCycle);
        }

        private static void DrawPlayerFocusedTarget(Pawn pawn, RimKataWeaponCycleState cycle)
        {
            if (!LiveFocus(pawn, cycle)) return;
            RimKataDualWeaponRenderUtility.DrawFocusedTargetLine(
                pawn, cycle.focusedTarget, cycle.focusedTargetFromAttackGizmo);
        }

    }
}
