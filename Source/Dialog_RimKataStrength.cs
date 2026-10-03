using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataStrength : Window
    {
        private sealed class RowEditor
        {
            internal Def def;
            internal RimKataStrengthRule rule;
            internal int category;
            internal string label;
            internal string displayLabel;
            internal string buffer;
            internal float displayedDefault;
        }

        private const float RowHeight = 32f;
        private const float Gap = 8f;
        private const float FieldWidth = 86f;
        private const int ImplantCategory = 7;
        private readonly RimKataSettings settings;
        private readonly List<RowEditor> rows = new List<RowEditor>();
        private readonly List<RowEditor> filtered = new List<RowEditor>();
        private readonly Dictionary<string, RimKataStrengthRule> rules = new Dictionary<string, RimKataStrengthRule>(StringComparer.Ordinal);
        private string search = string.Empty;
        private int categoryFilter;
        private int allowedFilter;
        private bool filteredDirty = true;
        private bool commitChangesOnClose;
        private Vector2 scrollPosition;
        private float batchPercent;
        private string batchBuffer;
        private float infoWidth;
        private float percentWidth;
        private float allowedWidth;
        private string overrideTip;

        public Dialog_RimKataStrength(RimKataSettings settings)
        {
            this.settings = settings;
            foreach (RimKataStrengthRule rule in settings.strengthRules ?? Enumerable.Empty<RimKataStrengthRule>())
            {
                if (rule == null || string.IsNullOrEmpty(rule.defName)) continue;
                RimKataStrengthRule copy = rule.Copy();
                copy.Sanitize();
                rules[copy.Key] = copy;
            }
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (Dialog_RimKataDefSelector.IsCandidate(def, RimKataDefSelectionKind.Armor))
                    AddRow(def, RimKataStrengthSourceKind.Apparel, (int)Dialog_RimKataDefSelector.ClassifyApparel(def));
            }
            var implants = new HashSet<HediffDef>();
            foreach (HediffDef def in DefDatabase<HediffDef>.AllDefsListForReading)
            {
                if (def.countsAsAddedPartOrImplant
                    || def.hediffClass != null && typeof(Hediff_Implant).IsAssignableFrom(def.hediffClass))
                    implants.Add(def);
            }
            foreach (RecipeDef recipe in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (recipe.addsHediff != null && recipe.workerClass != null
                    && typeof(Recipe_InstallImplant).IsAssignableFrom(recipe.workerClass))
                    implants.Add(recipe.addsHediff);
            }
            foreach (HediffDef def in implants) AddRow(def, RimKataStrengthSourceKind.Implant, ImplantCategory);
            rows.Sort((left, right) =>
            {
                int labelOrder = StringComparer.CurrentCultureIgnoreCase.Compare(left.label, right.label);
                return labelOrder != 0 ? labelOrder : StringComparer.Ordinal.Compare(left.rule.Key, right.rule.Key);
            });
            batchPercent = RimKataStrengthRule.SanitizePercent(settings.strengthIncreasePercent);
            batchBuffer = batchPercent.ToString();
            doCloseX = true;
            closeOnAccept = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = false;
            resizeable = true;
        }

        public override Vector2 InitialSize => new Vector2(
            Mathf.Clamp(960f, 760f, Mathf.Max(760f, UI.screenWidth - 80f)), 720f);

        private void AddRow(Def def, RimKataStrengthSourceKind kind, int category)
        {
            string key = kind + ":" + def.defName;
            if (!rules.TryGetValue(key, out RimKataStrengthRule rule))
            {
                rule = new RimKataStrengthRule { sourceKind = kind, defName = def.defName };
                rules.Add(key, rule);
            }
            rows.Add(new RowEditor
            {
                def = def,
                rule = rule,
                category = category,
                label = def.LabelCap.ToString(),
                displayLabel = def.LabelCap + " [" + def.defName + "]",
                buffer = rule.EffectivePercent(settings.strengthIncreasePercent).ToString(),
                displayedDefault = RimKataStrengthRule.SanitizePercent(settings.strengthIncreasePercent)
            });
        }

        public override void DoWindowContents(Rect inRect)
        {
            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), "KRWF_RimKata_StrengthEnhancement".Translate());
                Text.Font = GameFont.Small;
                float y = inRect.y + 42f;
                Label(new Rect(inRect.x, y, 90f, 30f), "KRWF_RimKata_Search".Translate(), TextAnchor.MiddleCenter);
                string nextSearch = Widgets.TextField(new Rect(inRect.x + 90f, y, inRect.width - 90f, 30f), search);
                if (nextSearch != search) { search = nextSearch; FiltersChanged(); }
                y += 36f;
                DrawFilters(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;
                RefreshFiltered();
                DrawBatch(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;
                float contentWidth = inRect.width - 18f;
                infoWidth = Mathf.Max(32f, Text.CalcSize("KRWF_RimKata_EquipmentInfo".Translate()).x + 8f);
                percentWidth = Mathf.Max(FieldWidth, Text.CalcSize("KRWF_RimKata_StrengthIncrease".Translate()).x + 8f);
                allowedWidth = Mathf.Max(32f, Text.CalcSize("KRWF_RimKata_EquipmentAllowed".Translate()).x + 8f);
                overrideTip = "KRWF_RimKata_StrengthOverrideTip".Translate(
                    RimKataStrengthRule.SanitizePercent(settings.strengthIncreasePercent).ToString());
                DrawRow(new Rect(inRect.x, y, contentWidth, RowHeight), null);
                y += RowHeight;
                Widgets.DrawLineHorizontal(inRect.x, y, contentWidth);
                y += 5f;
                Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(0f, inRect.yMax - y - 44f));
                Rect viewRect = new Rect(0f, 0f, contentWidth, Mathf.Max(outRect.height, filtered.Count * RowHeight));
                scrollPosition.y = Mathf.Clamp(scrollPosition.y, 0f, Mathf.Max(0f, viewRect.height - outRect.height));
                Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
                int first = Mathf.Max(0, Mathf.FloorToInt(scrollPosition.y / RowHeight));
                int last = Mathf.Min(filtered.Count, Mathf.CeilToInt((scrollPosition.y + outRect.height) / RowHeight));
                if (filtered.Count == 0)
                    Label(new Rect(0f, 0f, contentWidth, RowHeight), "KRWF_RimKata_NoMatchingDefs".Translate(), TextAnchor.MiddleCenter);
                for (int i = first; i < last; i++)
                {
                    Rect row = new Rect(0f, i * RowHeight, contentWidth, RowHeight);
                    if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                    DrawRow(row, filtered[i]);
                }
                Widgets.EndScrollView();
                DrawButtons(new Rect(inRect.x, inRect.yMax - 30f, inRect.width, 30f));
            }
            finally { Text.Font = previousFont; }
        }

        protected override void LateWindowOnGUI(Rect inRect)
            => GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 36f), inRect.y + 36f));

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose) return;
            settings.strengthRules = rules.Values.Where(rule => rule.enabled || rule.hasOverride)
                .Select(rule => rule.Copy()).OrderBy(rule => rule.Key, StringComparer.Ordinal).ToList();
            settings.SanitizeStrengthRules();
            RimKataMod.CommitListSettings();
        }

        private void DrawRow(Rect row, RowEditor editor)
        {
            Rect info = new Rect(row.xMax - infoWidth, row.y, infoWidth, row.height);
            Rect percent = new Rect(info.x - Gap - percentWidth, row.y, percentWidth, row.height);
            Rect allowed = new Rect(percent.x - Gap - allowedWidth, row.y, allowedWidth, row.height);
            Rect name = new Rect(row.x + 32f, row.y, Mathf.Max(0f, allowed.x - row.x - Gap - 32f), row.height);
            if (editor == null)
            {
                Label(name, "KRWF_RimKata_TargetName".Translate(), TextAnchor.MiddleLeft);
                Label(allowed, "KRWF_RimKata_EquipmentAllowed".Translate(), TextAnchor.MiddleCenter);
                Label(percent, "KRWF_RimKata_StrengthIncrease".Translate(), TextAnchor.MiddleCenter);
                Label(info, "KRWF_RimKata_EquipmentInfo".Translate(), TextAnchor.MiddleCenter);
                return;
            }
            if (editor.def is ThingDef apparel)
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 3f, 24f, 24f), apparel);
            bool enabled = editor.rule.enabled;
            Rect selection = new Rect(name.x, row.y, allowed.center.x + 12f - name.x, row.height);
            Widgets.CheckboxLabeled(selection, editor.displayLabel, ref enabled, paintable: allowedFilter == 0);
            if (enabled != editor.rule.enabled)
            {
                editor.rule.enabled = enabled;
                if (allowedFilter != 0) filteredDirty = true;
            }
            Rect field = new Rect(percent.center.x - FieldWidth * 0.5f, row.y + 2f, FieldWidth, row.height - 4f);
            string control = "TextField" + field.y.ToString("F0") + field.x.ToString("F0");
            float value = editor.rule.EffectivePercent(settings.strengthIncreasePercent);
            if (!editor.rule.hasOverride && GUI.GetNameOfFocusedControl() != control
                && (editor.displayedDefault != value || !float.TryParse(editor.buffer, out float shown) || shown != value))
            {
                editor.buffer = value.ToString();
                editor.displayedDefault = value;
            }
            string previousBuffer = editor.buffer;
            Event input = Event.current;
            bool focusedNumberInput = GUI.GetNameOfFocusedControl() == control
                && (input.type == EventType.KeyDown && !input.control && !input.alt
                    && (char.IsDigit(input.character) || input.character == '.' || input.character == ','
                        || input.character == '+' || input.character == '-' || input.character == 'e' || input.character == 'E')
                    || input.type == EventType.ExecuteCommand && input.commandName == "Paste");
            bool previousChanged = GUI.changed;
            bool fieldChanged;
            try
            {
                GUI.changed = false;
                RimKataSettingsDrawer.DrawFloatField(field, ref value, ref editor.buffer,
                    0f, float.MaxValue, "%", bold: editor.rule.hasOverride);
                fieldChanged = GUI.changed;
            }
            finally { GUI.changed |= previousChanged; }
            if (fieldChanged || focusedNumberInput || previousBuffer != editor.buffer)
            {
                if (editor.buffer.NullOrEmpty())
                {
                    editor.rule.hasOverride = false;
                    editor.rule.percent = 0f;
                }
                else if (float.TryParse(editor.buffer, out float entered)
                    && !float.IsNaN(entered) && !float.IsInfinity(entered) && entered >= 0f)
                {
                    editor.rule.hasOverride = true;
                    editor.rule.percent = entered;
                }
            }
            TooltipHandler.TipRegion(field, overrideTip);
            if (Widgets.ButtonImage(new Rect(info.center.x - 12f, info.center.y - 12f, 24f, 24f), TexButton.Info))
            {
                if (editor.def is ThingDef thing) RimKataPreviewInfoCard.Open(thing);
                else Find.WindowStack.Add(new Dialog_InfoCard(editor.def));
            }
        }

        private void DrawFilters(Rect rect)
        {
            float width = (rect.width - Gap * 2f) / 3f;
            Widgets.Dropdown<Dialog_RimKataStrength, int>(new Rect(rect.x, rect.y, width, rect.height), this,
                dialog => dialog.categoryFilter, dialog => dialog.CategoryMenu(),
                "KRWF_RimKata_ApparelKindFilter".Translate() + ": " + CategoryLabel(categoryFilter));
            Widgets.Dropdown<Dialog_RimKataStrength, int>(new Rect(rect.x + width + Gap, rect.y, width, rect.height), this,
                dialog => dialog.allowedFilter, dialog => dialog.AllowedMenu(),
                "KRWF_RimKata_AllowedStateFilter".Translate() + ": " + AllowedLabel(allowedFilter));
            if (Widgets.ButtonText(new Rect(rect.x + (width + Gap) * 2f, rect.y, width, rect.height), "KRWF_RimKata_FilteredResults".Translate()))
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("KRWF_RimKata_SelectFiltered".Translate(), () => SetFilteredAllowed(true)),
                    new FloatMenuOption("KRWF_RimKata_ClearFiltered".Translate(), () => SetFilteredAllowed(false))
                }));
        }

        private void DrawBatch(Rect rect)
        {
            string text = "KRWF_RimKata_FilteredResults".Translate();
            float width = Mathf.Max(90f, Text.CalcSize(text).x + 24f);
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, width, rect.height), text, active: filtered.Count > 0))
            {
                RefreshFiltered();
                foreach (RowEditor row in filtered)
                {
                    row.rule.hasOverride = true;
                    row.rule.percent = RimKataStrengthRule.SanitizePercent(batchPercent);
                    row.buffer = row.rule.percent.ToString();
                }
                GUI.FocusControl(null);
            }
            float labelX = rect.x + width + Gap;
            string label = "KRWF_RimKata_StrengthIncrease".Translate() + ":";
            float labelWidth = Mathf.Min(Text.CalcSize(label).x + Gap, Mathf.Max(0f, rect.xMax - labelX - FieldWidth));
            Label(new Rect(labelX, rect.y, labelWidth, rect.height), label, TextAnchor.MiddleLeft);
            RimKataSettingsDrawer.DrawFloatField(new Rect(labelX + labelWidth, rect.y + 2f, FieldWidth, rect.height - 4f),
                ref batchPercent, ref batchBuffer, 0f, float.MaxValue, "%");
            batchPercent = RimKataStrengthRule.SanitizePercent(batchPercent);
        }

        private void DrawButtons(Rect rect)
        {
            string reset = "KRWF_RimKata_ResetAllStrength".Translate();
            string filteredReset = "KRWF_RimKata_ResetFilteredStrength".Translate();
            float resetWidth = Mathf.Max(72f, Text.CalcSize(reset).x + 24f);
            float filteredWidth = Mathf.Max(90f, Text.CalcSize(filteredReset).x + 24f);
            float closeWidth = Mathf.Max(90f, Text.CalcSize("Close".Translate()).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize("Confirm".Translate()).x + 28f);
            float total = 2f * Mathf.Max(resetWidth, filteredWidth) + closeWidth + confirmWidth;
            float scale = Mathf.Min(1f, Mathf.Max(0f, rect.width - Gap * 3f) / total);
            resetWidth *= scale; filteredWidth *= scale; closeWidth *= scale; confirmWidth *= scale;
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, resetWidth, rect.height), reset))
                RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                    "KRWF_RimKata_StrengthEnhancement".Translate()), () => Reset(false));
            float center = rect.x + (rect.width - closeWidth - Gap - confirmWidth) * 0.5f;
            if (Widgets.ButtonText(new Rect(center, rect.y, closeWidth, rect.height), "Close".Translate())) Close();
            else if (Widgets.ButtonText(new Rect(center + closeWidth + Gap, rect.y, confirmWidth, rect.height), "Confirm".Translate()))
            {
                commitChangesOnClose = true;
                Close();
            }
            else if (Widgets.ButtonText(new Rect(rect.xMax - filteredWidth, rect.y, filteredWidth, rect.height), filteredReset,
                active: filtered.Count > 0))
                RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                    "KRWF_RimKata_StrengthEnhancement".Translate() + " (" + "KRWF_RimKata_FilteredResults".Translate() + ")"),
                    () => Reset(true));
        }

        private void Reset(bool onlyFiltered)
        {
            RefreshFiltered();
            var defaultKeys = new HashSet<string>(
                RimKataSettings.CreateDefaultStrengthRules().Select(rule => rule.Key), StringComparer.Ordinal);
            IEnumerable<RimKataStrengthRule> targets = onlyFiltered ? filtered.Select(row => row.rule) : rules.Values;
            foreach (RimKataStrengthRule rule in targets)
            {
                rule.enabled = defaultKeys.Contains(rule.Key);
                rule.hasOverride = false;
                rule.percent = 0f;
            }
            foreach (RowEditor row in rows) row.buffer = row.rule.EffectivePercent(settings.strengthIncreasePercent).ToString();
            GUI.FocusControl(null);
            FiltersChanged();
        }

        private void SetFilteredAllowed(bool enabled)
        {
            RefreshFiltered();
            foreach (RowEditor row in filtered) row.rule.enabled = enabled;
            filteredDirty = true;
        }

        private IEnumerable<Widgets.DropdownMenuElement<int>> CategoryMenu()
        {
            for (int i = 0; i <= ImplantCategory; i++)
            {
                int category = i;
                yield return new Widgets.DropdownMenuElement<int>
                {
                    payload = category,
                    option = new FloatMenuOption(CategoryLabel(category), () => { categoryFilter = category; FiltersChanged(); })
                };
            }
        }

        private IEnumerable<Widgets.DropdownMenuElement<int>> AllowedMenu()
        {
            for (int i = 0; i < 3; i++)
            {
                int value = i;
                yield return new Widgets.DropdownMenuElement<int>
                {
                    payload = value,
                    option = new FloatMenuOption(AllowedLabel(value), () => { allowedFilter = value; FiltersChanged(); })
                };
            }
        }

        private static string CategoryLabel(int category) => category == ImplantCategory
            ? "KRWF_RimKata_StrengthImplants".Translate().ToString()
            : Dialog_RimKataDefSelector.ApparelKindLabel((Dialog_RimKataDefSelector.ApparelKindFilter)category);

        private static string AllowedLabel(int value) => (value == 1 ? "KRWF_RimKata_AllowedYes"
            : value == 2 ? "KRWF_RimKata_AllowedNo" : "KRWF_RimKata_FilterAll").Translate();

        private void FiltersChanged()
        {
            filteredDirty = true;
            scrollPosition = Vector2.zero;
        }

        private void RefreshFiltered()
        {
            if (!filteredDirty) return;
            filtered.Clear();
            string query = search.Trim();
            foreach (RowEditor row in rows)
            {
                if (categoryFilter != 0 && row.category != categoryFilter
                    || allowedFilter == 1 && !row.rule.enabled || allowedFilter == 2 && row.rule.enabled) continue;
                if (query.Length > 0 && row.label.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) < 0
                    && row.rule.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                filtered.Add(row);
            }
            filteredDirty = false;
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
