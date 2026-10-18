using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataSecondaryEquipmentGizmos
    {
        private sealed class Marked
        {
            internal ThingWithComps weapon;
            internal string originalLabel, appliedLabel;
        }

        private static readonly ConditionalWeakTable<Gizmo, Marked> marked = new ConditionalWeakTable<Gizmo, Marked>();
        private static bool everMarked;
        internal static readonly IEqualityComparer<Gizmo> Identity = new GizmoIdentity();

        private sealed class GizmoIdentity : IEqualityComparer<Gizmo>
        {
            public bool Equals(Gizmo first, Gizmo second) => ReferenceEquals(first, second);
            public int GetHashCode(Gizmo gizmo) => RuntimeHelpers.GetHashCode(gizmo);
        }

        internal static Gizmo Mark(Gizmo gizmo, ThingWithComps weapon)
        {
            if (gizmo == null || weapon == null) return gizmo;
            everMarked = true;
            if (!marked.TryGetValue(gizmo, out Marked entry))
            {
                entry = new Marked();
                marked.Add(gizmo, entry);
            }
            entry.weapon = weapon;
            if (!(gizmo is Command command)) return gizmo;
            if (entry.appliedLabel != null && command.defaultLabel == entry.appliedLabel) return gizmo;
            entry.originalLabel = command.defaultLabel;
            entry.appliedLabel = null;
            // Preserve existing RimKata attack labels and labels supplied by CE itself.
            if (command.defaultLabel == "RimKata"
                || command.defaultLabel?.EndsWith(" RimKata", StringComparison.Ordinal) == true) return gizmo;
            entry.appliedLabel = string.IsNullOrEmpty(command.defaultLabel) ? "RimKata" : command.defaultLabel + " RimKata";
            command.defaultLabel = entry.appliedLabel;
            return gizmo;
        }

        internal static void RestoreIfChanged(Gizmo gizmo, ThingWithComps currentSecondary)
        {
            if (gizmo == null || !marked.TryGetValue(gizmo, out Marked entry) || entry.weapon == currentSecondary) return;
            if (gizmo is Command command && entry.appliedLabel != null && command.defaultLabel == entry.appliedLabel)
                command.defaultLabel = entry.originalLabel;
            marked.Remove(gizmo);
        }

        internal static bool IsEquipmentExtra(Gizmo gizmo) => gizmo != null && marked.TryGetValue(gizmo, out _);

        internal static IEnumerable<Gizmo> RestoreLabels(IEnumerable<Gizmo> original)
            => everMarked ? RestoreAll(original) : original;

        private static IEnumerable<Gizmo> RestoreAll(IEnumerable<Gizmo> original)
        {
            foreach (Gizmo gizmo in original)
            {
                RestoreIfChanged(gizmo, null);
                yield return gizmo;
            }
        }

        private static bool GroupsWith(Gizmo first, Gizmo other)
            => IsEquipmentExtra(first) == IsEquipmentExtra(other) && first.GroupsWith(other);

        private static GizmoResult Draw(Gizmo gizmo, Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            GizmoResult result = gizmo.GizmoOnGUI(topLeft, maxWidth, parms);
            if (gizmo is Command || Event.current.type != EventType.Repaint || !IsEquipmentExtra(gizmo)) return result;
            float width = gizmo.GetWidth(maxWidth);
            Rect badge = new Rect(topLeft.x + Mathf.Max(0f, width - 56f), topLeft.y + 2f, Mathf.Min(54f, width), 16f);
            Color previousColor = GUI.color;
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            try
            {
                GUI.color = Color.white;
                Widgets.DrawBoxSolid(badge, new Color(0f, 0f, 0f, 0.65f));
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(badge, "RimKata");
            }
            finally
            {
                GUI.color = previousColor;
                Text.Font = previousFont;
                Text.Anchor = previousAnchor;
            }
            return result;
        }

        [HarmonyPatch(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid))]
        private static class GridPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                MethodInfo groups = AccessTools.Method(typeof(Gizmo), nameof(Gizmo.GroupsWith), new[] { typeof(Gizmo) });
                MethodInfo draw = AccessTools.Method(typeof(Gizmo), nameof(Gizmo.GizmoOnGUI),
                    new[] { typeof(Vector2), typeof(float), typeof(GizmoRenderParms) });
                int groupCalls = 0, drawCalls = 0;
                foreach (CodeInstruction code in codes)
                {
                    if (code.Calls(groups)) groupCalls++;
                    if (code.Calls(draw)) drawCalls++;
                }
                if (groupCalls != 1 || drawCalls != 1)
                {
                    Log.Warning("[RimKata] Secondary equipment gizmo badges could not be connected: the gizmo grid call sites changed.");
                    return codes;
                }
                MethodInfo groupWrapper = AccessTools.Method(typeof(RimKataSecondaryEquipmentGizmos), nameof(GroupsWith));
                MethodInfo drawWrapper = AccessTools.Method(typeof(RimKataSecondaryEquipmentGizmos), nameof(Draw));
                for (int i = 0; i < codes.Count; i++)
                {
                    MethodInfo replacement = codes[i].Calls(groups) ? groupWrapper : codes[i].Calls(draw) ? drawWrapper : null;
                    if (replacement == null) continue;
                    codes[i] = new CodeInstruction(codes[i]) { opcode = OpCodes.Call, operand = replacement };
                }
                return codes;
            }
        }
    }
}
