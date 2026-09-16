using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public enum RimKataDefSelectionKind
    {
        Weapon,
        Armor
    }

    public sealed class Dialog_RimKataDefSelector : Window
    {
        private enum WeaponKindFilter
        {
            All,
            Melee,
            Ranged
        }

        private enum AllowedStateFilter
        {
            All,
            Yes,
            No
        }

        private enum ApparelKindFilter
        {
            All,
            Headgear,
            Armor,
            Outerwear,
            Underwear,
            Utility,
            Other
        }

        private const float RowHeight = 30f;
        private const float FilterColumnGap = 8f;
        private const float BottomResetAreaHeight = 38f;
        private const float MinimumWindowWidth = 760f;
        private const float WindowScreenMargin = 80f;
        private const float MinimumGripColumnWidth = 132f;
        private const float CheckboxSize = 24f;
        private const float EquipmentColumnGap = 8f;
        private const float ColumnHeaderPadding = 8f;
        private const float ButtonGap = 12f;

        private readonly struct EquipmentColumns
        {
            internal readonly Rect Name;
            internal readonly Rect Selection;
            internal readonly Rect Allowed;
            internal readonly Rect Grip;
            internal readonly Rect Info;

            internal EquipmentColumns(Rect row, float allowedWidth, float gripWidth, float infoWidth)
            {
                Info = new Rect(row.xMax - infoWidth, row.y, infoWidth, row.height);
                Grip = gripWidth > 0f
                    ? new Rect(Info.x - EquipmentColumnGap - gripWidth, row.y, gripWidth, row.height)
                    : Rect.zero;
                float rightColumnX = gripWidth > 0f ? Grip.x : Info.x;
                Allowed = new Rect(rightColumnX - EquipmentColumnGap - allowedWidth,
                    row.y, allowedWidth, row.height);
                float nameX = row.x + 32f;
                Name = new Rect(nameX, row.y, Mathf.Max(0f, Allowed.x - EquipmentColumnGap - nameX), row.height);
                // CheckboxLabeled anchors its checkbox to the right edge.
                Selection = new Rect(nameX, row.y,
                    Mathf.Max(0f, Allowed.center.x + CheckboxSize * 0.5f - nameX), row.height);
            }
        }
        private readonly RimKataSettings settings;
        private readonly RimKataDefSelectionKind kind;
        private readonly bool nonHuman;
        private readonly List<ThingDef> candidates;
        private readonly HashSet<string> candidateDefNames;
        private readonly HashSet<string> selectedDefNames;
        private readonly HashSet<string> twoHandedDefNames;
        private readonly HashSet<string> oneHandedOverrideDefNames;
        private Vector2 scrollPosition;
        private string searchText = string.Empty;
        private WeaponKindFilter weaponKindFilter = WeaponKindFilter.All;
        private ApparelKindFilter apparelKindFilter = ApparelKindFilter.All;
        private AllowedStateFilter allowedStateFilter = AllowedStateFilter.All;
        private int nonHumanCategoryFilter = -1;
        private bool commitChangesOnClose;

        public Dialog_RimKataDefSelector(RimKataSettings settings, RimKataDefSelectionKind kind, bool nonHuman = false)
        {
            this.settings = settings;
            this.kind = kind;
            this.nonHuman = nonHuman;
            doCloseX = true;
            doCloseButton = false;
            closeOnAccept = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = false;
            resizeable = true;

            List<string> source = kind == RimKataDefSelectionKind.Weapon
                ? settings.enabledWeaponDefNames
                : settings.enabledArmorDefNames;
            selectedDefNames = new HashSet<string>(source, StringComparer.Ordinal);
            twoHandedDefNames = new HashSet<string>(settings.twoHandWeaponDefNames, StringComparer.Ordinal);
            oneHandedOverrideDefNames = new HashSet<string>(settings.oneHandWeaponOverrideDefNames, StringComparer.Ordinal);

            candidates = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(def => nonHuman
                    ? RimKataNonHumanEquipmentCatalog.IsCandidate(def, kind)
                    : IsCandidate(def, kind))
                .OrderBy(def => def.LabelCap.ToString(), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(def => def.defName, StringComparer.Ordinal)
                .ToList();
            candidateDefNames = new HashSet<string>(candidates.Select(def => def.defName), StringComparer.Ordinal);
        }

        public override Vector2 InitialSize => new Vector2(CalculateWindowWidth(), 720f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            string titleKey = nonHuman
                ? (kind == RimKataDefSelectionKind.Weapon
                    ? "KRWF_RimKata_NonHumanWeaponDialogTitle" : "KRWF_RimKata_NonHumanArmorDialogTitle")
                : (kind == RimKataDefSelectionKind.Weapon
                    ? "KRWF_RimKata_WeaponDialogTitle" : "KRWF_RimKata_ArmorDialogTitle");
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 36f), titleKey.Translate());
            Text.Font = GameFont.Small;

            float y = inRect.y + 42f;
            DrawCenteredLabel(new Rect(inRect.x, y, 90f, 30f), "KRWF_RimKata_Search".Translate());
            searchText = Widgets.TextField(new Rect(inRect.x + 90f, y, inRect.width - 90f, 30f), searchText ?? string.Empty);
            y += 36f;

            DrawFilterControls(new Rect(inRect.x, y, inRect.width, 30f));
            y += 36f;
            if (nonHuman)
            {
                DrawNonHumanCategoryDropdown(new Rect(inRect.x, y, inRect.width, 30f));
                y += 36f;
            }
            float contentWidth = inRect.width - 18f;
            float allowedWidth = HeaderColumnWidth("KRWF_RimKata_EquipmentAllowed");
            float gripWidth = kind == RimKataDefSelectionKind.Weapon ? GripColumnWidth() : 0f;
            float infoWidth = HeaderColumnWidth("KRWF_RimKata_EquipmentInfo");
            DrawColumnHeaders(new EquipmentColumns(new Rect(inRect.x, y, contentWidth, RowHeight),
                allowedWidth, gripWidth, infoWidth));
            y += RowHeight;
            Widgets.DrawLineHorizontal(inRect.x, y, contentWidth);
            y += 5f;

            List<ThingDef> filtered = FilteredCandidates();
            Rect outRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(0f, inRect.yMax - y - 44f - BottomResetAreaHeight));
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, Mathf.Max(outRect.height, filtered.Count * RowHeight));
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            if (filtered.Count == 0)
            {
                DrawCenteredLabel(new Rect(0f, 0f, viewRect.width, RowHeight), "KRWF_RimKata_NoMatchingDefs".Translate());
            }
            else
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    ThingDef def = filtered[i];
                    Rect row = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);
                    if (i % 2 == 1)
                    {
                        Widgets.DrawLightHighlight(row);
                    }

                    bool selected = selectedDefNames.Contains(def.defName);
                    Rect iconRect = new Rect(row.x + 2f, row.y + 3f, 24f, 24f);
                    Widgets.DefIcon(iconRect, def);
                    EquipmentColumns columns = new EquipmentColumns(row, allowedWidth, gripWidth, infoWidth);
                    Widgets.CheckboxLabeled(columns.Selection, def.LabelCap + " [" + def.defName + "]", ref selected,
                        paintable: allowedStateFilter == AllowedStateFilter.All);
                    if (selected)
                    {
                        selectedDefNames.Add(def.defName);
                    }
                    else
                    {
                        selectedDefNames.Remove(def.defName);
                    }

                    if (kind == RimKataDefSelectionKind.Weapon)
                    {
                        bool twoHanded = EffectiveTwoHanded(def);
                        bool previous = twoHanded;
                        DrawRightAlignedCheckbox(columns.Grip, "KRWF_RimKata_TwoHandedWeapon".Translate(), ref twoHanded,
                            allowedStateFilter == AllowedStateFilter.All);
                        if (twoHanded != previous)
                        {
                            if (twoHanded)
                            {
                                oneHandedOverrideDefNames.Remove(def.defName);
                                twoHandedDefNames.Add(def.defName);
                            }
                            else
                            {
                                twoHandedDefNames.Remove(def.defName);
                                oneHandedOverrideDefNames.Add(def.defName);
                            }
                        }
                    }

                    DrawInfoButton(new Rect(columns.Info.center.x - CheckboxSize * 0.5f,
                        columns.Info.center.y - CheckboxSize * 0.5f, CheckboxSize, CheckboxSize), def);
                }
            }

            Widgets.EndScrollView();
            string resetLabel = "KRWF_RimKata_ResetSection".Translate();
            float resetWidth = Mathf.Max(72f, Text.CalcSize(resetLabel).x + 22f);
            Rect resetRect = new Rect(inRect.x, outRect.yMax + 8f, resetWidth, 30f);
            if (Widgets.ButtonText(resetRect, resetLabel))
            {
                string confirmationKey = kind == RimKataDefSelectionKind.Weapon
                    ? "KRWF_RimKata_ResetWeaponConfirmation"
                    : "KRWF_RimKata_ResetApparelConfirmation";
                RimKataConfirmationDialog.Show(confirmationKey.Translate(), ResetSelection);
            }
            DrawButtons(new Rect(inRect.x, inRect.yMax - 30f, inRect.width, 30f));
        }

        protected override void LateWindowOnGUI(Rect inRect)
        {
            // Keep checkbox painting separate from dragging the window by its title.
            GUI.DragWindow(new Rect(0f, 0f, Mathf.Max(0f, windowRect.width - 36f), inRect.y + 36f));
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose)
            {
                return;
            }
            List<string> target = kind == RimKataDefSelectionKind.Weapon
                ? settings.enabledWeaponDefNames
                : settings.enabledArmorDefNames;
            ApplyScopedSelection(target, selectedDefNames);
            if (kind == RimKataDefSelectionKind.Weapon)
            {
                ApplyScopedSelection(settings.twoHandWeaponDefNames, twoHandedDefNames);
                ApplyScopedSelection(settings.oneHandWeaponOverrideDefNames, oneHandedOverrideDefNames);
            }
            RimKataMod.CommitListSettings();
        }

        public static bool IsCandidate(ThingDef def, RimKataDefSelectionKind kind)
        {
            if (def == null || !def.PlayerAcquirable)
            {
                return false;
            }

            return kind == RimKataDefSelectionKind.Weapon ? def.IsWeapon : def.IsApparel;
        }

        private List<ThingDef> FilteredCandidates()
        {
            string query = (searchText ?? string.Empty).Trim();
            IEnumerable<ThingDef> filtered = candidates;

            if (nonHuman && nonHumanCategoryFilter >= 0)
            {
                RimKataTargetCategory category = (RimKataTargetCategory)nonHumanCategoryFilter;
                filtered = filtered.Where(def => RimKataNonHumanEquipmentCatalog.IsInCategory(def, category));
            }

            if (kind == RimKataDefSelectionKind.Weapon)
            {
                if (weaponKindFilter == WeaponKindFilter.Melee)
                {
                    filtered = filtered.Where(def => def.IsMeleeWeapon);
                }
                else if (weaponKindFilter == WeaponKindFilter.Ranged)
                {
                    filtered = filtered.Where(def => def.IsRangedWeapon);
                }
            }
            else if (apparelKindFilter != ApparelKindFilter.All)
            {
                filtered = filtered.Where(def => ClassifyApparel(def) == apparelKindFilter);
            }

            if (allowedStateFilter == AllowedStateFilter.Yes)
            {
                filtered = filtered.Where(def => selectedDefNames.Contains(def.defName));
            }
            else if (allowedStateFilter == AllowedStateFilter.No)
            {
                filtered = filtered.Where(def => !selectedDefNames.Contains(def.defName));
            }

            if (query.Length > 0)
            {
                filtered = filtered.Where(def => def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || def.LabelCap.ToString().IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
            }

            return filtered.ToList();
        }

        private void ResetSelection()
        {
            IEnumerable<string> defaults = kind == RimKataDefSelectionKind.Weapon
                ? RimKataSettings.DefaultEnabledWeaponDefNames
                : RimKataSettings.DefaultEnabledArmorDefNames;
            selectedDefNames.ExceptWith(candidateDefNames);
            foreach (string defName in defaults)
            {
                if (candidateDefNames.Contains(defName))
                {
                    selectedDefNames.Add(defName);
                }
            }

            scrollPosition = Vector2.zero;
            if (kind == RimKataDefSelectionKind.Weapon)
            {
                twoHandedDefNames.ExceptWith(candidateDefNames);
                oneHandedOverrideDefNames.ExceptWith(candidateDefNames);
            }
        }

        private void ApplyScopedSelection(List<string> target, HashSet<string> edited)
        {
            // Other equipment windows may own selections outside this catalog.
            // Merge only the definitions shown by this window's candidate scope.
            target.RemoveAll(name => candidateDefNames.Contains(name));
            target.AddRange(edited.Where(name => candidateDefNames.Contains(name)));
            target.Sort(StringComparer.Ordinal);
        }

        private void DrawButtons(Rect rect)
        {
            float closeWidth = Mathf.Max(90f, Text.CalcSize("Close".Translate()).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize("Confirm".Translate()).x + 28f);
            if (closeWidth + confirmWidth + ButtonGap > rect.width)
            {
                float scale = Mathf.Max(0f, rect.width - ButtonGap) / (closeWidth + confirmWidth);
                closeWidth *= scale;
                confirmWidth *= scale;
            }
            float x = rect.x + (rect.width - closeWidth - confirmWidth - ButtonGap) * 0.5f;
            if (Widgets.ButtonText(new Rect(x, rect.y, closeWidth, rect.height), "Close".Translate()))
            {
                Close();
                return;
            }
            if (Widgets.ButtonText(new Rect(x + closeWidth + ButtonGap, rect.y, confirmWidth, rect.height),
                "Confirm".Translate()))
            {
                commitChangesOnClose = true;
                Close();
            }
        }

        private bool EffectiveTwoHanded(ThingDef def)
        {
            if (oneHandedOverrideDefNames.Contains(def.defName))
            {
                return false;
            }

            if (twoHandedDefNames.Contains(def.defName))
            {
                return true;
            }

            return RimKataGripUtility.AutoGripTypeFor(def) == RimKataGripType.TwoHand;
        }

        private float CalculateWindowWidth()
        {
            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Small;
            float longestName = Text.CalcSize("KRWF_RimKata_TargetName".Translate()).x;
            for (int i = 0; i < candidates.Count; i++)
            {
                ThingDef def = candidates[i];
                string displayName = def.LabelCap + " [" + def.defName + "]";
                longestName = Mathf.Max(longestName, Text.CalcSize(displayName).x);
            }

            float gripWidth = kind == RimKataDefSelectionKind.Weapon
                ? GripColumnWidth()
                : 0f;
            float desiredRowWidth = 32f
                + longestName
                + EquipmentColumnGap
                + HeaderColumnWidth("KRWF_RimKata_EquipmentAllowed")
                + (gripWidth > 0f ? EquipmentColumnGap + gripWidth : 0f)
                + EquipmentColumnGap
                + HeaderColumnWidth("KRWF_RimKata_EquipmentInfo");
            Text.Font = previousFont;

            float desiredWindowWidth = desiredRowWidth + 18f + 36f;
            float maximumWindowWidth = Mathf.Max(MinimumWindowWidth, UI.screenWidth - WindowScreenMargin);
            return Mathf.Clamp(desiredWindowWidth, MinimumWindowWidth, maximumWindowWidth);
        }

        private void DrawColumnHeaders(EquipmentColumns columns)
        {
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(columns.Name, "KRWF_RimKata_TargetName".Translate());
            Text.Anchor = previousAnchor;
            DrawCenteredLabel(columns.Allowed, "KRWF_RimKata_EquipmentAllowed".Translate());
            if (kind == RimKataDefSelectionKind.Weapon)
                DrawCenteredLabel(columns.Grip, "KRWF_RimKata_TwoHandedWeapon".Translate());
            DrawCenteredLabel(columns.Info, "KRWF_RimKata_EquipmentInfo".Translate());
        }

        private static float HeaderColumnWidth(string key)
        {
            return Mathf.Max(CheckboxSize, Text.CalcSize(key.Translate()).x + ColumnHeaderPadding);
        }

        private static float GripColumnWidth()
        {
            return Mathf.Max(MinimumGripColumnWidth, Text.CalcSize("KRWF_RimKata_TwoHandedWeapon".Translate()).x + CheckboxSize + 10f);
        }

        private static void DrawRightAlignedCheckbox(Rect rect, string label, ref bool value, bool paintable)
        {
            Rect checkboxRect = new Rect(rect.xMax - CheckboxSize, rect.y + (rect.height - CheckboxSize) * 0.5f, CheckboxSize, CheckboxSize);
            Rect labelRect = new Rect(rect.x, rect.y, Mathf.Max(0f, checkboxRect.x - rect.x - 6f), rect.height);

            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(labelRect, label);
            Text.Anchor = previousAnchor;
            Widgets.Checkbox(checkboxRect.position, ref value, CheckboxSize, paintable: paintable);
        }

        private void DrawFilterControls(Rect rect)
        {
            float columnWidth = (rect.width - FilterColumnGap * 2f) / 3f;
            Rect kindRect = new Rect(rect.x, rect.y, columnWidth, rect.height);
            if (kind == RimKataDefSelectionKind.Weapon)
            {
                DrawWeaponKindDropdown(kindRect);
            }
            else
            {
                DrawApparelKindDropdown(kindRect);
            }

            DrawAllowedStateDropdown(new Rect(rect.x + columnWidth + FilterColumnGap, rect.y, columnWidth, rect.height));
            DrawFilteredSelectionDropdown(new Rect(rect.x + (columnWidth + FilterColumnGap) * 2f, rect.y, columnWidth, rect.height));
        }

        private void DrawWeaponKindDropdown(Rect rect)
        {
            string label = "KRWF_RimKata_WeaponKindFilter".Translate() + ": " + WeaponKindLabel(weaponKindFilter);
            Widgets.Dropdown<Dialog_RimKataDefSelector, WeaponKindFilter>(rect, this, dialog => dialog.weaponKindFilter, dialog => dialog.WeaponKindMenuElements(), label);
        }

        private void DrawApparelKindDropdown(Rect rect)
        {
            string label = "KRWF_RimKata_ApparelKindFilter".Translate() + ": " + ApparelKindLabel(apparelKindFilter);
            Widgets.Dropdown<Dialog_RimKataDefSelector, ApparelKindFilter>(rect, this, dialog => dialog.apparelKindFilter, dialog => dialog.ApparelKindMenuElements(), label);
        }

        private void DrawAllowedStateDropdown(Rect rect)
        {
            string label = "KRWF_RimKata_AllowedStateFilter".Translate() + ": " + AllowedStateLabel(allowedStateFilter);
            Widgets.Dropdown<Dialog_RimKataDefSelector, AllowedStateFilter>(rect, this, dialog => dialog.allowedStateFilter, dialog => dialog.AllowedStateMenuElements(), label);
        }

        private void DrawNonHumanCategoryDropdown(Rect rect)
        {
            string label = "KRWF_RimKata_NonHumanEquipmentCategoryFilter".Translate()
                + ": " + NonHumanCategoryLabel(nonHumanCategoryFilter);
            Widgets.Dropdown<Dialog_RimKataDefSelector, int>(rect, this,
                dialog => dialog.nonHumanCategoryFilter, dialog => dialog.NonHumanCategoryMenuElements(), label);
        }

        private IEnumerable<Widgets.DropdownMenuElement<int>> NonHumanCategoryMenuElements()
        {
            yield return NonHumanCategoryMenuElement(-1);
            yield return NonHumanCategoryMenuElement((int)RimKataTargetCategory.Mechanoid);
            yield return NonHumanCategoryMenuElement((int)RimKataTargetCategory.Insect);
            yield return NonHumanCategoryMenuElement((int)RimKataTargetCategory.Animal);
            yield return NonHumanCategoryMenuElement((int)RimKataTargetCategory.Other);
        }

        private Widgets.DropdownMenuElement<int> NonHumanCategoryMenuElement(int value)
        {
            return new Widgets.DropdownMenuElement<int>
            {
                option = new FloatMenuOption(NonHumanCategoryLabel(value), () =>
                {
                    nonHumanCategoryFilter = value;
                    scrollPosition = Vector2.zero;
                }),
                payload = value
            };
        }

        private static string NonHumanCategoryLabel(int value)
        {
            if (value < 0)
            {
                return "KRWF_RimKata_FilterAll".Translate();
            }
            switch ((RimKataTargetCategory)value)
            {
                case RimKataTargetCategory.Mechanoid:
                    return "KRWF_RimKata_TargetMechanoid".Translate();
                case RimKataTargetCategory.Insect:
                    return "KRWF_RimKata_TargetInsect".Translate();
                case RimKataTargetCategory.Animal:
                    return "KRWF_RimKata_TargetAnimal".Translate();
                default:
                    return "KRWF_RimKata_TargetOther".Translate();
            }
        }

        private void DrawFilteredSelectionDropdown(Rect rect)
        {
            if (Widgets.ButtonText(rect, "KRWF_RimKata_FilteredResults".Translate()))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("KRWF_RimKata_SelectFiltered".Translate(), () => SetFilteredSelection(true)),
                    new FloatMenuOption("KRWF_RimKata_ClearFiltered".Translate(), () => SetFilteredSelection(false))
                }));
            }
        }

        private void SetFilteredSelection(bool selected)
        {
            List<ThingDef> filtered = FilteredCandidates();
            foreach (ThingDef def in filtered)
            {
                if (selected)
                {
                    selectedDefNames.Add(def.defName);
                }
                else
                {
                    selectedDefNames.Remove(def.defName);
                }
            }
        }

        private IEnumerable<Widgets.DropdownMenuElement<ApparelKindFilter>> ApparelKindMenuElements()
        {
            yield return ApparelKindMenuElement(ApparelKindFilter.All);
            yield return ApparelKindMenuElement(ApparelKindFilter.Headgear);
            yield return ApparelKindMenuElement(ApparelKindFilter.Armor);
            yield return ApparelKindMenuElement(ApparelKindFilter.Outerwear);
            yield return ApparelKindMenuElement(ApparelKindFilter.Underwear);
            yield return ApparelKindMenuElement(ApparelKindFilter.Utility);
            yield return ApparelKindMenuElement(ApparelKindFilter.Other);
        }

        private Widgets.DropdownMenuElement<ApparelKindFilter> ApparelKindMenuElement(ApparelKindFilter value)
        {
            return new Widgets.DropdownMenuElement<ApparelKindFilter>
            {
                option = new FloatMenuOption(ApparelKindLabel(value), () => apparelKindFilter = value),
                payload = value
            };
        }

        private IEnumerable<Widgets.DropdownMenuElement<WeaponKindFilter>> WeaponKindMenuElements()
        {
            yield return WeaponKindMenuElement(WeaponKindFilter.All);
            yield return WeaponKindMenuElement(WeaponKindFilter.Melee);
            yield return WeaponKindMenuElement(WeaponKindFilter.Ranged);
        }

        private Widgets.DropdownMenuElement<WeaponKindFilter> WeaponKindMenuElement(WeaponKindFilter value)
        {
            return new Widgets.DropdownMenuElement<WeaponKindFilter>
            {
                option = new FloatMenuOption(WeaponKindLabel(value), () => weaponKindFilter = value),
                payload = value
            };
        }

        private IEnumerable<Widgets.DropdownMenuElement<AllowedStateFilter>> AllowedStateMenuElements()
        {
            yield return AllowedStateMenuElement(AllowedStateFilter.All);
            yield return AllowedStateMenuElement(AllowedStateFilter.Yes);
            yield return AllowedStateMenuElement(AllowedStateFilter.No);
        }

        private Widgets.DropdownMenuElement<AllowedStateFilter> AllowedStateMenuElement(AllowedStateFilter value)
        {
            return new Widgets.DropdownMenuElement<AllowedStateFilter>
            {
                option = new FloatMenuOption(AllowedStateLabel(value), () => allowedStateFilter = value),
                payload = value
            };
        }

        private static string WeaponKindLabel(WeaponKindFilter value)
        {
            switch (value)
            {
                case WeaponKindFilter.Melee:
                    return "KRWF_RimKata_WeaponKindMelee".Translate();
                case WeaponKindFilter.Ranged:
                    return "KRWF_RimKata_WeaponKindRanged".Translate();
                default:
                    return "KRWF_RimKata_FilterAll".Translate();
            }
        }

        private static string AllowedStateLabel(AllowedStateFilter value)
        {
            switch (value)
            {
                case AllowedStateFilter.Yes:
                    return "KRWF_RimKata_AllowedYes".Translate();
                case AllowedStateFilter.No:
                    return "KRWF_RimKata_AllowedNo".Translate();
                default:
                    return "KRWF_RimKata_FilterAll".Translate();
            }
        }

        private static string ApparelKindLabel(ApparelKindFilter value)
        {
            switch (value)
            {
                case ApparelKindFilter.Headgear:
                    return "KRWF_RimKata_ApparelKindHeadgear".Translate();
                case ApparelKindFilter.Armor:
                    return "KRWF_RimKata_ApparelKindArmor".Translate();
                case ApparelKindFilter.Outerwear:
                    return "KRWF_RimKata_ApparelKindOuterwear".Translate();
                case ApparelKindFilter.Underwear:
                    return "KRWF_RimKata_ApparelKindUnderwear".Translate();
                case ApparelKindFilter.Utility:
                    return "KRWF_RimKata_ApparelKindUtility".Translate();
                case ApparelKindFilter.Other:
                    return "KRWF_RimKata_ApparelKindOther".Translate();
                default:
                    return "KRWF_RimKata_FilterAll".Translate();
            }
        }

        private static ApparelKindFilter ClassifyApparel(ThingDef def)
        {
            if (def?.apparel == null)
            {
                return ApparelKindFilter.Other;
            }

            List<ApparelLayerDef> layers = def.apparel.layers;
            List<BodyPartGroupDef> bodyPartGroups = def.apparel.bodyPartGroups;
            List<string> tags = def.apparel.tags;
            List<ThingCategoryDef> thingCategories = def.thingCategories;
            List<string> tradeTags = def.tradeTags;

            if (AnyDefNameContains(layers, "belt", "utility", "pack")
                || AnyStringContains(tags, "belt", "utility", "pack"))
            {
                return ApparelKindFilter.Utility;
            }

            if (AnyDefNameContains(layers, "overhead")
                || AnyDefNameContains(bodyPartGroups, "fullhead", "upperhead"))
            {
                return ApparelKindFilter.Headgear;
            }

            if (AnyDefNameContains(layers, "middle", "armor", "armour")
                || AnyStringContains(tags, "armor", "armour", "protective")
                || AnyDefNameContains(thingCategories, "armor", "armour", "protective")
                || AnyStringContains(tradeTags, "armor", "armour", "protective")
                || HasProtectiveArmorStats(def))
            {
                return ApparelKindFilter.Armor;
            }

            if (AnyDefNameContains(layers, "shell", "outer")
                || AnyStringContains(tags, "outerwear"))
            {
                return ApparelKindFilter.Outerwear;
            }

            if (AnyDefNameContains(layers, "onskin", "under")
                || AnyStringContains(tags, "underwear"))
            {
                return ApparelKindFilter.Underwear;
            }

            return ApparelKindFilter.Other;
        }

        private static bool HasProtectiveArmorStats(ThingDef def)
        {
            List<StatModifier> stats = def?.statBases;
            if (stats == null)
            {
                return false;
            }

            for (int i = 0; i < stats.Count; i++)
            {
                StatModifier modifier = stats[i];
                if (modifier != null
                    && modifier.value >= 0.4f
                    && (modifier.stat == StatDefOf.ArmorRating_Sharp
                        || modifier.stat == StatDefOf.ArmorRating_Blunt
                        || modifier.stat == StatDefOf.ArmorRating_Heat))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool AnyDefNameContains<T>(IEnumerable<T> defs, params string[] tokens)
            where T : Def
        {
            return defs != null && defs.Any(def => def != null && ContainsAny(def.defName, tokens));
        }

        private static bool AnyStringContains(IEnumerable<string> values, params string[] tokens)
        {
            return values != null && values.Any(value => ContainsAny(value, tokens));
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            if (value.NullOrEmpty())
            {
                return false;
            }

            for (int i = 0; i < tokens.Length; i++)
            {
                if (value.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void DrawCenteredLabel(Rect rect, string label)
        {
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, label);
            Text.Anchor = previousAnchor;
        }

        private static void DrawInfoButton(Rect rect, ThingDef def)
        {
            if (Widgets.ButtonImage(rect, TexButton.Info, true, null))
            {
                RimKataPreviewInfoCard.Open(def);
            }
        }
    }
}
