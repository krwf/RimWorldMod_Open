using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataGeneProbability : Window
    {
        private enum CorpseFilter
        {
            All,
            NonCorpses,
            Corpses
        }

        private enum ProbabilityFilter
        {
            All,
            Present,
            Absent
        }

        private sealed class RuleEditor
        {
            internal readonly RimKataGeneProbabilityRule rule;
            internal string rimKataBuffer;
            internal string dependencyBuffer;

            internal RuleEditor(RimKataGeneProbabilityRule rule)
            {
                this.rule = rule;
                RefreshBuffers();
            }

            internal void RefreshBuffers()
            {
                rimKataBuffer = rule.rimKataChancePercent.ToString();
                dependencyBuffer = rule.dependencyChancePercent.ToString();
            }
        }

        private const float RowHeight = 32f;
        private const float ColumnGap = 8f;
        private const float FieldWidth = 86f;
        private const float BottomButtonHeight = 30f;
        private const int ShamblerCategory = -2;
        private readonly RimKataSettings settings;
        private readonly List<RimKataTargetEntry> candidates;
        private readonly List<RimKataTargetEntry> filteredCandidates = new List<RimKataTargetEntry>();
        private readonly Dictionary<string, RuleEditor> rules =
            new Dictionary<string, RuleEditor>(StringComparer.Ordinal);
        private Vector2 scrollPosition;
        private string searchText = string.Empty;
        private int categoryFilter = -1;
        private CorpseFilter corpseFilter = CorpseFilter.NonCorpses;
        private ProbabilityFilter rimKataFilter;
        private ProbabilityFilter dependencyFilter;
        private bool filteredCandidatesDirty = true;
        private string pendingProbabilityControl;
        private bool commitChangesOnClose;
        private float batchRimKataChance;
        private float batchDependencyChance;
        private string batchRimKataBuffer = "0";
        private string batchDependencyBuffer = "0";
        private float rimKataColumnWidth;
        private float dependencyColumnWidth;

        public Dialog_RimKataGeneProbability(RimKataSettings settings)
        {
            this.settings = settings;
            if (settings.geneProbabilityRules != null)
            {
                foreach (RimKataGeneProbabilityRule rule in settings.geneProbabilityRules)
                {
                    if (rule == null || string.IsNullOrEmpty(rule.key))
                        continue;
                    RimKataGeneProbabilityRule copy = rule.Copy();
                    copy.Sanitize(settings.enableRimKataG, settings.enableSerumDependency);
                    rules[copy.key] = new RuleEditor(copy);
                }
            }

            candidates = RimKataGeneProbability.EligibleEntries
                .OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList();
            foreach (RimKataTargetEntry entry in candidates)
            {
                if (!rules.ContainsKey(entry.Key))
                    rules.Add(entry.Key, new RuleEditor(new RimKataGeneProbabilityRule { key = entry.Key }));
            }

            doCloseX = true;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = false;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(
            Mathf.Clamp(900f, 760f, Mathf.Max(760f, UI.screenWidth - 80f)), 720f);

        public override void DoWindowContents(Rect inRect)
        {
            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f),
                    "KRWF_RimKata_GeneProbability".Translate());
                Text.Font = GameFont.Small;
                FinishProbabilityEdit();
                float y = inRect.y + 42f;
                DrawLabel(new Rect(inRect.x, y, 90f, 30f), "KRWF_RimKata_Search".Translate(),
                    TextAnchor.MiddleCenter);
                string nextSearch = Widgets.TextField(new Rect(inRect.x + 90f, y, inRect.width - 90f, 30f), searchText);
                if (!string.Equals(nextSearch, searchText, StringComparison.Ordinal))
                {
                    searchText = nextSearch;
                    FiltersChanged();
                }
                y += 36f;

                DrawFilterControls(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;
                Widgets.Dropdown<Dialog_RimKataGeneProbability, int>(
                    new Rect(inRect.x, y, inRect.width, 30f), this, dialog => dialog.categoryFilter,
                    dialog => dialog.CategoryMenuElements(),
                    "KRWF_RimKata_TargetCategoryFilter".Translate() + ": " + CategoryLabel(categoryFilter));
                y += 36f;
                RefreshFilteredCandidates();
                DrawBatchControls(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;

                float contentWidth = inRect.width - 18f;
                SetColumnWidths(contentWidth);
                DrawRow(new Rect(inRect.x, y, contentWidth, RowHeight), null);
                y += RowHeight;
                Widgets.DrawLineHorizontal(inRect.x, y, contentWidth);
                y += 5f;

                RefreshFilteredCandidates();
                Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(0f, inRect.yMax - y - 44f));
                Rect viewRect = new Rect(0f, 0f, contentWidth,
                    Mathf.Max(outRect.height, filteredCandidates.Count * RowHeight));
                scrollPosition.y = Mathf.Clamp(scrollPosition.y, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
                Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
                if (filteredCandidates.Count == 0)
                {
                    DrawLabel(new Rect(0f, 0f, viewRect.width, RowHeight),
                        "KRWF_RimKata_NoMatchingTargets".Translate(), TextAnchor.MiddleCenter);
                }
                else
                {
                    int first = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / RowHeight));
                    int last = Mathf.Min(filteredCandidates.Count,
                        Mathf.CeilToInt((scrollPosition.y + outRect.height) / RowHeight));
                    for (int i = first; i < last; i++)
                    {
                        Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                        if (i % 2 == 1)
                            Widgets.DrawLightHighlight(row);
                        DrawRow(row, filteredCandidates[i]);
                    }
                }
                Widgets.EndScrollView();
                DrawBottomButtons(inRect);
            }
            finally
            {
                Text.Font = previousFont;
            }
        }

        protected override void LateWindowOnGUI(Rect inRect)
        {
            GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 36f), inRect.y + 36f));
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose)
                return;
            settings.geneProbabilityRules = rules.Values.Select(editor => editor.rule.Copy())
                .OrderBy(rule => rule.key, StringComparer.Ordinal).ToList();
            RimKataMod.CommitGeneProbabilitySettings();
        }

        private void SetColumnWidths(float width)
        {
            rimKataColumnWidth = Mathf.Max(FieldWidth, Text.CalcSize("KRWF_RimKata_EnableRimKataG".Translate()).x + 16f);
            dependencyColumnWidth = Mathf.Max(FieldWidth, Text.CalcSize("KRWF_RimKata_EnableSerumDependency".Translate()).x + 16f);
            float maximum = Mathf.Max(FieldWidth * 2f, width * 0.6f - ColumnGap * 2f);
            float total = rimKataColumnWidth + dependencyColumnWidth;
            if (total > maximum)
            {
                rimKataColumnWidth *= maximum / total;
                dependencyColumnWidth *= maximum / total;
            }
        }

        private void DrawRow(Rect row, RimKataTargetEntry entry)
        {
            Rect dependencyRect = new Rect(row.xMax - dependencyColumnWidth, row.y, dependencyColumnWidth, row.height);
            Rect rimKataRect = new Rect(dependencyRect.x - ColumnGap - rimKataColumnWidth, row.y, rimKataColumnWidth, row.height);
            Rect nameRect = new Rect(row.x + 4f, row.y, Mathf.Max(0f, rimKataRect.x - row.x - ColumnGap - 4f), row.height);
            if (entry == null)
            {
                DrawLabel(nameRect, "KRWF_RimKata_TargetName".Translate(), TextAnchor.MiddleLeft);
                DrawLabel(rimKataRect, "KRWF_RimKata_EnableRimKataG".Translate(), TextAnchor.MiddleCenter);
                DrawLabel(dependencyRect, "KRWF_RimKata_EnableSerumDependency".Translate(), TextAnchor.MiddleCenter);
                return;
            }

            DrawLabel(nameRect, entry.DisplayLabel, TextAnchor.MiddleLeft);
            TooltipHandler.TipRegion(nameRect, entry.DisplayLabel);
            RuleEditor editor = rules[entry.Key];
            bool changed = DrawChanceField(FieldRect(rimKataRect), ref editor.rule.rimKataChancePercent,
                ref editor.rimKataBuffer, editor.rule.dependencyChancePercent, settings.enableRimKataG);
            changed |= DrawChanceField(FieldRect(dependencyRect), ref editor.rule.dependencyChancePercent,
                ref editor.dependencyBuffer, editor.rule.rimKataChancePercent, settings.enableSerumDependency);
            if (changed && (rimKataFilter != ProbabilityFilter.All || dependencyFilter != ProbabilityFilter.All))
            {
                string focusedControl = GUI.GetNameOfFocusedControl();
                bool stillMatches = MatchesProbability(editor.rule.rimKataChancePercent, rimKataFilter)
                    && MatchesProbability(editor.rule.dependencyChancePercent, dependencyFilter);
                bool editing = focusedControl == NumericControlName(FieldRect(rimKataRect))
                    || focusedControl == NumericControlName(FieldRect(dependencyRect));
                if (!stillMatches && editing)
                    pendingProbabilityControl = focusedControl;
                else
                {
                    pendingProbabilityControl = null;
                    filteredCandidatesDirty = true;
                }
            }
        }

        private void DrawFilterControls(Rect rect)
        {
            float width = (rect.width - ColumnGap * 2f) / 3f;
            Widgets.Dropdown<Dialog_RimKataGeneProbability, CorpseFilter>(
                new Rect(rect.x, rect.y, width, rect.height), this, dialog => dialog.corpseFilter,
                dialog => dialog.CorpseMenuElements(),
                "KRWF_RimKata_TargetCorpseFilter".Translate() + ": " + CorpseLabel(corpseFilter));
            Widgets.Dropdown<Dialog_RimKataGeneProbability, ProbabilityFilter>(
                new Rect(rect.x + width + ColumnGap, rect.y, width, rect.height), this,
                dialog => dialog.rimKataFilter, dialog => dialog.ProbabilityMenuElements(true),
                "KRWF_RimKata_EnableRimKataG".Translate() + ": " + ProbabilityLabel(rimKataFilter));
            Widgets.Dropdown<Dialog_RimKataGeneProbability, ProbabilityFilter>(
                new Rect(rect.x + (width + ColumnGap) * 2f, rect.y, width, rect.height), this,
                dialog => dialog.dependencyFilter, dialog => dialog.ProbabilityMenuElements(false),
                "KRWF_RimKata_EnableSerumDependency".Translate() + ": " + ProbabilityLabel(dependencyFilter));
        }

        private void DrawBatchControls(Rect rect)
        {
            string filteredLabel = "KRWF_RimKata_FilteredResults".Translate();
            string rimKataLabel = "KRWF_RimKata_EnableRimKataG".Translate() + ":";
            string dependencyLabel = "KRWF_RimKata_EnableSerumDependency".Translate() + ":";
            float buttonWidth = Mathf.Max(90f, Text.CalcSize(filteredLabel).x + 24f);
            float rimKataLabelWidth = Text.CalcSize(rimKataLabel).x + ColumnGap;
            float dependencyLabelWidth = Text.CalcSize(dependencyLabel).x + ColumnGap;
            float labelSpace = Mathf.Max(0f, rect.width - buttonWidth - FieldWidth * 2f - ColumnGap * 3f);
            float labelTotal = rimKataLabelWidth + dependencyLabelWidth;
            if (labelTotal > labelSpace)
            {
                rimKataLabelWidth *= labelSpace / labelTotal;
                dependencyLabelWidth *= labelSpace / labelTotal;
            }
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, buttonWidth, rect.height), filteredLabel,
                active: filteredCandidates.Count > 0))
                SetFilteredChances(batchRimKataChance, batchDependencyChance);

            float x = rect.x + buttonWidth + ColumnGap;
            DrawLabel(new Rect(x, rect.y, rimKataLabelWidth, rect.height), rimKataLabel, TextAnchor.MiddleLeft);
            DrawChanceField(new Rect(x + rimKataLabelWidth, rect.y + 2f, FieldWidth, rect.height - 4f),
                ref batchRimKataChance, ref batchRimKataBuffer, batchDependencyChance, settings.enableRimKataG);
            float dependencyX = rect.xMax - FieldWidth - dependencyLabelWidth;
            DrawLabel(new Rect(dependencyX, rect.y, dependencyLabelWidth, rect.height), dependencyLabel, TextAnchor.MiddleLeft);
            DrawChanceField(new Rect(rect.xMax - FieldWidth, rect.y + 2f, FieldWidth, rect.height - 4f),
                ref batchDependencyChance, ref batchDependencyBuffer, batchRimKataChance, settings.enableSerumDependency);
        }

        private void DrawBottomButtons(Rect inRect)
        {
            string resetLabel = "KRWF_RimKata_ResetAllGeneProbabilities".Translate();
            string filteredResetLabel = "KRWF_RimKata_ResetFilteredGeneProbabilities".Translate();
            string closeLabel = "Close".Translate();
            string confirmLabel = "Confirm".Translate();
            float resetWidth = Mathf.Max(72f, Text.CalcSize(resetLabel).x + 28f);
            float filteredResetWidth = Mathf.Max(90f, Text.CalcSize(filteredResetLabel).x + 28f);
            float closeWidth = Mathf.Max(90f, Text.CalcSize(closeLabel).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize(confirmLabel).x + 28f);
            float totalWidth = closeWidth + confirmWidth + 2f * Mathf.Max(resetWidth, filteredResetWidth);
            if (totalWidth + ColumnGap * 3f > inRect.width)
            {
                float scale = Mathf.Max(0f, inRect.width - ColumnGap * 3f) / totalWidth;
                resetWidth *= scale;
                filteredResetWidth *= scale;
                closeWidth *= scale;
                confirmWidth *= scale;
            }
            float y = inRect.yMax - BottomButtonHeight;
            if (Widgets.ButtonText(new Rect(inRect.x, y, resetWidth, BottomButtonHeight), resetLabel))
            {
                RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                    "KRWF_RimKata_GeneProbability".Translate()), ResetAll);
            }
            float x = inRect.x + (inRect.width - closeWidth - ColumnGap - confirmWidth) * 0.5f;
            if (Widgets.ButtonText(new Rect(x, y, closeWidth, BottomButtonHeight), closeLabel))
                Close();
            else if (Widgets.ButtonText(new Rect(x + closeWidth + ColumnGap, y, confirmWidth, BottomButtonHeight), confirmLabel))
            {
                commitChangesOnClose = true;
                Close();
            }
            else if (Widgets.ButtonText(new Rect(inRect.xMax - filteredResetWidth, y, filteredResetWidth, BottomButtonHeight),
                filteredResetLabel, active: filteredCandidates.Count > 0))
            {
                RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                    "KRWF_RimKata_GeneProbability".Translate() + " (" + "KRWF_RimKata_FilteredResults".Translate() + ")"),
                    () => SetFilteredChances(0f, 0f));
            }
        }

        private IEnumerable<Widgets.DropdownMenuElement<CorpseFilter>> CorpseMenuElements()
        {
            foreach (CorpseFilter value in new[] { CorpseFilter.All, CorpseFilter.NonCorpses, CorpseFilter.Corpses })
            {
                yield return new Widgets.DropdownMenuElement<CorpseFilter>
                {
                    payload = value,
                    option = new FloatMenuOption(CorpseLabel(value), () =>
                    {
                        corpseFilter = value;
                        FiltersChanged();
                    })
                };
            }
        }

        private IEnumerable<Widgets.DropdownMenuElement<ProbabilityFilter>> ProbabilityMenuElements(bool rimKata)
        {
            foreach (ProbabilityFilter value in new[] { ProbabilityFilter.All, ProbabilityFilter.Present, ProbabilityFilter.Absent })
            {
                yield return new Widgets.DropdownMenuElement<ProbabilityFilter>
                {
                    payload = value,
                    option = new FloatMenuOption(ProbabilityLabel(value), () =>
                    {
                        if (rimKata)
                            rimKataFilter = value;
                        else
                            dependencyFilter = value;
                        FiltersChanged();
                    })
                };
            }
        }

        private IEnumerable<Widgets.DropdownMenuElement<int>> CategoryMenuElements()
        {
            yield return CategoryMenuElement(-1);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Humanlike);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Mechanoid);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Insect);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Animal);
            yield return CategoryMenuElement(ShamblerCategory);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Other);
        }

        private Widgets.DropdownMenuElement<int> CategoryMenuElement(int value)
        {
            return new Widgets.DropdownMenuElement<int>
            {
                payload = value,
                option = new FloatMenuOption(CategoryLabel(value), () =>
                {
                    categoryFilter = value;
                    FiltersChanged();
                })
            };
        }

        private void FiltersChanged()
        {
            pendingProbabilityControl = null;
            filteredCandidatesDirty = true;
            scrollPosition = Vector2.zero;
        }

        private void FinishProbabilityEdit()
        {
            if (pendingProbabilityControl == null)
                return;
            bool accept = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (!accept && GUI.GetNameOfFocusedControl() == pendingProbabilityControl)
                return;
            if (accept)
                GUI.FocusControl(null);
            pendingProbabilityControl = null;
            filteredCandidatesDirty = true;
        }

        private void RefreshFilteredCandidates()
        {
            if (!filteredCandidatesDirty)
                return;
            filteredCandidates.Clear();
            string query = searchText.Trim();
            foreach (RimKataTargetEntry entry in candidates)
            {
                int category = RimKataGeneProbability.IsShamblerEntry(entry)
                    ? ShamblerCategory : (int)entry.Category;
                if (categoryFilter != -1 && category != categoryFilter)
                    continue;
                if ((corpseFilter == CorpseFilter.NonCorpses && entry.IsCorpse)
                    || (corpseFilter == CorpseFilter.Corpses && !entry.IsCorpse))
                    continue;
                RimKataGeneProbabilityRule rule = rules[entry.Key].rule;
                if (!MatchesProbability(rule.rimKataChancePercent, rimKataFilter)
                    || !MatchesProbability(rule.dependencyChancePercent, dependencyFilter))
                    continue;
                if (query.Length > 0
                    && entry.Label.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0
                    && entry.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && (entry.SearchText ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0)
                    continue;
                filteredCandidates.Add(entry);
            }
            filteredCandidatesDirty = false;
        }

        private void SetFilteredChances(float rimKataChance, float dependencyChance)
        {
            if (pendingProbabilityControl != null)
            {
                pendingProbabilityControl = null;
                filteredCandidatesDirty = true;
            }
            RefreshFilteredCandidates();
            foreach (RimKataTargetEntry entry in filteredCandidates)
            {
                RuleEditor editor = rules[entry.Key];
                editor.rule.rimKataChancePercent = rimKataChance;
                editor.rule.dependencyChancePercent = dependencyChance;
                editor.RefreshBuffers();
            }
            filteredCandidatesDirty = true;
            GUI.FocusControl(null);
        }

        private void ResetAll()
        {
            foreach (RuleEditor editor in rules.Values)
            {
                editor.rule.rimKataChancePercent = 0f;
                editor.rule.dependencyChancePercent = 0f;
                editor.RefreshBuffers();
            }
            FiltersChanged();
            GUI.FocusControl(null);
        }

        private static bool DrawChanceField(Rect rect, ref float value, ref string buffer, float otherChance, bool enabled)
        {
            float previousValue = value;
            float maximum = enabled ? Mathf.Max(0f, 100f - otherChance) : 0f;
            bool previousEnabled = GUI.enabled;
            try
            {
                GUI.enabled = previousEnabled && enabled;
                RimKataSettingsDrawer.DrawFloatField(rect, ref value, ref buffer, 0f, maximum, "%");
                float clamped = float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp(value, 0f, maximum);
                bool invalidBuffer = float.TryParse(buffer, out float entered)
                    && (float.IsNaN(entered) || float.IsInfinity(entered) || entered < 0f || entered > maximum);
                if (invalidBuffer)
                    clamped = float.IsNaN(entered) || float.IsInfinity(entered) ? 0f : Mathf.Clamp(entered, 0f, maximum);
                if (value != clamped || invalidBuffer)
                {
                    value = clamped;
                    buffer = value.ToString();
                }
                return previousValue != value;
            }
            finally
            {
                GUI.enabled = previousEnabled;
            }
        }

        private static Rect FieldRect(Rect column)
        {
            float width = Mathf.Min(FieldWidth, column.width);
            return new Rect(column.center.x - width * 0.5f, column.y + 2f, width, column.height - 4f);
        }

        private static string NumericControlName(Rect rect)
        {
            return "TextField" + rect.y.ToString("F0") + rect.x.ToString("F0");
        }

        private static bool MatchesProbability(float value, ProbabilityFilter filter)
        {
            return filter == ProbabilityFilter.All || (filter == ProbabilityFilter.Present ? value > 0f : value <= 0f);
        }

        private static string CategoryLabel(int value)
        {
            if (value == ShamblerCategory)
                return "KRWF_RimKata_TargetShambler".Translate();
            switch ((RimKataTargetCategory)value)
            {
                case RimKataTargetCategory.Humanlike: return "KRWF_RimKata_TargetHumanlike".Translate();
                case RimKataTargetCategory.Mechanoid: return "KRWF_RimKata_TargetMechanoid".Translate();
                case RimKataTargetCategory.Insect: return "KRWF_RimKata_TargetInsect".Translate();
                case RimKataTargetCategory.Animal: return "KRWF_RimKata_TargetAnimal".Translate();
                case RimKataTargetCategory.Other: return "KRWF_RimKata_TargetOther".Translate();
                default: return "KRWF_RimKata_FilterAll".Translate();
            }
        }

        private static string CorpseLabel(CorpseFilter value)
        {
            switch (value)
            {
                case CorpseFilter.NonCorpses: return "KRWF_RimKata_TargetNonCorpses".Translate();
                case CorpseFilter.Corpses: return "KRWF_RimKata_TargetCorpses".Translate();
                default: return "KRWF_RimKata_FilterAll".Translate();
            }
        }

        private static string ProbabilityLabel(ProbabilityFilter value)
        {
            switch (value)
            {
                case ProbabilityFilter.Present: return "KRWF_RimKata_GeneProbabilityPresent".Translate();
                case ProbabilityFilter.Absent: return "KRWF_RimKata_GeneProbabilityAbsent".Translate();
                default: return "KRWF_RimKata_FilterAll".Translate();
            }
        }

        private static void DrawLabel(Rect rect, string text, TextAnchor anchor)
        {
            TextAnchor previousAnchor = Text.Anchor;
            bool previousWordWrap = Text.WordWrap;
            Text.Anchor = anchor;
            Text.WordWrap = false;
            Widgets.Label(rect, text);
            Text.WordWrap = previousWordWrap;
            Text.Anchor = previousAnchor;
        }
    }
}
