using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace KRWF.RimKata
{
    internal static partial class RimKataPerspectiveShiftCompat
    {
        [ThreadStatic] private static Pawn playerGizmoPawn;
        private static Verb hoveredPlayerVerb;
        private static int playerHoverFrame = -1;

        private static void ApplyPlayerGizmos(Harmony harmony, Assembly psAssembly)
        {
            MethodInfo draw = AccessTools.DeclaredMethod(psAssembly.GetType("PerspectiveShift.Avatar"),
                "DrawPlayerGizmos", Type.EmptyTypes);
            if (draw?.ReturnType != typeof(void) || draw.IsStatic)
                throw new MissingMethodException("Perspective Shift gizmo API did not match.");
            RimKataStartupPatches.Patch(harmony, draw, prefix: RenderHook(nameof(BeginPlayerGizmos)),
                transpiler: RenderHook(nameof(ReplaceGizmoDraw)), finalizer: RenderHook(nameof(EndPlayerGizmos)));
            RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(MapGizmoUtility), nameof(MapGizmoUtility.MapUIUpdate)),
                postfix: RenderHook(nameof(DrawPlayerOverlays)));
            RimKataStartupPatches.Patch(harmony, AccessTools.Method(typeof(PawnAttackGizmoUtility), "GetMeleeAttackGizmo"),
                postfix: new HarmonyMethod(typeof(RimKataPerspectiveShiftCompat), nameof(BindPlayerMeleeGizmo)) { priority = Priority.Last });
        }

        private static bool TryShowPlayerSubdueMenu(Pawn pawn, Pawn target)
        {
            if (!pawn.Drafted || target == pawn || target.Dead
                || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return false;
            FloatMenuOption option = FloatMenuOptionProvider_RimKataSubdue.OptionFor(pawn, target);
            if (option == null) return false;
            Action action = option.action;
            if (action != null)
            {
                option.action = () =>
                {
                    if (!IsControlled(pawn) || RimKataSubdueUtility.Unavailable(pawn, target) != null
                        || !pawn.CanReachImmediate(target, PathEndMode.Touch)) return;
                    CancelCombatActions(pawn);
                    NotifyGizmoOrder(pawn);
                    action();
                };
            }
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption> { option }));
            return true;
        }

        internal static void ResetPlayerGizmos()
        {
            hoveredPlayerVerb = null;
            playerHoverFrame = -1;
        }

        private static void BeginPlayerGizmos(Pawn ___pawn, out Pawn __state)
        {
            __state = playerGizmoPawn;
            playerGizmoPawn = IsControlled(___pawn) ? ___pawn : null;
            if (Event.current?.type == EventType.Repaint) ResetPlayerGizmos();
        }

        private static void EndPlayerGizmos(Pawn __state, Exception __exception)
        {
            playerGizmoPawn = __state;
            if (__exception != null) ResetPlayerGizmos();
        }

        private static IEnumerable<CodeInstruction> ReplaceGizmoDraw(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo draw = AccessTools.Method(typeof(Gizmo), nameof(Gizmo.GizmoOnGUI),
                new[] { typeof(Vector2), typeof(float), typeof(GizmoRenderParms) });
            MethodInfo input = AccessTools.Method(typeof(Gizmo), nameof(Gizmo.ProcessInput), new[] { typeof(Event) });
            MethodInfo drawWrapper = AccessTools.Method(typeof(RimKataPerspectiveShiftCompat), nameof(DrawPlayerGizmo));
            MethodInfo inputWrapper = AccessTools.Method(typeof(RimKataPerspectiveShiftCompat), nameof(ProcessPlayerGizmo));
            var codes = new List<CodeInstruction>(instructions);
            int draws = 0, inputs = 0;
            foreach (CodeInstruction code in codes)
            {
                MethodInfo replacement = null;
                if (code.Calls(draw)) { replacement = drawWrapper; draws++; }
                else if (code.Calls(input)) { replacement = inputWrapper; inputs++; }
                if (replacement == null) continue;
                code.opcode = OpCodes.Call;
                code.operand = replacement;
            }
            if (draws != 1 || inputs != 1)
                throw new InvalidOperationException("Perspective Shift gizmo draw or input call did not match.");
            return codes;
        }

        private static GizmoResult DrawPlayerGizmo(Gizmo gizmo, Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            Command_VerbTarget command = gizmo as Command_VerbTarget;
            bool controlled = playerGizmoPawn != null && command?.verb?.CasterPawn == playerGizmoPawn;
            KeyBindingDef originalKey = command?.hotKey;
            bool hideKey = controlled && originalKey != null && command.defaultLabel == "RimKata"
                && !RimKataSecondaryEquipmentGizmos.IsEquipmentExtra(command)
                && command.verb.EquipmentSource != null
                && RimKataWeaponSlotUtility.IsSecondaryWeapon(playerGizmoPawn, command.verb.EquipmentSource);
            GizmoResult result;
            try
            {
                if (hideKey) command.hotKey = null;
                result = gizmo.GizmoOnGUI(topLeft, maxWidth, parms);
            }
            finally { if (hideKey) command.hotKey = originalKey; }
            if (controlled && command.drawRadius && Event.current?.type == EventType.Repaint
                && result.State == GizmoState.Mouseover)
            {
                hoveredPlayerVerb = command.verb;
                playerHoverFrame = Time.frameCount;
            }
            return result;
        }

        private static void DrawPlayerOverlays()
        {
            DrawPlayerFocusedTargets();
            Verb verb = hoveredPlayerVerb;
            if (verb == null) return;
            Pawn pawn = verb.CasterPawn;
            if (Time.frameCount - playerHoverFrame > 1 || !IsControlled(pawn)
                || pawn?.Spawned != true || pawn.Map != Find.CurrentMap || pawn.InMentalState)
            {
                ResetPlayerGizmos();
                return;
            }
            // Match vanilla's world-update timing; the full callback also draws an unwanted vanilla ring.
            Patch_CommandVerbTarget_RimKataAutomaticRange.DrawAutomaticSearchRange(verb);
        }

        private static void ProcessPlayerGizmo(Gizmo gizmo, Event input)
        {
            gizmo.ProcessInput(input);
            if (playerGizmoPawn != null && IsControlled(playerGizmoPawn))
                gizmo.ProcessGroupInput(input, new List<Gizmo> { gizmo });
        }

        private static void BindPlayerMeleeGizmo(Pawn pawn, Gizmo __result)
        {
            if (playerGizmoPawn != pawn || !IsControlled(pawn)
                || !(__result is Command_Target command) || command.action == null) return;
            Action<LocalTargetInfo> action = command.action;
            command.action = target => ExecutePlayerMeleeTarget(pawn, action, target);
        }

        private static void ExecutePlayerMeleeTarget(Pawn pawn, Action<LocalTargetInfo> action, LocalTargetInfo target)
        {
            if (!IsControlled(pawn) || !pawn.Spawned || !pawn.Drafted) return;
            List<object> selected = Find.Selector?.SelectedObjectsListForReading;
            if (selected == null) return;
            NotifyGizmoOrder(pawn);
            // The original callback reads selection at target acceptance, after PS restored its UI-only selection.
            // Scope just that callback so existing melee/Harmony behavior still receives the original action.
            if (selected.Count == 1 && selected[0] == pawn) { action(target); return; }
            var previous = new List<object>(selected);
            selected.Clear();
            selected.Add(pawn);
            try { action(target); }
            finally { selected.Clear(); selected.AddRange(previous); }
        }
    }
}
