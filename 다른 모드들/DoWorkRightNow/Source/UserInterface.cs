using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace KRWF.DWRN
{
    /// <summary>
    /// The armed header-selection tool is transient UI state. The actual forced
    /// work type lives in ForcedWorkMapComponent and is saved with its map.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class WorkForceUI
    {
        private static readonly Texture2D WarningIcon =
            ContentFinder<Texture2D>.Get("UI/Warning");

        private static bool armed;
        private static Map armedMap;
        private static Game armedGame;

        public static bool Armed => armed && armedGame == Current.Game &&
            armedMap == Find.CurrentMap && WorkTabIsOpen;

        private static bool WorkTabIsOpen => Find.MainTabsRoot != null &&
            Find.MainTabsRoot.OpenTab != null &&
            Find.MainTabsRoot.OpenTab.TabWindow is MainTabWindow_Work;

        public static void ResetTool()
        {
            armed = false;
            armedMap = null;
            armedGame = null;
        }

        public static void BeforeRootGUI()
        {
            if (armed && !Armed)
                ResetTool();

            if (!Armed || Event.current == null)
                return;

            // Consume cancel before vanilla closes the Work window or opens a
            // right-click order menu. Existing forced work remains enabled.
            Event ev = Event.current;
            if ((ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape) ||
                (ev.type == EventType.MouseDown && ev.button == 1))
            {
                ResetTool();
                ev.Use();
            }
        }

        public static void DrawCursor()
        {
            if (!Armed || Event.current == null || Event.current.type != EventType.Repaint)
                return;

            Vector2 mouse = Event.current.mousePosition;
            float x = Mathf.Clamp(mouse.x + 18f, 0f, UI.screenWidth - 28f);
            float y = Mathf.Clamp(mouse.y + 18f, 0f, UI.screenHeight - 28f);
            // Use the same layered mouse attachment as vanilla commands. It is
            // registered before WindowStackOnGUI, so windows cannot cover it.
            GenUI.DrawMouseAttachment((Texture)WarningIcon,
                customRect: new Rect(x, y, 26f, 26f));
        }

        public static void DrawToolbar(Rect rect)
        {
            if (Find.CurrentMap == null || Event.current == null)
                return;

            ForcedWorkMapComponent component = ForcedWorkMapComponent.For(Find.CurrentMap);
            if (component == null)
                return;

            // Vanilla 1.6/1.7 draw the priority labels at x=370..530 and
            // x=630..790. Their shared midpoint leaves this tool in the gap.
            float x = Mathf.Max(rect.x + 160f, Mathf.Min(rect.x + 566f, rect.xMax - 32f));
            Rect button = new Rect(x, rect.y + 1f, 28f, 28f);
            bool over = Mouse.IsOver(button);
            bool isArmed = Armed;

            if (Event.current.type == EventType.Repaint)
            {
                if (isArmed || over)
                    Widgets.DrawBoxSolid(button, new Color(0.68f, 0.16f, 0.10f, isArmed ? 0.56f : 0.24f));
                if (component.Enabled || isArmed)
                    DrawGradientBorder(button, 3, 0.9f);

                Color oldColor = GUI.color;
                try
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(button.ContractedBy(2f), WarningIcon);
                }
                finally
                {
                    GUI.color = oldColor;
                }
            }

            string status = component.Enabled
                ? "DWRN.ActiveWork".Translate(component.ForcedWork.labelShort).ToString()
                : "DWRN.NoForcedWork".Translate().ToString();
            string tip = "DWRN.ToolTitle".Translate() + "\n\n" +
                "DWRN.ToolInstructions".Translate() + "\n\n" + status;
            if (isArmed)
                tip += "\n" + "DWRN.ToolArmed".Translate();
            TooltipHandler.TipRegion(button, tip);

            if (over && Event.current.type == EventType.MouseDown && Event.current.button == 0)
            {
                if (isArmed)
                    ResetTool();
                else
                {
                    // Avoid keeping a world designator armed under this UI tool.
                    Find.DesignatorManager.Deselect();
                    armed = true;
                    armedMap = Find.CurrentMap;
                    armedGame = Current.Game;
                }
                SoundDefOf.Click.PlayOneShotOnCamera();
                Event.current.Use();
            }
        }

        public static bool HandleHeaderClick(PawnColumnWorker_WorkPriority column)
        {
            if (!Armed || column.def.workType == null)
                return true;

            // The base header widget has already used the mouse event. Its
            // button field still identifies the click; skip sorting/shift edits.
            if (Event.current.button != 0)
                return false;

            ForcedWorkMapComponent component = ForcedWorkMapComponent.For(Find.CurrentMap);
            if (component == null)
                return false;

            WorkTypeDef type = column.def.workType;
            bool disabling = component.ForcedWork == type;
            if (!disabling && (type.workGiversByPriority == null || type.workGiversByPriority.Count == 0))
            {
                Messages.Message("DWRN.NoWorkGivers".Translate(type.labelShort),
                    MessageTypeDefOf.RejectInput, false);
                return false;
            }

            component.SetForcedWork(disabling ? null : type);
            if (disabling)
                SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera();
            else
                SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera();
            Messages.Message((disabling ? "DWRN.WorkDisabled" : "DWRN.WorkEnabled").Translate(type.labelShort),
                MessageTypeDefOf.NeutralEvent, false);
            return false;
        }

        public static void DrawHeader(Rect rect, PawnColumnWorker_WorkPriority column, bool foreground)
        {
            if (!WorkTabIsOpen || Event.current == null || Event.current.type != EventType.Repaint ||
                column.def.workType == null)
                return;

            ForcedWorkMapComponent component = ForcedWorkMapComponent.For(Find.CurrentMap);
            bool selected = component != null && component.ForcedWork == column.def.workType;
            if (!selected)
                return;

            // Reproduce only the label bounds, not its private size cache. Long
            // labels can extend outside their narrow columns, so outlining the
            // full column would put a vertical red line across the lettering.
            GameFont oldFont = Text.Font;
            Rect labelRect;
            try
            {
                Text.Font = GameFont.Small;
                Vector2 size = Text.CalcSize(column.def.workType.labelShort.CapitalizeFirst());
                labelRect = new Rect(rect.center.x - size.x / 2f,
                    rect.y + (column.def.moveWorkTypeLabelDown ? 20f : 0f), size.x, size.y)
                    .ContractedBy(-2f);
            }
            finally
            {
                Text.Font = oldFont;
            }
            if (foreground)
            {
                DrawGradientBorder(labelRect, 2, 0.82f);
            }
            else
            {
                Widgets.DrawBoxSolid(labelRect, new Color(0.85f, 0.08f, 0.035f, 0.16f));
            }
        }

        public static string HeaderTip(PawnColumnWorker_WorkPriority column, string original)
        {
            if (!WorkTabIsOpen || column.def.workType == null)
                return original;

            ForcedWorkMapComponent component = ForcedWorkMapComponent.For(Find.CurrentMap);
            bool selected = component != null && component.ForcedWork == column.def.workType;
            if (Armed)
            {
                string action = (selected ? "DWRN.HeaderRelease" : "DWRN.HeaderForce")
                    .Translate(column.def.workType.labelShort).ToString();
                // Hide the vanilla sort/shift instructions while this tool owns
                // the header click; keep the work description readable.
                return action + "\n\n" + column.def.workType.description + "\n\n" +
                    "DWRN.ToolArmed".Translate();
            }
            if (selected)
                return "DWRN.ActiveWork".Translate(column.def.workType.labelShort) + "\n\n" + original;
            return original;
        }

        public static bool ShouldHighlight
        {
            get
            {
                ForcedWorkMapComponent component = ForcedWorkMapComponent.For(Find.CurrentMap);
                return Armed || (component != null && component.Enabled);
            }
        }

        public static void DrawWindowBorder(Window window)
        {
            if (window is MainTabWindow_Work && ShouldHighlight && window.IsOpen)
                DrawGradientBorder(window.windowRect.AtZero(), 7, 0.95f);
        }

        public static void DrawMainButton(MainButtonWorker worker, Rect rect)
        {
            if (worker.def == null || worker.def.defName != "Work" || !ShouldHighlight)
                return;

            DrawGradientBorder(rect, 5, 0.95f);
            ForcedWorkMapComponent component = ForcedWorkMapComponent.For(Find.CurrentMap);
            string tip = component != null && component.Enabled
                ? "DWRN.TabActive".Translate(component.ForcedWork.labelShort).ToString()
                : "DWRN.TabArmed".Translate().ToString();
            TooltipHandler.TipRegion(rect, tip);
        }

        private static void DrawGradientBorder(Rect rect, int layers, float opacity)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint)
                return;

            Color oldColor = GUI.color;
            try
            {
                GUI.color = Color.white;
                for (int i = 0; i < layers; i++)
                {
                    Rect r = rect.ContractedBy(i);
                    if (r.width <= 2f || r.height <= 2f)
                        break;
                    float fade = 1f - (float)i / layers;
                    Color color = new Color(1f, 0.10f + 0.10f * fade, 0.055f,
                        opacity * fade * fade);
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), color);
                    Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), color);
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y + 1f, 1f, r.height - 2f), color);
                    Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y + 1f, 1f, r.height - 2f), color);
                }
            }
            finally
            {
                GUI.color = oldColor;
            }
        }
    }

    [HarmonyPatch(typeof(MainTabWindow_Work), nameof(MainTabWindow_Work.DoWindowContents))]
    internal static class WorkToolbarPatch
    {
        private static void Postfix(Rect rect) => WorkForceUI.DrawToolbar(rect);
    }

    [HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), "HeaderClicked")]
    internal static class WorkHeaderClickPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(PawnColumnWorker_WorkPriority __instance) =>
            WorkForceUI.HandleHeaderClick(__instance);
    }

    [HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), nameof(PawnColumnWorker_WorkPriority.DoHeader))]
    internal static class WorkHeaderDrawingPatch
    {
        private static void Prefix(Rect rect, PawnColumnWorker_WorkPriority __instance) =>
            WorkForceUI.DrawHeader(rect, __instance, false);

        private static void Postfix(Rect rect, PawnColumnWorker_WorkPriority __instance) =>
            WorkForceUI.DrawHeader(rect, __instance, true);
    }

    [HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), "GetHeaderTip")]
    internal static class WorkHeaderTooltipPatch
    {
        private static void Postfix(PawnColumnWorker_WorkPriority __instance, ref string __result) =>
            __result = WorkForceUI.HeaderTip(__instance, __result);
    }

    [HarmonyPatch(typeof(MainButtonWorker), nameof(MainButtonWorker.DoButton))]
    internal static class WorkMainButtonPatch
    {
        private static void Postfix(MainButtonWorker __instance, Rect rect) =>
            WorkForceUI.DrawMainButton(__instance, rect);
    }

    // Draw inside the window's GUI callback. This preserves modal layering and
    // also works when the 1.7 GUI implementation queues its window callbacks.
    [HarmonyPatch(typeof(Window), "InnerWindowOnGUI")]
    internal static class WorkWindowBorderPatch
    {
        private static void Postfix(Window __instance) => WorkForceUI.DrawWindowBorder(__instance);
    }

    [HarmonyPatch(typeof(Window), nameof(Window.PostClose))]
    internal static class WorkWindowClosePatch
    {
        private static void Postfix(Window __instance)
        {
            if (__instance is MainTabWindow_Work)
                WorkForceUI.ResetTool();
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    internal static class WorkCursorPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            WorkForceUI.BeforeRootGUI();
            WorkForceUI.DrawCursor();
        }
    }
}
