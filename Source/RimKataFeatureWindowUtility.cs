using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataFeatureWindowUtility
    {
        internal static void DrawCheckbox(Rect rect, string label, ref bool value)
        {
            Widgets.CheckboxLabeled(rect, label, ref value, paintable: true);
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && Mouse.IsOver(rect))
                current.Use();
        }
    }
}
