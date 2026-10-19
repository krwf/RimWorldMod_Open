using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataStatSelector : RimKataSettingsWindow
    {
        private sealed class Row
        {
            internal RimKataActivationStatItem item;
            internal RimKataStatActivationRule rule;
            internal string buffer;
            internal string displayLabel;
        }

        private const float RowHeight = 32f;
        private const float Gap = 8f;
        private const float ButtonHeight = 30f;
        private const float BottomHeight = ButtonHeight * 2f + Gap;
        private readonly RimKataSettings settings;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Row> filtered = new List<Row>();
        private readonly Dictionary<string, RimKataStatActivationRule> rules =
            new Dictionary<string, RimKataStatActivationRule>(StringComparer.Ordinal);
        private readonly Dictionary<string, RimKataStatActivationRule> originalRules =
            new Dictionary<string, RimKataStatActivationRule>(StringComparer.Ordinal);
        private string search = string.Empty;
        private int categoryFilter;
        private int allowedFilter;
        private bool filteredDirty = true;
        private bool commitChangesOnClose;
        private Vector2 scrollPosition;
        private float batchValue;
        private string batchBuffer = "0";
        private float allowedWidth;
        private float comparisonWidth;

        public Dialog_RimKataStatSelector(RimKataSettings settings)
        {
            this.settings = settings;
            foreach (RimKataStatActivationRule rule in settings.statActivationRules
                ?? Enumerable.Empty<RimKataStatActivationRule>())
            {
                if (rule == null || string.IsNullOrEmpty(rule.key)) continue;
                RimKataStatActivationRule copy = rule.Copy();
                copy.Sanitize();
                rules[copy.key] = copy;
                originalRules[copy.key] = copy.Copy();
            }
            foreach (RimKataActivationStatItem item in RimKataActivationStatCatalog.All)
            {
                if (!rules.TryGetValue(item.Key, out RimKataStatActivationRule rule))
                {
                    rule = new RimKataStatActivationRule { key = item.Key, value = item.DefaultValue };
                    rules.Add(rule.key, rule);
                }
                rows.Add(new Row { item = item, rule = rule, buffer = item.ToDisplay(rule.value).ToString(),
                    displayLabel = item.Label + " [" + item.Key.Substring(item.Key.IndexOf(':') + 1) + "]" });
            }
            doCloseX = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = false;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(
            Mathf.Min(1040f, Mathf.Max(680f, UI.screenWidth - 80f)),
            Mathf.Min(760f, Mathf.Max(320f, UI.screenHeight - 80f)));

        public override void DoWindowContents(Rect inRect)
        {
            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), "KRWF_RimKata_StatSelection".Translate());
                Text.Font = GameFont.Small;
                float y = inRect.y + 42f;
                string searchLabel = "KRWF_RimKata_Search".Translate();
                float searchWidth = Mathf.Max(65f, Text.CalcSize(searchLabel).x + Gap);
                Label(new Rect(inRect.x, y, searchWidth, 30f), searchLabel, TextAnchor.MiddleLeft);
                string nextSearch = Widgets.TextField(new Rect(inRect.x + searchWidth, y,
                    Mathf.Max(1f, inRect.width - searchWidth), 30f), search);
                if (search != nextSearch) { search = nextSearch; FiltersChanged(); }
                y += 36f;
                DrawFilters(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;
                RefreshFiltered();
                DrawBatch(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;
                allowedWidth = Mathf.Min(100f, Mathf.Max(42f,
                    Text.CalcSize("KRWF_RimKata_EquipmentAllowed".Translate()).x + 12f));
                comparisonWidth = Mathf.Min(170f, Mathf.Max(100f,
                    Enum.GetValues(typeof(RimKataStatComparison)).Cast<RimKataStatComparison>()
                        .Max(value => Text.CalcSize(ComparisonLabel(value)).x) + 24f));
                float contentWidth = Mathf.Max(1f, inRect.width - 18f);
                DrawRow(new Rect(inRect.x, y, contentWidth, RowHeight), null);
                y += RowHeight;
                Widgets.DrawLineHorizontal(inRect.x, y, contentWidth);
                y += 5f;
                Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(1f, inRect.yMax - y - BottomHeight - 14f));
                Rect viewRect = new Rect(0f, 0f, contentWidth, Mathf.Max(outRect.height, filtered.Count * RowHeight));
                scrollPosition.y = Mathf.Clamp(scrollPosition.y, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
                Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
                if (filtered.Count == 0)
                    Label(new Rect(0f, 0f, contentWidth, RowHeight), "KRWF_RimKata_NoMatchingDefs".Translate(), TextAnchor.MiddleCenter);
                int first = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / RowHeight));
                int last = Mathf.Min(filtered.Count, Mathf.CeilToInt((scrollPosition.y + outRect.height) / RowHeight));
                for (int i = first; i < last; i++)
                {
                    Rect rowRect = new Rect(0f, i * RowHeight, contentWidth, RowHeight);
                    if (i % 2 == 1) Widgets.DrawLightHighlight(rowRect);
                    DrawRow(rowRect, filtered[i]);
                }
                Widgets.EndScrollView();
                DrawButtons(new Rect(inRect.x, inRect.yMax - BottomHeight, inRect.width, ButtonHeight));
            }
            finally { Text.Font = previousFont; }
        }

        protected override void LateWindowOnGUI(Rect inRect)
            => GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 36f), inRect.y + 36f));

        protected override void OnSettingsClosed()
        {
            if (!commitChangesOnClose) return;
            commitChangesOnClose = false;
            List<RimKataStatActivationRule> selected = rules.Values.Where(rule =>
            {
                RimKataActivationStatItem item = RimKataActivationStatCatalog.Find(rule.key);
                return item == null || rule.enabled || rule.comparison != RimKataStatComparison.GreaterOrEqual
                    || rule.value != item.DefaultValue;
            }).OrderBy(rule => rule.key, StringComparer.Ordinal).Select(rule => rule.Copy()).ToList();
            bool changed = selected.Count != originalRules.Count || selected.Any(rule =>
                !originalRules.TryGetValue(rule.key, out RimKataStatActivationRule original)
                || original.enabled != rule.enabled || original.comparison != rule.comparison || original.value != rule.value);
            if (!changed) return;
            settings.statActivationRules = selected;
            RimKataActivationSettings.Apply(true);
            RimKataMod.CommitListSettings();
        }

        private void DrawRow(Rect rect, Row row)
        {
            float numericWidth = Mathf.Min(110f, rect.width * 0.14f);
            Rect baseline = new Rect(rect.xMax - numericWidth, rect.y, numericWidth, rect.height);
            Rect valueRect = new Rect(baseline.x - Gap - numericWidth, rect.y, numericWidth, rect.height);
            Rect comparison = new Rect(valueRect.x - Gap - comparisonWidth, rect.y, comparisonWidth, rect.height);
            Rect allowed = new Rect(comparison.x - Gap - allowedWidth, rect.y, allowedWidth, rect.height);
            Rect name = new Rect(rect.x + 4f, rect.y, Mathf.Max(1f, allowed.x - rect.x - Gap - 4f), rect.height);
            if (row == null)
            {
                Label(name, "KRWF_RimKata_TargetName".Translate(), TextAnchor.MiddleLeft);
                Label(allowed, "KRWF_RimKata_EquipmentAllowed".Translate(), TextAnchor.MiddleCenter);
                Label(comparison, "KRWF_RimKata_StatComparison".Translate(), TextAnchor.MiddleCenter);
                Label(valueRect, "KRWF_RimKata_StatValue".Translate(), TextAnchor.MiddleCenter);
                Label(baseline, "KRWF_RimKata_StatDefault".Translate(), TextAnchor.MiddleCenter);
                return;
            }
            Label(name, row.displayLabel, TextAnchor.MiddleLeft);
            TooltipHandler.TipRegion(name, row.displayLabel);
            bool enabled = row.rule.enabled;
            RimKataFeatureWindowUtility.DrawCheckbox(new Rect(allowed.center.x - 12f, allowed.y, 24f, allowed.height),
                string.Empty, ref enabled);
            if (enabled != row.rule.enabled) { row.rule.enabled = enabled; if (allowedFilter != 0) filteredDirty = true; }
            if (Widgets.ButtonText(new Rect(comparison.x, comparison.y + 2f, comparison.width, comparison.height - 4f),
                ComparisonLabel(row.rule.comparison)))
            {
                var options = new List<FloatMenuOption>();
                foreach (RimKataStatComparison choice in Enum.GetValues(typeof(RimKataStatComparison)))
                {
                    RimKataStatComparison selected = choice;
                    options.Add(new FloatMenuOption(ComparisonLabel(choice), () => row.rule.comparison = selected));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            float display = row.item.ToDisplay(row.rule.value);
            float previous = display;
            Widgets.TextFieldNumeric(new Rect(valueRect.x, valueRect.y + 2f, valueRect.width, valueRect.height - 4f),
                ref display, ref row.buffer, -float.MaxValue, float.MaxValue);
            if (display != previous && !float.IsNaN(display) && !float.IsInfinity(display))
            {
                float raw = row.item.FromDisplay(display);
                if (!float.IsNaN(raw) && !float.IsInfinity(raw)) row.rule.value = raw;
            }
            Label(baseline, row.item.DefaultLabel, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(baseline, row.item.DefaultLabel);
        }

        private void DrawFilters(Rect rect)
        {
            float width = (rect.width - Gap * 2f) / 3f;
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), RimKataActivationStatCatalog.CategoryLabel(categoryFilter)))
            {
                var options = new List<FloatMenuOption>();
                for (int i = 0; i <= 9; i++)
                {
                    int selected = i;
                    options.Add(new FloatMenuOption(RimKataActivationStatCatalog.CategoryLabel(i),
                        () => { categoryFilter = selected; FiltersChanged(); }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Widgets.ButtonText(new Rect(rect.x + width + Gap, rect.y, width, rect.height),
                "KRWF_RimKata_AllowedStateFilter".Translate() + ": " + AllowedLabel(allowedFilter)))
            {
                var options = new List<FloatMenuOption>();
                for (int i = 0; i < 3; i++)
                {
                    int selected = i;
                    options.Add(new FloatMenuOption(AllowedLabel(i), () => { allowedFilter = selected; FiltersChanged(); }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            if (Widgets.ButtonText(new Rect(rect.x + (width + Gap) * 2f, rect.y, width, rect.height), "KRWF_RimKata_FilteredResults".Translate()))
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("KRWF_RimKata_SelectFiltered".Translate(), () => SetFilteredAllowed(true)),
                    new FloatMenuOption("KRWF_RimKata_ClearFiltered".Translate(), () => SetFilteredAllowed(false))
                }));
        }

        private void DrawBatch(Rect rect)
        {
            string label = "KRWF_RimKata_FilteredResults".Translate();
            float width = Mathf.Max(90f, Text.CalcSize(label).x + 24f);
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), label, active: filtered.Count > 0))
            {
                foreach (Row row in filtered)
                {
                    float raw = row.item.FromDisplay(batchValue);
                    if (float.IsNaN(raw) || float.IsInfinity(raw)) continue;
                    row.rule.value = raw;
                    row.buffer = batchValue.ToString();
                }
                GUI.FocusControl(null);
            }
            Widgets.TextFieldNumeric(new Rect(rect.x + width + Gap, rect.y + 2f, 110f, rect.height - 4f),
                ref batchValue, ref batchBuffer, -float.MaxValue, float.MaxValue);
        }

        private void DrawButtons(Rect rect)
        {
            string reset = "KRWF_RimKata_ResetAllStrength".Translate();
            string filteredReset = "KRWF_RimKata_ResetFilteredStrength".Translate();
            float resetWidth = Mathf.Min(rect.width * 0.25f, Mathf.Max(72f, Text.CalcSize(reset).x + 24f));
            float filteredWidth = Mathf.Min(rect.width * 0.3f, Mathf.Max(90f, Text.CalcSize(filteredReset).x + 24f));
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, resetWidth, rect.height), reset))
                ConfirmReset(false);
            if (Widgets.ButtonText(new Rect(rect.xMax - filteredWidth, rect.y, filteredWidth, rect.height), filteredReset,
                active: filtered.Count > 0)) ConfirmReset(true);
            RimKataFeatureWindowUtility.DrawCenteredExplanation(new Rect(rect.x + resetWidth + Gap, rect.y,
                Mathf.Max(0f, rect.width - resetWidth - filteredWidth - Gap * 2f), rect.height),
                "KRWF_RimKata_AnyRequirement".Translate());
            float closeWidth = Mathf.Max(90f, Text.CalcSize("Close".Translate()).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize("Confirm".Translate()).x + 28f);
            float x = rect.center.x - (closeWidth + Gap + confirmWidth) * 0.5f;
            float y = rect.yMax + Gap;
            if (Widgets.ButtonText(new Rect(x, y, closeWidth, rect.height), "Close".Translate())) Close();
            else if (Widgets.ButtonText(new Rect(x + closeWidth + Gap, y, confirmWidth, rect.height), "Confirm".Translate()))
            { commitChangesOnClose = true; Close(); }
        }

        private void ConfirmReset(bool onlyFiltered)
            => RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                "KRWF_RimKata_StatSelection".Translate().ToString() + (onlyFiltered
                    ? " (" + "KRWF_RimKata_FilteredResults".Translate().ToString() + ")" : string.Empty)), () => Reset(onlyFiltered));

        private void Reset(bool onlyFiltered)
        {
            RefreshFiltered();
            foreach (Row row in onlyFiltered ? filtered : rows)
            {
                row.rule.enabled = false;
                row.rule.comparison = RimKataStatComparison.GreaterOrEqual;
                row.rule.value = row.item.DefaultValue;
                row.buffer = row.item.ToDisplay(row.rule.value).ToString();
            }
            if (!onlyFiltered)
                foreach (string key in rules.Keys.Where(key => RimKataActivationStatCatalog.Find(key) == null).ToList()) rules.Remove(key);
            GUI.FocusControl(null);
            FiltersChanged();
        }

        private void SetFilteredAllowed(bool enabled)
        {
            RefreshFiltered();
            foreach (Row row in filtered) row.rule.enabled = enabled;
            filteredDirty = true;
        }

        private void FiltersChanged() { filteredDirty = true; scrollPosition = Vector2.zero; }

        private void RefreshFiltered()
        {
            if (!filteredDirty) return;
            filteredDirty = false;
            filtered.Clear();
            string query = search.Trim();
            foreach (Row row in rows)
            {
                if (categoryFilter != 0 && row.item.Category != categoryFilter
                    || allowedFilter == 1 && !row.rule.enabled || allowedFilter == 2 && row.rule.enabled) continue;
                if (query.Length == 0 || row.displayLabel.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    filtered.Add(row);
            }
        }

        private static string AllowedLabel(int value) => (value == 1 ? "KRWF_RimKata_AllowedYes"
            : value == 2 ? "KRWF_RimKata_AllowedNo" : "KRWF_RimKata_FilterAll").Translate();

        private static string ComparisonLabel(RimKataStatComparison comparison)
            => ("KRWF_RimKata_Comparison" + comparison).Translate();

        private static void Label(Rect rect, string label, TextAnchor anchor)
        {
            TextAnchor previous = Text.Anchor;
            Text.Anchor = anchor;
            Widgets.Label(rect, label);
            Text.Anchor = previous;
        }
    }
}
