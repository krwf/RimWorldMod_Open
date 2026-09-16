using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataCombatFeatures : Window
    {
        private const float RowHeight = 30f;
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
        private bool commitChangesOnClose;
        private bool secondaryWeaponEnabled;
        private bool singleShotConversionEnabled;
        private bool randomAttackEnabled;
        private bool explosiveInterceptionEnabled;
        private bool movingFireEnabled;
        private bool closeFireEnabled;
        private bool targetRushEnabled;
        private bool responseEnabled;
        private bool rangedDodgeEnabled;
        private bool tumbleEnabled;

        private static readonly string[] LabelKeys =
        {
            "KRWF_RimKata_FeatureSecondaryWeapon",
            "KRWF_RimKata_FeatureSingleShotConversion",
            "KRWF_RimKata_FeatureRandomAttack",
            "KRWF_RimKata_FeatureExplosiveInterception",
            "KRWF_RimKata_FeatureMovingFire",
            "KRWF_RimKata_FeatureCloseFire",
            "KRWF_RimKata_FeatureTargetRush",
            "KRWF_RimKata_FeatureResponse",
            "KRWF_RimKata_FeatureRangedDodge",
            "KRWF_RimKata_FeatureTumble"
        };

        public Dialog_RimKataCombatFeatures(RimKataSettings settings)
        {
            this.settings = settings;
            if (settings != null)
            {
                secondaryWeaponEnabled = settings.secondaryWeaponEnabled;
                singleShotConversionEnabled =
                    settings.singleShotConversionEnabled;
                randomAttackEnabled = settings.randomAttackEnabled;
                explosiveInterceptionEnabled =
                    settings.explosiveInterceptionEnabled;
                movingFireEnabled = settings.movingFireEnabled;
                closeFireEnabled = settings.closeFireEnabled;
                targetRushEnabled = settings.targetRushEnabled;
                responseEnabled = settings.responseEnabled;
                rangedDodgeEnabled = settings.rangedDodgeEnabled;
                tumbleEnabled = settings.tumbleEnabled;
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
                    buttonRowWidth);
                float width = contentWidth + Margin * 2f;
                float height = VerticalPadding * 2f
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
            DrawCheckbox(inRect, ref y, LabelKeys[0], ref secondaryWeaponEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[1], ref singleShotConversionEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[2], ref randomAttackEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[3], ref explosiveInterceptionEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[4], ref movingFireEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[5], ref closeFireEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[6], ref targetRushEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[7], ref responseEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[8], ref rangedDodgeEnabled);
            DrawCheckbox(inRect, ref y, LabelKeys[9], ref tumbleEnabled);

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

            bool changed = settings.secondaryWeaponEnabled
                    != secondaryWeaponEnabled
                || settings.singleShotConversionEnabled
                    != singleShotConversionEnabled
                || settings.randomAttackEnabled != randomAttackEnabled
                || settings.explosiveInterceptionEnabled
                    != explosiveInterceptionEnabled
                || settings.movingFireEnabled != movingFireEnabled
                || settings.closeFireEnabled != closeFireEnabled
                || settings.targetRushEnabled != targetRushEnabled
                || settings.responseEnabled != responseEnabled
                || settings.rangedDodgeEnabled != rangedDodgeEnabled
                || settings.tumbleEnabled != tumbleEnabled;

            settings.secondaryWeaponEnabled = secondaryWeaponEnabled;
            settings.singleShotConversionEnabled =
                singleShotConversionEnabled;
            settings.randomAttackEnabled = randomAttackEnabled;
            settings.explosiveInterceptionEnabled =
                explosiveInterceptionEnabled;
            settings.movingFireEnabled = movingFireEnabled;
            settings.closeFireEnabled = closeFireEnabled;
            settings.targetRushEnabled = targetRushEnabled;
            settings.responseEnabled = responseEnabled;
            settings.rangedDodgeEnabled = rangedDodgeEnabled;
            settings.tumbleEnabled = tumbleEnabled;

            if (changed)
            {
                RimKataMod.ApplyCombatFeatureSettingsChange();
            }
        }

        private static void DrawCheckbox(Rect inRect, ref float y, string key, ref bool value)
        {
            Widgets.CheckboxLabeled(new Rect(inRect.x, y, inRect.width, RowHeight), key.Translate(), ref value, paintable: true);
            y += RowHeight;
        }

    }
}
