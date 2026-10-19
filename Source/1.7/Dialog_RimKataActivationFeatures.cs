using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataActivationFeatures : RimKataSettingsWindow
    {
        private const float RowHeight = 30f;
        private const float RestrictionButtonHeight = 30f;
        private const float CloseButtonHeight = 30f;
        private const float BottomButtonGap = 8f;
        private const float MinimumButtonWidth = 90f;
        private const float ButtonHorizontalPadding = 28f;
        private const float CheckboxSize = 24f;
        private const float CheckboxLabelGap = 6f;
        private const float LevelFieldWidth = 104f;
        private const float MinimumWindowWidth = 220f;
        private const float WindowScreenMargin = 80f;
        private readonly RimKataSettings settings;
        private readonly RimKataSettingsUiBuffers mainBuffers;
        private bool commitChangesOnClose;
        private bool enableRimKataA;
        private bool enableRimKataP;
        private bool enableRimKataI;
        private bool enableRimKataG;
        private bool enableSerumDependency;
        private bool showDebugGizmo;
        private Vector2 scrollPosition;
        private readonly List<SkillRow> skillRows = new List<SkillRow>();
        private readonly Dictionary<string, RimKataSkillActivationRule> skillRules =
            new Dictionary<string, RimKataSkillActivationRule>(StringComparer.Ordinal);

        private sealed class SkillRow
        {
            internal string label;
            internal string buffer;
            internal RimKataSkillActivationRule rule;
        }

        private float ContentHeight => RestrictionButtonHeight + BottomButtonGap + RowHeight + 10f
            + (LabelKeys.Length + skillRows.Count + 3) * RowHeight;

        private static readonly string[] LabelKeys =
        {
            "KRWF_RimKata_EnableRimKataA",
            "KRWF_RimKata_EnableRimKataP",
            "KRWF_RimKata_EnableRimKataI",
            "KRWF_RimKata_EnableRimKataG",
            "KRWF_RimKata_EnableSerumDependency"
        };

        public Dialog_RimKataActivationFeatures(RimKataSettings settings, RimKataSettingsUiBuffers buffers)
        {
            this.settings = settings;
            mainBuffers = buffers;
            if (settings != null)
            {
                enableRimKataA = settings.enableRimKataA;
                enableRimKataP = settings.enableRimKataP;
                enableRimKataI = settings.enableRimKataI;
                enableRimKataG = settings.enableRimKataG;
                enableSerumDependency = settings.enableSerumDependency;
                showDebugGizmo = settings.showDebugGizmo;
                settings.SanitizePersonalActivation();
                foreach (RimKataSkillActivationRule rule in settings.skillActivationRules)
                    skillRules[rule.defName] = rule.Copy();
                foreach (SkillDef skill in DefDatabase<SkillDef>.AllDefsListForReading)
                {
                    if (!skillRules.TryGetValue(skill.defName, out RimKataSkillActivationRule rule))
                    {
                        rule = new RimKataSkillActivationRule { defName = skill.defName };
                        skillRules.Add(skill.defName, rule);
                    }
                    skillRows.Add(new SkillRow
                    { label = skill.LabelCap, rule = rule, buffer = rule.requiredLevel.ToString() });
                }
            }

            doCloseX = false;
            doCloseButton = false;
            closeOnClickedOutside = true;
            closeOnAccept = false;
            closeOnCancel = true;
            absorbInputAroundWindow = true;
            resizeable = false;
            draggable = true;
        }

        public override Vector2 InitialSize
        {
            get
            {
                float featureLabelWidth = 0f;
                for (int i = 0; i < LabelKeys.Length; i++)
                {
                    featureLabelWidth = Mathf.Max(
                        featureLabelWidth,
                        Text.CalcSize(LabelKeys[i].Translate()).x);
                }

                float restrictionLabelWidth = Text.CalcSize(
                    "KRWF_RimKata_RemoveRestrictions".Translate()).x;
                float closeWidth = Mathf.Max(
                    MinimumButtonWidth,
                    Text.CalcSize("Close".Translate()).x
                        + ButtonHorizontalPadding);
                float confirmWidth = Mathf.Max(
                    MinimumButtonWidth,
                    Text.CalcSize("Confirm".Translate()).x
                        + ButtonHorizontalPadding);
                float buttonRowWidth = closeWidth
                    + BottomButtonGap
                    + confirmWidth;
                float contentWidth = Mathf.Max(
                    featureLabelWidth + CheckboxSize + CheckboxLabelGap,
                    Mathf.Max(
                        restrictionLabelWidth + ButtonHorizontalPadding,
                        buttonRowWidth));
                contentWidth = Mathf.Max(contentWidth, NonHumanEquipmentRowWidth());
                contentWidth = Mathf.Max(contentWidth,
                    EquipmentButtonWidth("KRWF_RimKata_TraitSelection") + BottomButtonGap
                    + EquipmentButtonWidth("KRWF_RimKata_StatSelection"));
                foreach (SkillRow row in skillRows)
                    contentWidth = Mathf.Max(contentWidth, LevelFieldWidth + BottomButtonGap
                        + Text.CalcSize(row.label).x + CheckboxSize + CheckboxLabelGap);
                contentWidth = Mathf.Max(contentWidth,
                    Text.CalcSize("KRWF_RimKata_ShowDebugGizmo".Translate()).x + CheckboxSize + CheckboxLabelGap);
                float maximumHeight = Mathf.Max(180f, UI.screenHeight - WindowScreenMargin);
                float naturalHeight = ContentHeight + 10f + CloseButtonHeight + Margin * 2f;
                float height = Mathf.Min(naturalHeight, maximumHeight);
                float width = contentWidth + Margin * 2f + (height < naturalHeight ? 18f : 0f);
                float maximumWindowWidth = Mathf.Max(
                    MinimumWindowWidth,
                    UI.screenWidth - WindowScreenMargin);
                return new Vector2(
                    Mathf.Clamp(
                        width,
                        MinimumWindowWidth,
                        maximumWindowWidth),
                    height);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (settings == null)
            {
                Close();
                return;
            }

            Rect outRect = new Rect(inRect.x, inRect.y, inRect.width,
                Mathf.Max(1f, inRect.height - CloseButtonHeight - 10f));
            bool scrolls = ContentHeight > outRect.height;
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(1f, outRect.width - (scrolls ? 18f : 0f)), ContentHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float y = 0f;
            DrawRestrictionButton(viewRect, ref y);
            y += BottomButtonGap;
            DrawNonHumanEquipmentButtons(viewRect, ref y);
            y += 10f;
            DrawCheckbox(viewRect, ref y, LabelKeys[0], ref enableRimKataA);
            DrawCheckbox(viewRect, ref y, LabelKeys[1], ref enableRimKataP);
            DrawCheckbox(viewRect, ref y, LabelKeys[2], ref enableRimKataI);
            DrawCheckbox(viewRect, ref y, LabelKeys[3], ref enableRimKataG);
            DrawCheckbox(viewRect, ref y, LabelKeys[4], ref enableSerumDependency);
            DrawSelectionButtons(viewRect, ref y);
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(viewRect.x, y, viewRect.width, RowHeight), "SkillLevel".Translate().CapitalizeFirst());
            Text.Anchor = previousAnchor;
            y += RowHeight;
            foreach (SkillRow row in skillRows)
            {
                if (y + RowHeight >= scrollPosition.y && y < scrollPosition.y + outRect.height)
                    DrawLevelRow(viewRect, y, row);
                y += RowHeight;
            }
            DrawCheckbox(viewRect, ref y, "KRWF_RimKata_ShowDebugGizmo", ref showDebugGizmo);
            Widgets.EndScrollView();

            y = inRect.yMax - CloseButtonHeight;
            string closeLabel = "Close".Translate();
            string confirmLabel = "Confirm".Translate();
            float closeWidth = Mathf.Max(
                MinimumButtonWidth,
                Text.CalcSize(closeLabel).x + ButtonHorizontalPadding);
            float confirmWidth = Mathf.Max(
                MinimumButtonWidth,
                Text.CalcSize(confirmLabel).x + ButtonHorizontalPadding);
            float availableButtonWidth = Mathf.Max(
                0f,
                inRect.width - BottomButtonGap);
            float naturalButtonWidth = closeWidth + confirmWidth;
            if (naturalButtonWidth > availableButtonWidth
                && naturalButtonWidth > 0f)
            {
                float scale = availableButtonWidth / naturalButtonWidth;
                closeWidth *= scale;
                confirmWidth *= scale;
            }

            float buttonRowWidth = closeWidth
                + BottomButtonGap
                + confirmWidth;
            float buttonX = inRect.x
                + (inRect.width - buttonRowWidth) * 0.5f;
            Rect closeRect = new Rect(
                buttonX,
                y,
                closeWidth,
                CloseButtonHeight);
            if (Widgets.ButtonText(closeRect, closeLabel))
            {
                Close();
                return;
            }

            Rect confirmRect = new Rect(
                closeRect.xMax + BottomButtonGap,
                y,
                confirmWidth,
                CloseButtonHeight);
            if (Widgets.ButtonText(confirmRect, confirmLabel))
            {
                commitChangesOnClose = true;
                Close();
            }
        }

        protected override void OnSettingsClosed()
        {
            if (!commitChangesOnClose || settings == null)
            {
                return;
            }
            commitChangesOnClose = false;

            bool changed = settings.enableRimKataA != enableRimKataA
                || settings.enableRimKataP != enableRimKataP
                || settings.enableRimKataI != enableRimKataI
                || settings.enableRimKataG != enableRimKataG
                || settings.enableSerumDependency != enableSerumDependency;
            bool skillsChanged = settings.skillActivationRules.Count != skillRules.Count
                || settings.skillActivationRules.Any(rule => !skillRules.TryGetValue(rule.defName, out RimKataSkillActivationRule edited)
                    || rule.enabled != edited.enabled || rule.requiredLevel != edited.requiredLevel);
            bool debugChanged = settings.showDebugGizmo != showDebugGizmo;

            settings.enableRimKataA = enableRimKataA;
            settings.enableRimKataP = enableRimKataP;
            settings.enableRimKataI = enableRimKataI;
            settings.enableRimKataG = enableRimKataG;
            settings.enableSerumDependency = enableSerumDependency;
            settings.showDebugGizmo = showDebugGizmo;
            if (skillsChanged)
            {
                settings.skillActivationRules = skillRules.Values.Select(rule => rule.Copy())
                    .OrderBy(rule => rule.defName, StringComparer.Ordinal).ToList();
                settings.SanitizePersonalActivation();
            }
            if (changed || skillsChanged)
                RimKataActivationSettings.Apply(true);
            if (changed || skillsChanged || debugChanged)
            {
                mainBuffers?.SyncFrom(settings);
                RimKataMod.CommitListSettings();
            }
        }

        private static void DrawCheckbox(Rect inRect, ref float y, string key, ref bool value)
        {
            RimKataFeatureWindowUtility.DrawCheckbox(new Rect(inRect.x, y, inRect.width, RowHeight), key.Translate(), ref value);
            y += RowHeight;
        }

        private void DrawSelectionButtons(Rect inRect, ref float y)
        {
            float traitWidth = EquipmentButtonWidth("KRWF_RimKata_TraitSelection");
            float statWidth = EquipmentButtonWidth("KRWF_RimKata_StatSelection");
            float scale = Mathf.Min(1f, Mathf.Max(0f, inRect.width - BottomButtonGap) / (traitWidth + statWidth));
            traitWidth *= scale;
            statWidth *= scale;
            float x = inRect.center.x - (traitWidth + BottomButtonGap + statWidth) * 0.5f;
            if (Widgets.ButtonText(new Rect(x, y, traitWidth, RowHeight), "KRWF_RimKata_TraitSelection".Translate()))
                Find.WindowStack.Add(new Dialog_RimKataTraitSelector(settings));
            if (Widgets.ButtonText(new Rect(x + traitWidth + BottomButtonGap, y, statWidth, RowHeight),
                "KRWF_RimKata_StatSelection".Translate()))
                Find.WindowStack.Add(new Dialog_RimKataStatSelector(settings));
            y += RowHeight;
        }

        private static void DrawLevelRow(Rect inRect, float y, SkillRow row)
        {
            RimKataSettingsDrawer.DrawIntField(new Rect(inRect.x, y + 2f, LevelFieldWidth, RowHeight - 4f),
                ref row.rule.requiredLevel, ref row.buffer, 0, int.MaxValue, string.Empty);
            RimKataFeatureWindowUtility.DrawCheckbox(new Rect(inRect.x + LevelFieldWidth + BottomButtonGap,
                y, inRect.width - LevelFieldWidth - BottomButtonGap, RowHeight), row.label, ref row.rule.enabled);
        }

        private static float EquipmentButtonWidth(string key)
        {
            return Mathf.Max(MinimumButtonWidth, Text.CalcSize(key.Translate()).x + ButtonHorizontalPadding);
        }

        private static float NonHumanEquipmentRowWidth()
        {
            return EquipmentButtonWidth("KRWF_RimKata_WeaponButton")
                + EquipmentButtonWidth("KRWF_RimKata_ApparelButton")
                + Text.CalcSize("KRWF_RimKata_NonHumanEquipmentLabel".Translate()).x
                + BottomButtonGap * 2f;
        }

        private void DrawNonHumanEquipmentButtons(Rect inRect, ref float y)
        {
            string label = "KRWF_RimKata_NonHumanEquipmentLabel".Translate();
            float labelWidth = Text.CalcSize(label).x;
            float weaponWidth = EquipmentButtonWidth("KRWF_RimKata_WeaponButton");
            float apparelWidth = EquipmentButtonWidth("KRWF_RimKata_ApparelButton");
            float available = Mathf.Max(0f, inRect.width - labelWidth - BottomButtonGap * 2f);
            if (weaponWidth + apparelWidth > available)
            {
                float scale = available / (weaponWidth + apparelWidth);
                weaponWidth *= scale;
                apparelWidth *= scale;
            }
            float x = inRect.x + (inRect.width - weaponWidth - apparelWidth - labelWidth
                - BottomButtonGap * 2f) * 0.5f;
            Rect weaponRect = new Rect(x, y, weaponWidth, RowHeight);
            Rect labelRect = new Rect(weaponRect.xMax + BottomButtonGap, y, labelWidth, RowHeight);
            Rect apparelRect = new Rect(labelRect.xMax + BottomButtonGap, y, apparelWidth, RowHeight);
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(labelRect, label);
            Text.Anchor = previousAnchor;
            if (Widgets.ButtonText(weaponRect, "KRWF_RimKata_WeaponButton".Translate()))
            {
                Find.WindowStack.Add(new Dialog_RimKataDefSelector(settings,
                    RimKataDefSelectionKind.Weapon, nonHuman: true));
            }
            if (Widgets.ButtonText(apparelRect, "KRWF_RimKata_ApparelButton".Translate()))
            {
                Find.WindowStack.Add(new Dialog_RimKataDefSelector(settings,
                    RimKataDefSelectionKind.Armor, nonHuman: true));
            }
            y += RowHeight;
        }

        private void DrawRestrictionButton(Rect inRect, ref float y)
        {
            string label = "KRWF_RimKata_RemoveRestrictions".Translate();
            float width = Mathf.Clamp(Text.CalcSize(label).x + 28f, 90f, inRect.width);
            Rect buttonRect = new Rect(inRect.x + (inRect.width - width) * 0.5f, y, width, RestrictionButtonHeight);
            y += RestrictionButtonHeight;
            if (!Widgets.ButtonText(buttonRect, label))
            {
                return;
            }

            if (RimKataMod.EnsureProfilesInitialized())
            {
                Find.WindowStack.Add(new Dialog_RimKataTargetSelector(settings));
            }
        }
    }
}
