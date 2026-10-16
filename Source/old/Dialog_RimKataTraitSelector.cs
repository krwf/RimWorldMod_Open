using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataTraitSelector : Window
    {
        private sealed class Row
        {
            internal RimKataTraitActivationRule rule;
            internal string label;
            internal string displayLabel;
        }

        private const float RowHeight = 32f;
        private const float Gap = 8f;
        private const float ButtonHeight = 30f;
        private readonly RimKataSettings settings;
        private readonly List<Row> rows = new List<Row>();
        private readonly List<Row> filtered = new List<Row>();
        private readonly Dictionary<string, RimKataTraitActivationRule> rules =
            new Dictionary<string, RimKataTraitActivationRule>(StringComparer.Ordinal);
        private readonly Dictionary<string, RimKataTraitActivationRule> initialRules =
            new Dictionary<string, RimKataTraitActivationRule>(StringComparer.Ordinal);
        private string search = string.Empty;
        private bool filteredDirty = true;
        private bool commitChangesOnClose;
        private Vector2 scrollPosition;

        public Dialog_RimKataTraitSelector(RimKataSettings settings)
        {
            this.settings = settings;
            foreach (RimKataTraitActivationRule original in settings.traitActivationRules
                ?? Enumerable.Empty<RimKataTraitActivationRule>())
            {
                if (original == null || string.IsNullOrEmpty(original.defName)
                    || !original.friendly && !original.hostile) continue;
                string key = Key(original.defName, original.degree);
                rules[key] = Copy(original);
                initialRules[key] = Copy(original);
            }
            foreach (TraitDef def in DefDatabase<TraitDef>.AllDefsListForReading)
            {
                if (def.degreeDatas == null) continue;
                foreach (TraitDegreeData degree in def.degreeDatas)
                {
                    if (degree == null) continue;
                    string key = Key(def.defName, degree.degree);
                    if (!rules.TryGetValue(key, out RimKataTraitActivationRule rule))
                    {
                        rule = new RimKataTraitActivationRule { defName = def.defName, degree = degree.degree };
                        rules.Add(key, rule);
                    }
                    string label = (degree.label.NullOrEmpty() ? def.label ?? def.defName : degree.label).CapitalizeFirst();
                    rows.Add(new Row { rule = rule, label = label, displayLabel = label + " [" + def.defName + "]" });
                }
            }
            rows.Sort((left, right) =>
            {
                int compared = StringComparer.CurrentCultureIgnoreCase.Compare(left.label, right.label);
                if (compared != 0) return compared;
                compared = StringComparer.Ordinal.Compare(left.rule.defName, right.rule.defName);
                return compared != 0 ? compared : left.rule.degree.CompareTo(right.rule.degree);
            });
            doCloseX = true;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = false;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(
            Mathf.Min(820f, Mathf.Max(420f, UI.screenWidth - 80f)),
            Mathf.Min(720f, Mathf.Max(300f, UI.screenHeight - 80f)));

        public override void DoWindowContents(Rect inRect)
        {
            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f),
                    "KRWF_RimKata_TraitSelection".Translate());
                Text.Font = GameFont.Small;
                float y = inRect.y + 42f;
                string searchLabel = "KRWF_RimKata_Search".Translate();
                float searchWidth = Mathf.Max(65f, Text.CalcSize(searchLabel).x + Gap);
                Label(new Rect(inRect.x, y, searchWidth, 30f), searchLabel, TextAnchor.MiddleLeft);
                string nextSearch = Widgets.TextField(new Rect(inRect.x + searchWidth, y,
                    Mathf.Max(1f, inRect.width - searchWidth), 30f), search);
                if (nextSearch != search)
                {
                    search = nextSearch;
                    filteredDirty = true;
                    scrollPosition = Vector2.zero;
                }
                y += 36f;
                RefreshFiltered();
                float contentWidth = Mathf.Max(1f, inRect.width - 18f);
                float sideWidth = Mathf.Max(50f, Mathf.Max(Text.CalcSize("KRWF_RimKata_TargetFriendly".Translate()).x,
                    Text.CalcSize("KRWF_RimKata_TargetHostile".Translate()).x) + 16f);
                sideWidth = Mathf.Min(sideWidth, contentWidth * 0.25f);
                DrawRow(new Rect(inRect.x, y, contentWidth, RowHeight), null, sideWidth);
                y += RowHeight;
                Widgets.DrawLineHorizontal(inRect.x, y, contentWidth);
                y += 5f;
                Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(1f, inRect.yMax - y - 44f));
                Rect viewRect = new Rect(0f, 0f, contentWidth, Mathf.Max(outRect.height, filtered.Count * RowHeight));
                scrollPosition.y = Mathf.Clamp(scrollPosition.y, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
                Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
                if (filtered.Count == 0)
                    Label(new Rect(0f, 0f, contentWidth, RowHeight), "KRWF_RimKata_NoMatchingDefs".Translate(),
                        TextAnchor.MiddleCenter);
                int first = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / RowHeight));
                int last = Mathf.Min(filtered.Count, Mathf.CeilToInt((scrollPosition.y + outRect.height) / RowHeight));
                for (int i = first; i < last; i++)
                {
                    Rect row = new Rect(0f, i * RowHeight, contentWidth, RowHeight);
                    if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                    DrawRow(row, filtered[i], sideWidth);
                }
                Widgets.EndScrollView();
                DrawButtons(new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight));
            }
            finally { Text.Font = previousFont; }
        }

        protected override void LateWindowOnGUI(Rect inRect)
            => GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 36f), inRect.y + 36f));

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose) return;
            List<RimKataTraitActivationRule> selected = rules.Values.Where(rule => rule.friendly || rule.hostile)
                .OrderBy(rule => rule.defName, StringComparer.Ordinal).ThenBy(rule => rule.degree)
                .Select(Copy).ToList();
            bool changed = selected.Count != (settings.traitActivationRules?.Count ?? 0)
                || selected.Count != initialRules.Count || selected.Any(rule =>
                !initialRules.TryGetValue(Key(rule.defName, rule.degree), out RimKataTraitActivationRule original)
                || original.friendly != rule.friendly || original.hostile != rule.hostile);
            if (!changed) return;
            settings.traitActivationRules = selected;
            RimKataActivationSettings.Apply(true);
            RimKataMod.CommitListSettings();
        }

        private static string Key(string defName, int degree)
            => defName + ":" + degree.ToString(CultureInfo.InvariantCulture);

        private static RimKataTraitActivationRule Copy(RimKataTraitActivationRule original)
            => new RimKataTraitActivationRule
            {
                defName = original.defName, degree = original.degree,
                friendly = original.friendly, hostile = original.hostile
            };

        private void RefreshFiltered()
        {
            if (!filteredDirty) return;
            filteredDirty = false;
            filtered.Clear();
            string query = search.Trim();
            foreach (Row row in rows)
                if (query.Length == 0 || row.displayLabel.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    filtered.Add(row);
        }

        private static void DrawRow(Rect rect, Row row, float sideWidth)
        {
            Rect hostile = new Rect(rect.xMax - sideWidth, rect.y, sideWidth, rect.height);
            Rect friendly = new Rect(hostile.x - Gap - sideWidth, rect.y, sideWidth, rect.height);
            Rect name = new Rect(rect.x + 4f, rect.y, Mathf.Max(1f, friendly.x - rect.x - Gap - 4f), rect.height);
            if (row == null)
            {
                Label(name, "KRWF_RimKata_TargetName".Translate(), TextAnchor.MiddleLeft);
                Label(friendly, "KRWF_RimKata_TargetFriendly".Translate(), TextAnchor.MiddleCenter);
                Label(hostile, "KRWF_RimKata_TargetHostile".Translate(), TextAnchor.MiddleCenter);
                return;
            }
            Label(name, row.displayLabel, TextAnchor.MiddleLeft);
            TooltipHandler.TipRegion(name, row.displayLabel);
            RimKataFeatureWindowUtility.DrawCheckbox(new Rect(friendly.center.x - 12f, friendly.y, 24f, friendly.height),
                string.Empty, ref row.rule.friendly);
            RimKataFeatureWindowUtility.DrawCheckbox(new Rect(hostile.center.x - 12f, hostile.y, 24f, hostile.height),
                string.Empty, ref row.rule.hostile);
        }

        private void DrawButtons(Rect rect)
        {
            string resetLabel = "KRWF_RimKata_ResetTraitSelection".Translate();
            float resetWidth = Mathf.Max(72f, Text.CalcSize(resetLabel).x + 28f);
            float closeWidth = Mathf.Max(90f, Text.CalcSize("Close".Translate()).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize("Confirm".Translate()).x + 28f);
            float scale = Mathf.Min(1f, Mathf.Max(0f, rect.width - Gap * 3f)
                / (resetWidth * 2f + closeWidth + confirmWidth));
            resetWidth *= scale;
            closeWidth *= scale;
            confirmWidth *= scale;
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, resetWidth, rect.height), resetLabel))
                RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                    "KRWF_RimKata_TraitSelection".Translate()), ResetSelection);
            float x = rect.center.x - (closeWidth + Gap + confirmWidth) * 0.5f;
            if (Widgets.ButtonText(new Rect(x, rect.y, closeWidth, rect.height), "Close".Translate())) Close();
            else if (Widgets.ButtonText(new Rect(x + closeWidth + Gap, rect.y, confirmWidth, rect.height), "Confirm".Translate()))
            {
                commitChangesOnClose = true;
                Close();
            }
        }

        private void ResetSelection()
        {
            foreach (RimKataTraitActivationRule rule in rules.Values) rule.friendly = rule.hostile = false;
        }

        private static void Label(Rect rect, string text, TextAnchor anchor)
        {
            TextAnchor previous = Text.Anchor;
            Text.Anchor = anchor;
            Widgets.Label(rect, text);
            Text.Anchor = previous;
        }
    }
}
