using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataTargetSelector : Window
    {
        private enum UsageFilter
        {
            All,
            Enabled,
            Disabled
        }

        private enum CorpseFilter
        {
            All,
            NonCorpses,
            Corpses
        }

        private const float RowHeight = 32f;
        private const float ColumnGap = 8f;
        private const float CheckboxSize = 24f;
        private const float UsageColumnWidth = 80f;
        private const float BottomButtonHeight = 30f;
        private const float BottomButtonGap = 8f;
        private readonly RimKataSettings settings;
        private readonly RimKataProfileStore profiles;
        private readonly List<RimKataTargetEntry> candidates;
        private readonly List<RimKataTargetEntry> filteredCandidates = new List<RimKataTargetEntry>();
        private readonly Dictionary<string, RimKataTargetRule> rules =
            new Dictionary<string, RimKataTargetRule>(StringComparer.Ordinal);
        private readonly Dictionary<string, RimKataStoredProfile> profilesById;
        private Vector2 scrollPosition;
        private string searchText = string.Empty;
        private int categoryFilter = -1;
        private UsageFilter usageFilter;
        private CorpseFilter corpseFilter = CorpseFilter.NonCorpses;
        private bool filteredCandidatesDirty = true;
        private bool commitChangesOnClose;

        public Dialog_RimKataTargetSelector(RimKataSettings settings)
        {
            this.settings = settings;
            profiles = RimKataMod.Profiles;
            profilesById = profiles.Profiles.ToDictionary(profile => profile.Id, profile => profile,
                StringComparer.Ordinal);
            if (settings.targetAccessRules != null)
            {
                foreach (RimKataTargetRule rule in settings.targetAccessRules)
                {
                    if (rule != null && !string.IsNullOrEmpty(rule.key))
                        rules[rule.key] = rule.Copy();
                }
            }

            candidates = RimKataTargetCatalog.Entries
                .OrderBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .ToList();
            foreach (RimKataTargetEntry entry in candidates)
            {
                if (!rules.ContainsKey(entry.Key))
                {
                    rules.Add(entry.Key, new RimKataTargetRule
                    {
                        key = entry.Key,
                        enabled = false,
                        profileId = profiles.Current.Id
                    });
                }
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
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f),
                "KRWF_RimKata_TargetDialogTitle".Translate());
            Text.Font = GameFont.Small;

            float y = inRect.y + 42f;
            string description = "KRWF_RimKata_TargetDialogDescription".Translate();
            float descriptionHeight = Text.CalcHeight(description, inRect.width);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, descriptionHeight), description);
            y += descriptionHeight + 10f;

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
            Widgets.Dropdown<Dialog_RimKataTargetSelector, int>(
                new Rect(inRect.x, y, inRect.width, 30f), this, dialog => dialog.categoryFilter,
                dialog => dialog.CategoryMenuElements(),
                "KRWF_RimKata_TargetCategoryFilter".Translate() + ": " + CategoryLabel(categoryFilter));
            y += 36f;
            float contentWidth = inRect.width - 18f;
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
            Text.Font = previousFont;
        }

        protected override void LateWindowOnGUI(Rect inRect)
        {
            // Profile and checkbox painting must not start a window drag.
            GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 36f), inRect.y + 36f));
        }

        private void DrawBottomButtons(Rect inRect)
        {
            string resetLabel = "KRWF_RimKata_ResetSection".Translate();
            string closeLabel = "Close".Translate();
            string confirmLabel = "Confirm".Translate();
            string filteredProfileLabel = "KRWF_RimKata_FilteredResults".Translate() + " "
                + "KRWF_RimKata_Profile".Translate();
            float resetWidth = Mathf.Max(72f, Text.CalcSize(resetLabel).x + 28f);
            float closeWidth = Mathf.Max(90f, Text.CalcSize(closeLabel).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize(confirmLabel).x + 28f);
            float filteredProfileWidth = Mathf.Max(90f, Text.CalcSize(filteredProfileLabel).x + 28f);
            float totalWidth = closeWidth + confirmWidth + 2f * Mathf.Max(resetWidth, filteredProfileWidth);
            if (totalWidth + BottomButtonGap * 3f > inRect.width)
            {
                float scale = Mathf.Max(0f, inRect.width - BottomButtonGap * 3f) / totalWidth;
                resetWidth *= scale;
                closeWidth *= scale;
                confirmWidth *= scale;
                filteredProfileWidth *= scale;
            }
            float y = inRect.yMax - BottomButtonHeight;
            if (Widgets.ButtonText(new Rect(inRect.x, y, resetWidth, BottomButtonHeight), resetLabel))
            {
                RimKataConfirmationDialog.Show("KRWF_RimKata_ResetSectionConfirmation".Translate(
                    "KRWF_RimKata_TargetDialogTitle".Translate()), ResetSelection);
            }
            float x = inRect.x + (inRect.width - closeWidth - BottomButtonGap - confirmWidth) * 0.5f;
            Rect closeRect = new Rect(x, y, closeWidth, BottomButtonHeight);
            if (Widgets.ButtonText(closeRect, closeLabel))
            {
                Close();
            }
            else if (Widgets.ButtonText(new Rect(closeRect.xMax + BottomButtonGap, closeRect.y,
                confirmWidth, BottomButtonHeight), confirmLabel))
            {
                commitChangesOnClose = true;
                Close();
            }
            else if (Widgets.ButtonText(new Rect(inRect.xMax - filteredProfileWidth, y,
                filteredProfileWidth, BottomButtonHeight), filteredProfileLabel,
                active: filteredCandidates.Count > 0))
            {
                Find.WindowStack.Add(new FloatMenu(profiles.Profiles.Select(profile =>
                    new FloatMenuOption(profile.Name, () => SetFilteredProfile(profile))).ToList()));
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose)
            {
                return;
            }
            // Keep rules for definitions supplied by temporarily disabled mods.
            settings.targetAccessRules = rules.Values.OrderBy(rule => rule.key, StringComparer.Ordinal)
                .Select(rule => rule.Copy()).ToList();
            RimKataMod.CommitListSettings();
        }

        private void DrawRow(Rect row, RimKataTargetEntry entry)
        {
            float profileWidth = Mathf.Clamp(row.width * 0.3f, 180f, 280f);
            Rect profileRect = new Rect(row.xMax - profileWidth, row.y + 2f, profileWidth, row.height - 4f);
            Rect usageRect = new Rect(profileRect.x - ColumnGap - UsageColumnWidth, row.y,
                UsageColumnWidth, row.height);
            Rect nameRect = new Rect(row.x + 4f, row.y, Mathf.Max(0f, usageRect.x - row.x - ColumnGap - 4f), row.height);
            if (entry == null)
            {
                DrawLabel(nameRect, "KRWF_RimKata_TargetName".Translate(), TextAnchor.MiddleLeft);
                DrawLabel(usageRect, "KRWF_RimKata_TargetEnabled".Translate(), TextAnchor.MiddleCenter);
                DrawLabel(profileRect, "KRWF_RimKata_TargetProfile".Translate(), TextAnchor.MiddleCenter);
                return;
            }

            DrawLabel(nameRect, entry.DisplayLabel, TextAnchor.MiddleLeft);
            TooltipHandler.TipRegion(nameRect, entry.DisplayLabel);
            RimKataTargetRule rule = rules[entry.Key];
            bool enabled = rule.enabled;
            Widgets.Checkbox(new Vector2(usageRect.center.x - CheckboxSize * 0.5f,
                usageRect.center.y - CheckboxSize * 0.5f), ref enabled, CheckboxSize,
                paintable: usageFilter == UsageFilter.All);
            if (enabled != rule.enabled)
            {
                rule.enabled = enabled;
                filteredCandidatesDirty = true;
            }

            RimKataStoredProfile profile = ProfileFor(rule);
            Widgets.Dropdown<RimKataTargetRule, RimKataStoredProfile>(profileRect, rule, ProfileFor,
                ProfileMenuElements, profile.Name, dragLabel: profile.Name, paintable: true);
        }

        private void DrawFilterControls(Rect rect)
        {
            float width = (rect.width - ColumnGap * 2f) / 3f;
            Widgets.Dropdown<Dialog_RimKataTargetSelector, CorpseFilter>(
                new Rect(rect.x, rect.y, width, rect.height), this, dialog => dialog.corpseFilter,
                dialog => dialog.CorpseMenuElements(),
                "KRWF_RimKata_TargetCorpseFilter".Translate() + ": " + CorpseLabel(corpseFilter));
            Widgets.Dropdown<Dialog_RimKataTargetSelector, UsageFilter>(
                new Rect(rect.x + width + ColumnGap, rect.y, width, rect.height), this,
                dialog => dialog.usageFilter, dialog => dialog.UsageMenuElements(),
                "KRWF_RimKata_TargetUsageFilter".Translate() + ": " + UsageLabel(usageFilter));
            if (Widgets.ButtonText(new Rect(rect.x + (width + ColumnGap) * 2f, rect.y, width, rect.height),
                "KRWF_RimKata_FilteredResults".Translate()))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("KRWF_RimKata_SelectFiltered".Translate(), () => SetFilteredEnabled(true)),
                    new FloatMenuOption("KRWF_RimKata_ClearFiltered".Translate(), () => SetFilteredEnabled(false))
                }));
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

        private IEnumerable<Widgets.DropdownMenuElement<int>> CategoryMenuElements()
        {
            yield return CategoryMenuElement(-1);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Humanlike);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Mechanoid);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Insect);
            yield return CategoryMenuElement((int)RimKataTargetCategory.Animal);
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

        private IEnumerable<Widgets.DropdownMenuElement<UsageFilter>> UsageMenuElements()
        {
            foreach (UsageFilter value in new[] { UsageFilter.All, UsageFilter.Enabled, UsageFilter.Disabled })
            {
                yield return new Widgets.DropdownMenuElement<UsageFilter>
                {
                    payload = value,
                    option = new FloatMenuOption(UsageLabel(value), () =>
                    {
                        usageFilter = value;
                        FiltersChanged();
                    })
                };
            }
        }

        private RimKataStoredProfile ProfileFor(RimKataTargetRule rule)
        {
            return rule.profileId != null && profilesById.TryGetValue(rule.profileId, out RimKataStoredProfile profile)
                ? profile : profiles.Current;
        }

        private IEnumerable<Widgets.DropdownMenuElement<RimKataStoredProfile>> ProfileMenuElements(RimKataTargetRule rule)
        {
            foreach (RimKataStoredProfile profile in profiles.Profiles)
            {
                yield return new Widgets.DropdownMenuElement<RimKataStoredProfile>
                {
                    payload = profile,
                    option = new FloatMenuOption(profile.Name, () => rule.profileId = profile.Id)
                };
            }
        }

        private void FiltersChanged()
        {
            filteredCandidatesDirty = true;
            scrollPosition = Vector2.zero;
        }

        private void RefreshFilteredCandidates()
        {
            if (!filteredCandidatesDirty)
                return;
            filteredCandidates.Clear();
            string query = searchText.Trim();
            foreach (RimKataTargetEntry entry in candidates)
            {
                if (categoryFilter >= 0 && (int)entry.Category != categoryFilter)
                    continue;
                if ((corpseFilter == CorpseFilter.NonCorpses && entry.IsCorpse)
                    || (corpseFilter == CorpseFilter.Corpses && !entry.IsCorpse))
                    continue;
                bool enabled = rules[entry.Key].enabled;
                if ((usageFilter == UsageFilter.Enabled && !enabled)
                    || (usageFilter == UsageFilter.Disabled && enabled))
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

        private void SetFilteredEnabled(bool enabled)
        {
            RefreshFilteredCandidates();
            foreach (RimKataTargetEntry entry in filteredCandidates)
                rules[entry.Key].enabled = enabled;
            filteredCandidatesDirty = true;
        }

        private void SetFilteredProfile(RimKataStoredProfile profile)
        {
            RefreshFilteredCandidates();
            foreach (RimKataTargetEntry entry in filteredCandidates)
                rules[entry.Key].profileId = profile.Id;
        }

        private void ResetSelection()
        {
            string profileId = profiles.Current.Id;
            foreach (RimKataTargetEntry entry in candidates)
            {
                RimKataTargetRule rule = rules[entry.Key];
                rule.enabled = false;
                rule.profileId = profileId;
            }
            FiltersChanged();
        }

        private static string CategoryLabel(int value)
        {
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

        private static string UsageLabel(UsageFilter value)
        {
            switch (value)
            {
                case UsageFilter.Enabled: return "KRWF_RimKata_TargetEnabled".Translate();
                case UsageFilter.Disabled: return "KRWF_RimKata_TargetDisabled".Translate();
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
