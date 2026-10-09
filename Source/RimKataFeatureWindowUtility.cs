using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataFeatureWindowUtility
    {
        internal static void DrawCenteredExplanation(Rect rect, string label)
        {
            if (rect.width <= 0f) return;
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            bool previousWordWrap = Text.WordWrap;
            try
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.WordWrap = true;
                if (Text.CalcHeight(label, rect.width) > rect.height)
                    Text.Font = GameFont.Tiny;
                Widgets.Label(rect, label);
            }
            finally
            {
                Text.Font = previousFont;
                Text.Anchor = previousAnchor;
                Text.WordWrap = previousWordWrap;
            }
        }

        internal static void DrawCheckbox(Rect rect, string label, ref bool value)
        {
            Widgets.CheckboxLabeled(rect, label, ref value, paintable: true);
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && Mouse.IsOver(rect))
                current.Use();
        }
    }
}
