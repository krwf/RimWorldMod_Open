using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataActivationFeatures : Window
    {
        private const float RowHeight = 30f;
        private const float RestrictionButtonHeight = 30f;
        private const float CloseButtonHeight = 30f;
        private const float BottomButtonGap = 8f;
        private const float MinimumButtonWidth = 90f;
        private const float ButtonHorizontalPadding = 28f;
        private const float CheckboxSize = 24f;
        private const float CheckboxLabelGap = 6f;
        private const float VerticalPadding = 18f;
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
            }

            doCloseX = false;
            doCloseButton = false;
            closeOnClickedOutside = true;
            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
            resizeable = false;
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
                float width = contentWidth + Margin * 2f;
                float height = VerticalPadding * 2f
                    + RestrictionButtonHeight
                    + BottomButtonGap + RowHeight
                    + 10f
                    + LabelKeys.Length * RowHeight
                    + 10f
                    + CloseButtonHeight;
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

            float y = inRect.y;
            DrawRestrictionButton(inRect, ref y);
            y += BottomButtonGap;
            DrawNonHumanEquipmentButtons(inRect, ref y);
            y += 10f;
            DrawCheckbox(inRect, ref y, LabelKeys[0], ref enableRimKataA);
            DrawCheckbox(inRect, ref y, LabelKeys[1], ref enableRimKataP);
            DrawCheckbox(inRect, ref y, LabelKeys[2], ref enableRimKataI);
            DrawCheckbox(inRect, ref y, LabelKeys[3], ref enableRimKataG);
            DrawCheckbox(inRect, ref y, LabelKeys[4], ref enableSerumDependency);

            y += 10f;
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

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose || settings == null)
            {
                return;
            }

            bool changed = settings.enableRimKataA != enableRimKataA
                || settings.enableRimKataP != enableRimKataP
                || settings.enableRimKataI != enableRimKataI
                || settings.enableRimKataG != enableRimKataG
                || settings.enableSerumDependency != enableSerumDependency;

            settings.enableRimKataA = enableRimKataA;
            settings.enableRimKataP = enableRimKataP;
            settings.enableRimKataI = enableRimKataI;
            settings.enableRimKataG = enableRimKataG;
            settings.enableSerumDependency = enableSerumDependency;
            if (changed)
            {
                RimKataActivationSettings.Apply();
                mainBuffers?.SyncFrom(settings);
                RimKataMod.CommitListSettings();
            }
        }

        private static void DrawCheckbox(Rect inRect, ref float y, string key, ref bool value)
        {
            Widgets.CheckboxLabeled(new Rect(inRect.x, y, inRect.width, RowHeight), key.Translate(), ref value, paintable: true);
            y += RowHeight;
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
