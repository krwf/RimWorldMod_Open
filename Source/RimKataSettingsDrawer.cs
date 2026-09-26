using RimWorld;
using System.Linq;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class RimKataSettingsUiBuffers
    {
        public string rangedDodgeChance;
        public string rangedDodgeChanceGrowth;
        public string rangedDodgeChanceMinimum;
        public string meleeResponseChance;
        public string meleeResponseChanceGrowth;
        public string meleeResponseChanceMinimum;
        public string meleeDodgeChance;
        public string meleeDodgeChanceGrowth;
        public string meleeDodgeChanceMinimum;
        public string interceptionChance;
        public string interceptionChanceGrowth;
        public string interceptionChanceMinimum;
        public string interceptionCriticalChance;
        public string interceptionCriticalChanceGrowth;
        public string interceptionCriticalChanceMinimum;
        public string movingAccuracyMultiplier;
        public string movingAccuracyMultiplierGrowth;
        public string movingAccuracyMultiplierMinimum;
        public string armorCooldownReduction;
        public string armorCooldownReductionGrowth;
        public string armorCooldownReductionMinimum;
        public string rangedDodgeDurationTicks;
        public string rangedDodgeDurationGrowthTicks;
        public string rangedDodgeDurationBaseTicks;
        public string proneMissChance;
        public string proneMissChanceGrowth;
        public string proneMissChanceMinimum;
        public string proneHuntingStealthBonus;
        public string proneHuntingStealthBonusGrowth;
        public string proneHuntingStealthBonusMinimum;
        public string customCandidateRange;
        public string responseWeaponDurabilityLossChance;
        public string responseWeaponDurabilityLossAmount;
        public string creepJoinerRimKataGeneChance;
        public string creepJoinerDependencyGeneChance;
        public string aiSecondaryWeaponChance;
        public string responseDisarmChance;
        public string responseDisarmChanceGrowth;
        public string responseDisarmChanceMinimum;
        public string responseCooldownReduction;
        public string responseCooldownReductionGrowth;
        public string responseCooldownReductionMinimum;
        public string serumDodgeMultiplier;
        public string serumDodgeMultiplierGrowth;
        public string serumDodgeMultiplierMinimum;
        public string serumResponseMultiplier;
        public string serumResponseMultiplierGrowth;
        public string serumResponseMultiplierMinimum;
        public string serumInterceptionMultiplier;
        public string serumInterceptionMultiplierGrowth;
        public string serumInterceptionMultiplierMinimum;

        public void SyncFrom(RimKataSettings settings)
        {
            rangedDodgeChance = settings.rangedDodgeChancePercent.ToString();
            rangedDodgeChanceGrowth = settings.rangedDodgeChanceGrowthPerLevelPercent.ToString();
            rangedDodgeChanceMinimum = settings.rangedDodgeChanceMinimumPercent.ToString();
            meleeResponseChance = settings.meleeResponseChancePercent.ToString();
            meleeResponseChanceGrowth = settings.meleeResponseChanceGrowthPerLevelPercent.ToString();
            meleeResponseChanceMinimum = settings.meleeResponseChanceMinimumPercent.ToString();
            meleeDodgeChance = settings.meleeDodgeChancePercent.ToString();
            meleeDodgeChanceGrowth = settings.meleeDodgeChanceGrowthPerLevelPercent.ToString();
            meleeDodgeChanceMinimum = settings.meleeDodgeChanceMinimumPercent.ToString();
            interceptionChance = settings.interceptionChancePercent.ToString();
            interceptionChanceGrowth = settings.interceptionChanceGrowthPerLevelPercent.ToString();
            interceptionChanceMinimum = settings.interceptionChanceMinimumPercent.ToString();
            interceptionCriticalChance = settings.interceptionCriticalChancePercent.ToString();
            interceptionCriticalChanceGrowth = settings.interceptionCriticalChanceGrowthPerLevelPercent.ToString();
            interceptionCriticalChanceMinimum = settings.interceptionCriticalChanceMinimumPercent.ToString();
            movingAccuracyMultiplier = settings.movingAccuracyMultiplierPercent.ToString();
            movingAccuracyMultiplierGrowth = settings.movingAccuracyMultiplierGrowthPerLevelPercent.ToString();
            movingAccuracyMultiplierMinimum = settings.movingAccuracyMultiplierMinimumPercent.ToString();
            armorCooldownReduction = settings.armorCooldownReductionPercent.ToString();
            armorCooldownReductionGrowth = settings.armorCooldownReductionGrowthPerLevelPercent.ToString();
            armorCooldownReductionMinimum = settings.armorCooldownReductionMinimumPercent.ToString();
            rangedDodgeDurationTicks = settings.rangedDodgeDurationTicks.ToString();
            rangedDodgeDurationGrowthTicks = settings.rangedDodgeDurationGrowthPerLevelTicks.ToString();
            rangedDodgeDurationBaseTicks = settings.rangedDodgeDurationBaseTicks.ToString();
            proneMissChance = settings.proneMissChancePercent.ToString();
            proneMissChanceGrowth = settings.proneMissChanceGrowthPerLevelPercent.ToString();
            proneMissChanceMinimum = settings.proneMissChanceMinimumPercent.ToString();
            proneHuntingStealthBonus = settings.proneHuntingStealthBonusPercent.ToString();
            proneHuntingStealthBonusGrowth = settings.proneHuntingStealthBonusGrowthPerLevelPercent.ToString();
            proneHuntingStealthBonusMinimum = settings.proneHuntingStealthBonusMinimumPercent.ToString();
            customCandidateRange = settings.customCandidateRange > 0f
                ? settings.customCandidateRange.ToString("0.##")
                : string.Empty;
            responseWeaponDurabilityLossChance = settings.responseWeaponDurabilityLossChancePercent.ToString();
            responseWeaponDurabilityLossAmount = settings.responseWeaponDurabilityLossAmount.ToString();
            creepJoinerRimKataGeneChance = settings.creepJoinerRimKataGeneChancePercent.ToString();
            creepJoinerDependencyGeneChance = settings.creepJoinerDependencyGeneChancePercent.ToString();
            aiSecondaryWeaponChance = settings.aiSecondaryWeaponChancePercent.ToString();
            responseDisarmChance = settings.responseDisarmChancePercent.ToString();
            responseDisarmChanceGrowth = settings.responseDisarmChanceGrowthPerLevelPercent.ToString();
            responseDisarmChanceMinimum = settings.responseDisarmChanceMinimumPercent.ToString();
            responseCooldownReduction = settings.responseCooldownReductionPercent.ToString();
            responseCooldownReductionGrowth = settings.responseCooldownReductionGrowthPerLevelPercent.ToString();
            responseCooldownReductionMinimum = settings.responseCooldownReductionMinimumPercent.ToString();
            serumDodgeMultiplier = settings.serumDodgeMultiplierPercent.ToString();
            serumDodgeMultiplierGrowth = settings.serumDodgeMultiplierGrowthPerLevelPercent.ToString();
            serumDodgeMultiplierMinimum = settings.serumDodgeMultiplierMinimumPercent.ToString();
            serumResponseMultiplier = settings.serumResponseMultiplierPercent.ToString();
            serumResponseMultiplierGrowth = settings.serumResponseMultiplierGrowthPerLevelPercent.ToString();
            serumResponseMultiplierMinimum = settings.serumResponseMultiplierMinimumPercent.ToString();
            serumInterceptionMultiplier = settings.serumInterceptionMultiplierPercent.ToString();
            serumInterceptionMultiplierGrowth = settings.serumInterceptionMultiplierGrowthPerLevelPercent.ToString();
            serumInterceptionMultiplierMinimum = settings.serumInterceptionMultiplierMinimumPercent.ToString();
        }

    }

    public static class RimKataSettingsDrawer
    {
        private const float MaximumMultiplierPercent = 1000f;
        private const float RowHeight = 30f;
        private const float SectionHeaderHeight = 32f;
        private const float ColumnHeaderHeight = 23f;
        private const float ButtonHeight = 30f;
        private static float FieldWidth => HeaderColumnWidth("KRWF_RimKata_AppliedValue", 86f);
        private static float MinimumFieldWidth => HeaderColumnWidth("KRWF_RimKata_Minimum", 86f);
        private static float FixedColumnWidth => HeaderColumnWidth("KRWF_RimKata_Fixed", 72f);
        private const float ColumnGap = 8f;
        private const float ScrollbarWidth = 18f;
        private const float LabelPadding = 18f;
        private const float RangePresetMinimumWidth = 160f;

        private static float HeaderColumnWidth(string key, float minimumWidth)
        {
            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Tiny;
            float width = Mathf.Max(minimumWidth, Text.CalcSize(key.Translate()).x + 12f);
            Text.Font = previousFont;
            return width;
        }

        public static Vector2 RecommendedWindowSize()
        {
            float labelWidth = MaximumTranslatedLabelWidth();
            bool showMinimum = HasAnyGrowth(RimKataMod.Settings);
            float selectorWidth = RangePresetWidth();
            float standardWidth = labelWidth
                + LabelPadding
                + FieldWidth
                + (showMinimum ? ColumnGap + MinimumFieldWidth : 0f)
                + ColumnGap
                + FixedColumnWidth;
            float rangeWidth = labelWidth
                + LabelPadding
                + FieldWidth
                + ColumnGap
                + selectorWidth;
            float contentWidth = Mathf.Max(standardWidth, rangeWidth, CreepJoinerSettingsWidth())
                + ScrollbarWidth
                + 24f;
            float windowWidth = contentWidth + 72f;
            float windowHeight = Mathf.Min(760f, UI.screenHeight - 80f);
            return new Vector2(
                Mathf.Clamp(windowWidth, 540f, UI.screenWidth - 80f),
                Mathf.Max(520f, windowHeight));
        }

        public static void Draw(
            Rect inRect,
            RimKataSettings settings,
            RimKataSettingsUiBuffers buffers,
            ref Vector2 scrollPosition)
        {
            Rect outRect = inRect.ContractedBy(4f);
            float contentHeight = CalculateContentHeight();
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(1f, outRect.width - ScrollbarWidth), Mathf.Max(contentHeight, outRect.height));

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float y = 0f;

            int loadedWeaponCount = RimKataEquipmentUtility.LoadedSelectionCount(settings.enabledWeaponDefNames, RimKataDefSelectionKind.Weapon);
            string weaponButton = "KRWF_RimKata_EditWeapons".Translate() + " (" + "KRWF_RimKata_SelectedCount".Translate(loadedWeaponCount) + ")";
            const float equipmentButtonGap = 8f;
            float equipmentButtonWidth = (viewRect.width - equipmentButtonGap) * 0.5f;
            if (Widgets.ButtonText(new Rect(0f, y, equipmentButtonWidth, ButtonHeight), weaponButton))
            {
                Find.WindowStack.Add(new Dialog_RimKataDefSelector(settings, RimKataDefSelectionKind.Weapon));
            }

            int loadedArmorCount = RimKataEquipmentUtility.LoadedSelectionCount(settings.enabledArmorDefNames, RimKataDefSelectionKind.Armor);
            string armorButton = "KRWF_RimKata_EditArmor".Translate() + " (" + "KRWF_RimKata_SelectedCount".Translate(loadedArmorCount) + ")";
            if (Widgets.ButtonText(new Rect(equipmentButtonWidth + equipmentButtonGap, y, equipmentButtonWidth, ButtonHeight), armorButton))
            {
                Find.WindowStack.Add(new Dialog_RimKataDefSelector(settings, RimKataDefSelectionKind.Armor));
            }

            y += ButtonHeight + 3f;
            bool previousFriendlyEffects = settings.enableFriendlyPawnEffects;
            bool previousHostileEffects = settings.enableHostilePawnEffects;
            float pawnEffectsWidth = (viewRect.width - ColumnGap) * 0.5f;
            Widgets.CheckboxLabeled(new Rect(0f, y, pawnEffectsWidth, RowHeight), "KRWF_RimKata_EnableFriendlyPawnEffects".Translate(), ref settings.enableFriendlyPawnEffects, paintable: true);
            Widgets.CheckboxLabeled(new Rect(pawnEffectsWidth + ColumnGap, y, pawnEffectsWidth, RowHeight), "KRWF_RimKata_EnableHostilePawnEffects".Translate(), ref settings.enableHostilePawnEffects, paintable: true);
            if (previousFriendlyEffects != settings.enableFriendlyPawnEffects
                || previousHostileEffects != settings.enableHostilePawnEffects)
            {
                RimKataEligibilityCache.RefreshPermissions();
                RimKataColonistBarWeaponCache.RefreshAll();
            }
            y += RowHeight;
            DrawGeneralSettings(viewRect.width, ref y, settings, buffers);
            y += 3f;
            string activationFeaturesLabel = "KRWF_RimKata_ActivationFeatures".Translate();
            float activationFeaturesWidth = Mathf.Min(viewRect.width,
                Mathf.Max(Text.CalcSize(activationFeaturesLabel).x + 24f, 80f));
            if (Widgets.ButtonText(new Rect((viewRect.width - activationFeaturesWidth) * 0.5f,
                    y, activationFeaturesWidth, ButtonHeight), activationFeaturesLabel))
            {
                Find.WindowStack.Add(new Dialog_RimKataActivationFeatures(settings, buffers));
            }
            y += ButtonHeight;
            y += 8f;
            Widgets.DrawLineHorizontal(0f, y, viewRect.width, new Color(1f, 1f, 0f, 0.6f));
            y += 5f;
            TextAnchor previousAnchor = Text.Anchor;
            Color previousColor = GUI.color;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.yellow;
            Widgets.Label(new Rect(0f, y, viewRect.width, SectionHeaderHeight), "KRWF_RimKata_ProfileSettingsHeader".Translate());
            GUI.color = previousColor;
            Text.Anchor = previousAnchor;
            y += SectionHeaderHeight;
            string combatFeaturesLabel = "KRWF_RimKata_CombatFeatures".Translate();
            string modFeaturesLabel = "KRWF_RimKata_ModFeatures".Translate();
            float combatFeaturesWidth = Mathf.Max(Text.CalcSize(combatFeaturesLabel).x + 24f, 80f);
            float modFeaturesWidth = Mathf.Max(Text.CalcSize(modFeaturesLabel).x + 24f, 80f);
            float featuresWidth = combatFeaturesWidth + ColumnGap + modFeaturesWidth;
            if (featuresWidth > viewRect.width)
            {
                float scale = Mathf.Max(0f, viewRect.width - ColumnGap) / (combatFeaturesWidth + modFeaturesWidth);
                combatFeaturesWidth *= scale;
                modFeaturesWidth *= scale;
                featuresWidth = combatFeaturesWidth + ColumnGap + modFeaturesWidth;
            }
            float featuresX = (viewRect.width - featuresWidth) * 0.5f;
            if (Widgets.ButtonText(new Rect(featuresX, y, combatFeaturesWidth, ButtonHeight), combatFeaturesLabel))
            {
                Find.WindowStack.Add(new Dialog_RimKataCombatFeatures(settings));
            }
            if (Widgets.ButtonText(new Rect(featuresX + combatFeaturesWidth + ColumnGap, y, modFeaturesWidth, ButtonHeight), modFeaturesLabel))
            {
                Find.WindowStack.Add(new Dialog_RimKataModFeatures(settings, buffers));
            }
            y += RowHeight;
            DrawCandidateRangeRow(viewRect.width, ref y, settings, buffers);
            y += 8f;
            Widgets.DrawLineHorizontal(0f, y, viewRect.width);
            y += 5f;

            bool showMinimum = HasAnyGrowth(settings);
            DrawColumnHeaders(viewRect.width, ref y, showMinimum);

            Widgets.DrawLineHorizontal(0f, y, viewRect.width);
            y += 5f;

            DrawSectionHeader(viewRect.width, ref y, "KRWF_RimKata_ChanceHeader", delegate
            {
                ResetCombat(settings);
                buffers.SyncFrom(settings);
            });
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_MeleeResponseChance", SkillDefOf.Melee,
                ref settings.meleeResponseChanceFixed, ref settings.meleeResponseChancePercent, ref buffers.meleeResponseChance,
                ref settings.meleeResponseChanceGrowthPerLevelPercent, ref buffers.meleeResponseChanceGrowth,
                ref settings.meleeResponseChanceMinimumPercent, ref buffers.meleeResponseChanceMinimum,
                float.MaxValue, float.MaxValue, showMinimum, float.MaxValue);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_ResponseDisarmChance", SkillDefOf.Melee,
                ref settings.responseDisarmChanceFixed, ref settings.responseDisarmChancePercent, ref buffers.responseDisarmChance,
                ref settings.responseDisarmChanceGrowthPerLevelPercent, ref buffers.responseDisarmChanceGrowth,
                ref settings.responseDisarmChanceMinimumPercent, ref buffers.responseDisarmChanceMinimum,
                100f, 100f, showMinimum);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_MeleeDodgeChance", SkillDefOf.Melee,
                ref settings.meleeDodgeChanceFixed, ref settings.meleeDodgeChancePercent, ref buffers.meleeDodgeChance,
                ref settings.meleeDodgeChanceGrowthPerLevelPercent, ref buffers.meleeDodgeChanceGrowth,
                ref settings.meleeDodgeChanceMinimumPercent, ref buffers.meleeDodgeChanceMinimum,
                float.MaxValue, float.MaxValue, showMinimum, float.MaxValue);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_RangedDodgeChance", SkillDefOf.Melee,
                ref settings.rangedDodgeChanceFixed, ref settings.rangedDodgeChancePercent, ref buffers.rangedDodgeChance,
                ref settings.rangedDodgeChanceGrowthPerLevelPercent, ref buffers.rangedDodgeChanceGrowth,
                ref settings.rangedDodgeChanceMinimumPercent, ref buffers.rangedDodgeChanceMinimum,
                100f, 100f, showMinimum);
            DrawDurationRow(viewRect.width, ref y, settings, buffers, showMinimum);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_ProneMissChance", SkillDefOf.Shooting,
                ref settings.proneMissChanceFixed, ref settings.proneMissChancePercent, ref buffers.proneMissChance,
                ref settings.proneMissChanceGrowthPerLevelPercent, ref buffers.proneMissChanceGrowth,
                ref settings.proneMissChanceMinimumPercent, ref buffers.proneMissChanceMinimum,
                100f, 100f, showMinimum, 100f);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_ProneHuntingStealthBonus", SkillDefOf.Shooting,
                ref settings.proneHuntingStealthBonusFixed, ref settings.proneHuntingStealthBonusPercent, ref buffers.proneHuntingStealthBonus,
                ref settings.proneHuntingStealthBonusGrowthPerLevelPercent, ref buffers.proneHuntingStealthBonusGrowth,
                ref settings.proneHuntingStealthBonusMinimumPercent, ref buffers.proneHuntingStealthBonusMinimum,
                100f, 100f, showMinimum, 100f);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_InterceptionChance", SkillDefOf.Shooting,
                ref settings.interceptionChanceFixed, ref settings.interceptionChancePercent, ref buffers.interceptionChance,
                ref settings.interceptionChanceGrowthPerLevelPercent, ref buffers.interceptionChanceGrowth,
                ref settings.interceptionChanceMinimumPercent, ref buffers.interceptionChanceMinimum,
                float.MaxValue, float.MaxValue, showMinimum, float.MaxValue);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_InterceptionCriticalChance", SkillDefOf.Shooting,
                ref settings.interceptionCriticalChanceFixed, ref settings.interceptionCriticalChancePercent, ref buffers.interceptionCriticalChance,
                ref settings.interceptionCriticalChanceGrowthPerLevelPercent, ref buffers.interceptionCriticalChanceGrowth,
                ref settings.interceptionCriticalChanceMinimumPercent, ref buffers.interceptionCriticalChanceMinimum,
                100f, 100f, showMinimum);
            FinishSection(viewRect.width, ref y);

            DrawSectionHeader(viewRect.width, ref y, "KRWF_RimKata_TimingHeader", delegate
            {
                ResetModifiers(settings);
                buffers.SyncFrom(settings);
            });
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_MovingAccuracyMultiplier", SkillDefOf.Shooting,
                ref settings.movingAccuracyMultiplierFixed, ref settings.movingAccuracyMultiplierPercent, ref buffers.movingAccuracyMultiplier,
                ref settings.movingAccuracyMultiplierGrowthPerLevelPercent, ref buffers.movingAccuracyMultiplierGrowth,
                ref settings.movingAccuracyMultiplierMinimumPercent, ref buffers.movingAccuracyMultiplierMinimum,
                MaximumMultiplierPercent, MaximumMultiplierPercent, showMinimum);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_ArmorCooldownReduction", SkillDefOf.Shooting,
                ref settings.armorCooldownReductionFixed, ref settings.armorCooldownReductionPercent, ref buffers.armorCooldownReduction,
                ref settings.armorCooldownReductionGrowthPerLevelPercent, ref buffers.armorCooldownReductionGrowth,
                ref settings.armorCooldownReductionMinimumPercent, ref buffers.armorCooldownReductionMinimum,
                100f, 100f, showMinimum);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_ResponseCooldownReduction", SkillDefOf.Melee,
                ref settings.responseCooldownReductionFixed, ref settings.responseCooldownReductionPercent, ref buffers.responseCooldownReduction,
                ref settings.responseCooldownReductionGrowthPerLevelPercent, ref buffers.responseCooldownReductionGrowth,
                ref settings.responseCooldownReductionMinimumPercent, ref buffers.responseCooldownReductionMinimum,
                100f, 100f, showMinimum);
            FinishSection(viewRect.width, ref y);

            DrawSectionHeader(viewRect.width, ref y, "KRWF_RimKata_SerumHeader", delegate
            {
                ResetSerum(settings);
                buffers.SyncFrom(settings);
            });
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_SerumResponseMultiplier", SkillDefOf.Melee,
                ref settings.serumResponseMultiplierFixed, ref settings.serumResponseMultiplierPercent, ref buffers.serumResponseMultiplier,
                ref settings.serumResponseMultiplierGrowthPerLevelPercent, ref buffers.serumResponseMultiplierGrowth,
                ref settings.serumResponseMultiplierMinimumPercent, ref buffers.serumResponseMultiplierMinimum,
                MaximumMultiplierPercent, MaximumMultiplierPercent, showMinimum);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_SerumDodgeMultiplier", SkillDefOf.Melee,
                ref settings.serumDodgeMultiplierFixed, ref settings.serumDodgeMultiplierPercent, ref buffers.serumDodgeMultiplier,
                ref settings.serumDodgeMultiplierGrowthPerLevelPercent, ref buffers.serumDodgeMultiplierGrowth,
                ref settings.serumDodgeMultiplierMinimumPercent, ref buffers.serumDodgeMultiplierMinimum,
                MaximumMultiplierPercent, MaximumMultiplierPercent, showMinimum);
            DrawFloatRow(viewRect.width, ref y, "KRWF_RimKata_SerumInterceptionMultiplier", SkillDefOf.Shooting,
                ref settings.serumInterceptionMultiplierFixed, ref settings.serumInterceptionMultiplierPercent, ref buffers.serumInterceptionMultiplier,
                ref settings.serumInterceptionMultiplierGrowthPerLevelPercent, ref buffers.serumInterceptionMultiplierGrowth,
                ref settings.serumInterceptionMultiplierMinimumPercent, ref buffers.serumInterceptionMultiplierMinimum,
                MaximumMultiplierPercent, MaximumMultiplierPercent, showMinimum);
            FinishSection(viewRect.width, ref y);

            const float bottomButtonGap = 8f;
            float bottomButtonWidth = (viewRect.width - bottomButtonGap) * 0.5f;
            if (Widgets.ButtonText(new Rect(0f, y, bottomButtonWidth, ButtonHeight), "KRWF_RimKata_ResetSettings".Translate()))
            {
                string profileName = RimKataMod.Profiles?.Current?.Name
                    ?? "KRWF_RimKata_Profile".Translate();
                RimKataConfirmationDialog.Show(
                    "KRWF_RimKata_ResetProfileConfirmation".Translate(profileName),
                    delegate
                    {
                        settings.ResetNumericDefaults();
                        buffers.SyncFrom(settings);
                        RimKataMod.ApplyCombatFeatureSettingsChange();
                    });
            }
            string profileButtonLabel = RimKataMod.Profiles?.Current?.Name
                ?? "KRWF_RimKata_Profile".Translate();
            if (Widgets.ButtonText(
                new Rect(bottomButtonWidth + bottomButtonGap, y, bottomButtonWidth, ButtonHeight),
                profileButtonLabel))
            {
                RimKataProfileMenu.Open(settings, buffers);
            }

            Widgets.EndScrollView();
        }

        private static void DrawGeneralSettings(
            float width,
            ref float y,
            RimKataSettings settings,
            RimKataSettingsUiBuffers buffers)
        {
            DrawCreepJoinerGeneRows(width, ref y, settings, buffers);
            DrawSimpleFloatRow(
                width,
                ref y,
                "KRWF_RimKata_AiSecondaryWeaponChance",
                ref settings.aiSecondaryWeaponChancePercent,
                ref buffers.aiSecondaryWeaponChance,
                0f,
                100f,
                "%");
        }

        private static void DrawCreepJoinerGeneRows(
            float width,
            ref float y,
            RimKataSettings settings,
            RimKataSettingsUiBuffers buffers)
        {
            float previousRimKataChance = settings.creepJoinerRimKataGeneChancePercent;
            float previousDependencyChance = settings.creepJoinerDependencyGeneChancePercent;
            settings.SanitizeCreepJoinerGeneChances();
            if (previousRimKataChance != settings.creepJoinerRimKataGeneChancePercent)
                buffers.creepJoinerRimKataGeneChance = settings.creepJoinerRimKataGeneChancePercent.ToString();
            if (previousDependencyChance != settings.creepJoinerDependencyGeneChancePercent)
                buffers.creepJoinerDependencyGeneChance = settings.creepJoinerDependencyGeneChancePercent.ToString();
            Rect rimKataRow = new Rect(0f, y, width, RowHeight);
            Rect dependencyRow = new Rect(0f, y + RowHeight, width, RowHeight);
            DrawCreepJoinerChanceField(rimKataRow,
                ref settings.creepJoinerRimKataGeneChancePercent, ref buffers.creepJoinerRimKataGeneChance,
                settings.creepJoinerDependencyGeneChancePercent, settings.enableRimKataG);
            DrawCreepJoinerChanceField(dependencyRow,
                ref settings.creepJoinerDependencyGeneChancePercent, ref buffers.creepJoinerDependencyGeneChance,
                settings.creepJoinerRimKataGeneChancePercent, settings.enableSerumDependency);

            float remaining = Mathf.Max(0f, 100f - settings.creepJoinerRimKataGeneChancePercent - settings.creepJoinerDependencyGeneChancePercent);
            DrawCreepJoinerChanceLabels(rimKataRow,
                "KRWF_RimKata_CreepJoinerRimKataGeneChance".Translate(),
                "KRWF_RimKata_CreepJoinerNoGeneChance".Translate(remaining.ToString("0.##") + "%"));
            DrawCreepJoinerChanceLabels(dependencyRow,
                "KRWF_RimKata_CreepJoinerDependencyGeneChance".Translate(),
                "KRWF_RimKata_CreepJoinerGeneChanceLimit".Translate());
            y += RowHeight * 2f;
        }

        private static void DrawCreepJoinerChanceField(Rect row, ref float value, ref string buffer, float otherChance, bool enabled)
        {
            float maximum = enabled ? Mathf.Max(0f, 100f - otherChance) : 0f;
            DrawFloatField(new Rect(row.x, row.y + 2f, FieldWidth, row.height - 4f),
                ref value, ref buffer, 0f, maximum, "%");
            float clamped = Mathf.Clamp(value, 0f, maximum);
            if (value != clamped
                || (float.TryParse(buffer, out float entered) && (entered < 0f || entered > maximum)))
            {
                value = clamped;
                buffer = value.ToString();
            }
        }

        private static void DrawCreepJoinerChanceLabels(Rect row, string label, string hint)
        {
            float hintWidth = Text.CalcSize(hint).x;
            Rect hintRect = new Rect(row.xMax - hintWidth, row.y, hintWidth, row.height);
            float labelX = row.x + FieldWidth + ColumnGap;
            DrawRowLabel(new Rect(labelX, row.y, Mathf.Max(1f, hintRect.x - labelX - LabelPadding), row.height), label);
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(hintRect, hint);
            Text.Anchor = previousAnchor;
        }

        private static float CreepJoinerSettingsWidth()
        {
            float rimKataWidth = Text.CalcSize("KRWF_RimKata_CreepJoinerRimKataGeneChance".Translate()).x
                + Text.CalcSize("KRWF_RimKata_CreepJoinerNoGeneChance".Translate("100.00%")).x;
            float dependencyWidth = Text.CalcSize("KRWF_RimKata_CreepJoinerDependencyGeneChance".Translate()).x
                + Text.CalcSize("KRWF_RimKata_CreepJoinerGeneChanceLimit".Translate()).x;
            return FieldWidth + ColumnGap + LabelPadding + Mathf.Max(rimKataWidth, dependencyWidth);
        }

        private static void DrawCandidateRangeRow(
            float width,
            ref float y,
            RimKataSettings settings,
            RimKataSettingsUiBuffers buffers)
        {
            if (settings.candidateRangeMode == RimKataCandidateRangeMode.Custom
                && settings.customCandidateRange <= 0f)
            {
                settings.customCandidateRange = CustomRangeSeed();
                buffers.customCandidateRange = settings.customCandidateRange.ToString("0.##");
            }

            Rect row = new Rect(0f, y, width, RowHeight);
            Rect fieldRect = new Rect(row.x, row.y + 2f, FieldWidth, row.height - 4f);
            float selectorWidth = RangePresetWidth();
            Rect presetRect = new Rect(row.xMax - selectorWidth, row.y + 2f, selectorWidth, row.height - 4f);
            Rect labelRect = new Rect(fieldRect.xMax + ColumnGap, row.y, Mathf.Max(1f, presetRect.x - fieldRect.xMax - ColumnGap - LabelPadding), row.height);

            float displayedRange = settings.candidateRangeMode == RimKataCandidateRangeMode.Custom
                ? (settings.customCandidateRange > 0f
                    ? settings.customCandidateRange
                    : RimKataRangeUtility.PresetRange(RimKataCandidateRangeMode.Short))
                : (settings.candidateRangeMode == RimKataCandidateRangeMode.Unlimited
                    ? 0f
                    : RimKataRangeUtility.PresetRange(settings.candidateRangeMode));
            bool previousEnabled = GUI.enabled;
            GUI.enabled = settings.candidateRangeMode == RimKataCandidateRangeMode.Custom;
            if (settings.candidateRangeMode == RimKataCandidateRangeMode.Unlimited)
            {
                TextAnchor previousAnchor = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(fieldRect, "—");
                Text.Anchor = previousAnchor;
            }
            else
            {
                string displayedRangeBuffer = displayedRange.ToString("0.##");
                if (settings.candidateRangeMode == RimKataCandidateRangeMode.Custom)
                {
                    DrawFloatField(
                        fieldRect,
                        ref displayedRange,
                        ref buffers.customCandidateRange,
                        RimKataSettings.MinimumCustomCandidateRange,
                        RimKataSettings.MaximumCustomCandidateRange,
                        "KRWF_RimKata_CellUnit".Translate());
                }
                else
                {
                    GUI.enabled = true;
                    Widgets.DrawBoxSolid(fieldRect.ContractedBy(1f), Widgets.MenuSectionBGFillColor);
                    TextAnchor previousAnchor = Text.Anchor;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(fieldRect, displayedRangeBuffer + " " + "KRWF_RimKata_CellUnit".Translate());
                    Text.Anchor = previousAnchor;
                }
            }
            if (settings.candidateRangeMode == RimKataCandidateRangeMode.Custom)
            {
                settings.customCandidateRange = displayedRange;
            }

            GUI.enabled = previousEnabled;
            DrawRowLabel(labelRect, "KRWF_RimKata_MaximumCandidateRange".Translate());

            if (Widgets.ButtonText(presetRect, CandidateRangeModeLabel(settings.candidateRangeMode)))
            {
                Find.WindowStack.Add(new FloatMenu(
                    System.Enum.GetValues(typeof(RimKataCandidateRangeMode))
                        .Cast<RimKataCandidateRangeMode>()
                        .Select(mode => new FloatMenuOption(
                            CandidateRangeModeLabel(mode),
                            delegate
                            {
                                settings.candidateRangeMode = mode;
                                if (mode == RimKataCandidateRangeMode.Custom && settings.customCandidateRange <= 0f)
                                {
                                    settings.customCandidateRange = CustomRangeSeed();
                                }

                                buffers.customCandidateRange = (settings.customCandidateRange > 0f
                                    ? settings.customCandidateRange
                                    : RimKataRangeUtility.PresetRange(RimKataCandidateRangeMode.Short)).ToString("0.##");
                            }))
                        .ToList()));
            }

            y += RowHeight;
        }

        private static string CandidateRangeModeLabel(RimKataCandidateRangeMode mode)
        {
            bool showDetectedDistance = RimKataRangeUtility.RuntimeBandsAvailable;
            switch (mode)
            {
                case RimKataCandidateRangeMode.Medium:
                    return showDetectedDistance
                        ? "KRWF_RimKata_RangePresetMedium".Translate(RimKataRangeUtility.PresetRange(mode).ToString("0.##"))
                        : "KRWF_RimKata_RangePresetMediumFallback".Translate();
                case RimKataCandidateRangeMode.Long:
                    return showDetectedDistance
                        ? "KRWF_RimKata_RangePresetLong".Translate(RimKataRangeUtility.PresetRange(mode).ToString("0.##"))
                        : "KRWF_RimKata_RangePresetLongFallback".Translate();
                case RimKataCandidateRangeMode.Unlimited:
                    return "KRWF_RimKata_RangePresetUnlimited".Translate();
                case RimKataCandidateRangeMode.Custom:
                    return "KRWF_RimKata_RangePresetCustom".Translate();
                default:
                    return showDetectedDistance
                        ? "KRWF_RimKata_RangePresetShort".Translate(RimKataRangeUtility.PresetRange(mode).ToString("0.##"))
                        : "KRWF_RimKata_RangePresetShortFallback".Translate();
            }
        }

        private static float CustomRangeSeed()
        {
            return Mathf.Clamp(
                RimKataRangeUtility.PresetRange(RimKataCandidateRangeMode.Short),
                RimKataSettings.MinimumCustomCandidateRange,
                RimKataSettings.MaximumCustomCandidateRange);
        }

        private static float RangePresetWidth()
        {
            float width = RangePresetMinimumWidth;
            foreach (RimKataCandidateRangeMode mode in System.Enum.GetValues(typeof(RimKataCandidateRangeMode)))
            {
                width = Mathf.Max(width, Text.CalcSize(CandidateRangeModeLabel(mode)).x + 22f);
            }

            return width;
        }

        private static void DrawSectionHeader(float width, ref float y, string translationKey, System.Action reset)
        {
            Rect row = new Rect(0f, y, width, SectionHeaderHeight);
            TaggedString sectionLabel = translationKey.Translate();
            string resetLabel = "KRWF_RimKata_ResetSection".Translate();
            float resetWidth = Mathf.Max(72f, Text.CalcSize(resetLabel).x + 22f);
            Rect buttonRect = new Rect(row.xMax - resetWidth, row.y + 2f, resetWidth, row.height - 4f);
            Rect labelRect = new Rect(row.x, row.y, Mathf.Max(1f, buttonRect.x - row.x - 8f), row.height);

            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(labelRect, sectionLabel);
            Text.Font = GameFont.Small;
            Text.Anchor = previousAnchor;
            if (Widgets.ButtonText(buttonRect, resetLabel))
            {
                RimKataConfirmationDialog.Show(
                    "KRWF_RimKata_ResetSectionConfirmation".Translate(sectionLabel),
                    reset);
            }

            Text.Font = previousFont;
            y += SectionHeaderHeight;
        }

        private static void DrawColumnHeaders(float width, ref float y, bool showMinimum)
        {
            SplitRow(new Rect(0f, y, width, ColumnHeaderHeight), showMinimum, out _, out Rect valueRect, out Rect minimumRect, out Rect fixedRect);
            TextAnchor previousAnchor = Text.Anchor;
            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(valueRect, "KRWF_RimKata_AppliedValue".Translate());
            if (showMinimum)
            {
                Widgets.Label(minimumRect, "KRWF_RimKata_Minimum".Translate());
            }

            Widgets.Label(fixedRect, "KRWF_RimKata_Fixed".Translate());
            Text.Anchor = previousAnchor;
            Text.Font = previousFont;
            y += ColumnHeaderHeight;
        }

        private static void DrawDurationRow(
            float width,
            ref float y,
            RimKataSettings settings,
            RimKataSettingsUiBuffers buffers,
            bool showMinimum)
        {
            Rect row = new Rect(0f, y, width, RowHeight);
            SplitRow(row, showMinimum, out Rect labelRect, out Rect valueRect, out Rect minimumRect, out Rect fixedRect);
            string label = "KRWF_RimKata_RangedDodgeDuration".Translate();
            if (!settings.rangedDodgeDurationFixed)
            {
                label = "KRWF_RimKata_DurationGrowthLabel".Translate(label, SkillDefOf.Melee.LabelCap);
            }

            DrawRowLabel(labelRect, label);
            if (settings.rangedDodgeDurationFixed)
            {
                DrawIntField(valueRect, ref settings.rangedDodgeDurationTicks, ref buffers.rangedDodgeDurationTicks,
                    RimKataSettings.MinimumRangedDodgeDurationTicks, RimKataSettings.MaximumRangedDodgeDurationTicks,
                    "tick");
            }
            else
            {
                DrawFloatField(valueRect, ref settings.rangedDodgeDurationGrowthPerLevelTicks, ref buffers.rangedDodgeDurationGrowthTicks,
                    0f, MaximumMultiplierPercent, "tick");
                DrawIntField(minimumRect, ref settings.rangedDodgeDurationBaseTicks, ref buffers.rangedDodgeDurationBaseTicks,
                    RimKataSettings.MinimumRangedDodgeDurationTicks, RimKataSettings.MaximumRangedDodgeDurationTicks,
                    "tick");
            }

            DrawFixedCheckbox(fixedRect, ref settings.rangedDodgeDurationFixed);
            y += RowHeight;
        }

        private static void DrawFloatRow(
            float width,
            ref float y,
            string labelKey,
            SkillDef skill,
            ref bool fixedValue,
            ref float fixedPercent,
            ref string fixedBuffer,
            ref float growthPercent,
            ref string growthBuffer,
            ref float minimumPercent,
            ref string minimumBuffer,
            float fixedMaximum,
            float minimumMaximum,
            bool showMinimum,
            float growthMaximum = MaximumMultiplierPercent)
        {
            Rect row = new Rect(0f, y, width, RowHeight);
            SplitRow(row, showMinimum, out Rect labelRect, out Rect valueRect, out Rect minimumRect, out Rect fixedRect);
            string label = labelKey.Translate();
            if (!fixedValue)
            {
                label = "KRWF_RimKata_GrowthLabel".Translate(label, skill.LabelCap);
            }

            DrawRowLabel(labelRect, label);
            if (fixedValue)
            {
                DrawFloatField(valueRect, ref fixedPercent, ref fixedBuffer, 0f, fixedMaximum, "%");
            }
            else
            {
                DrawFloatField(valueRect, ref growthPercent, ref growthBuffer, 0f, growthMaximum, "%");
                DrawFloatField(minimumRect, ref minimumPercent, ref minimumBuffer, 0f, minimumMaximum, "%");
            }

            DrawFixedCheckbox(fixedRect, ref fixedValue);
            y += RowHeight;
        }

        private static void DrawSimpleFloatRow(
            float width,
            ref float y,
            string labelKey,
            ref float value,
            ref string buffer,
            float minimum,
            float maximum,
            string unit)
        {
            Rect row = new Rect(0f, y, width, RowHeight);
            SplitRow(row, false, out Rect labelRect, out Rect valueRect, out _, out _);
            DrawFloatField(valueRect, ref value, ref buffer, minimum, maximum, unit);
            DrawRowLabel(labelRect, labelKey.Translate());
            y += RowHeight;
        }

        private static void DrawSimpleIntRow(
            float width,
            ref float y,
            string labelKey,
            ref int value,
            ref string buffer,
            int minimum,
            int maximum,
            string unit)
        {
            Rect row = new Rect(0f, y, width, RowHeight);
            SplitRow(row, false, out Rect labelRect, out Rect valueRect, out _, out _);
            DrawIntField(valueRect, ref value, ref buffer, minimum, maximum, unit);
            DrawRowLabel(labelRect, labelKey.Translate());
            y += RowHeight;
        }

        private static void SplitRow(
            Rect row,
            bool showMinimum,
            out Rect labelRect,
            out Rect valueRect,
            out Rect minimumRect,
            out Rect fixedRect)
        {
            fixedRect = new Rect(row.xMax - FixedColumnWidth, row.y, FixedColumnWidth, row.height);
            minimumRect = showMinimum
                ? new Rect(fixedRect.x - ColumnGap - MinimumFieldWidth, row.y + 2f, MinimumFieldWidth, row.height - 4f)
                : new Rect(fixedRect.x, row.y + 2f, 0f, row.height - 4f);
            valueRect = new Rect(row.x, row.y + 2f, FieldWidth, row.height - 4f);
            labelRect = new Rect(valueRect.xMax + ColumnGap, row.y, Mathf.Max(1f, (showMinimum ? minimumRect.x : fixedRect.x) - valueRect.xMax - ColumnGap - LabelPadding), row.height);
        }

        private static void DrawRowLabel(Rect rect, string label)
        {
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect, label);
            Text.Anchor = previousAnchor;
        }

        private static void DrawFixedCheckbox(Rect rect, ref bool value)
        {
            Widgets.Checkbox(new Vector2(rect.x + (rect.width - 24f) * 0.5f, rect.y + (rect.height - 24f) * 0.5f), ref value, 24f, paintable: true);
        }

        internal static void DrawFloatField(
            Rect rect,
            ref float value,
            ref string buffer,
            float minimum,
            float maximum,
            string unit)
        {
            Widgets.TextFieldNumeric(rect, ref value, ref buffer, minimum, maximum);
            if (!NumericFieldHasFocus(rect))
            {
                DrawNumericUnit(rect, buffer.NullOrEmpty() ? value.ToString() : buffer, unit);
            }
        }

        internal static void DrawIntField(
            Rect rect,
            ref int value,
            ref string buffer,
            int minimum,
            int maximum,
            string unit)
        {
            Widgets.TextFieldNumeric(rect, ref value, ref buffer, minimum, maximum);
            if (!NumericFieldHasFocus(rect))
            {
                DrawNumericUnit(rect, buffer.NullOrEmpty() ? value.ToString() : buffer, unit);
            }
        }

        private static bool NumericFieldHasFocus(Rect rect)
        {
            string numericControlName = "TextField" + rect.y.ToString("F0") + rect.x.ToString("F0");
            return GUI.GetNameOfFocusedControl() == numericControlName;
        }

        private static void DrawNumericUnit(Rect fieldRect, string displayedNumber, string unit)
        {
            float numberWidth = Text.CalcSize(displayedNumber).x;
            float unitWidth = Text.CalcSize(unit).x;
            if (numberWidth + unitWidth + 10f > fieldRect.width)
            {
                return;
            }

            float unitX = Mathf.Min(fieldRect.x + 5f + numberWidth, fieldRect.xMax - unitWidth - 4f);
            Rect unitRect = new Rect(unitX, fieldRect.y + 1f, unitWidth + 2f, fieldRect.height);
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(unitRect, unit);
            Text.Anchor = previousAnchor;
        }

        private static void FinishSection(float width, ref float y)
        {
            y += 3f;
            Widgets.DrawLineHorizontal(0f, y, width);
            y += 5f;
        }

        private static float CalculateContentHeight()
        {
            const int rowCount = 23;
            const int sectionCount = 3;
            return ButtonHeight * 3f + 3f
                + rowCount * RowHeight
                + sectionCount * (SectionHeaderHeight + 8f)
                + ColumnHeaderHeight
                + SectionHeaderHeight + 13f
                + 24f;
        }

        private static bool HasAnyGrowth(RimKataSettings settings)
        {
            return settings != null
                && (!settings.rangedDodgeDurationFixed
                    || !settings.rangedDodgeChanceFixed
                    || !settings.proneMissChanceFixed
                    || !settings.proneHuntingStealthBonusFixed
                    || !settings.meleeResponseChanceFixed
                    || !settings.responseDisarmChanceFixed
                    || !settings.meleeDodgeChanceFixed
                    || !settings.interceptionChanceFixed
                    || !settings.interceptionCriticalChanceFixed
                    || !settings.movingAccuracyMultiplierFixed
                    || !settings.armorCooldownReductionFixed
                    || !settings.responseCooldownReductionFixed
                    || !settings.serumDodgeMultiplierFixed
                    || !settings.serumResponseMultiplierFixed
                    || !settings.serumInterceptionMultiplierFixed);
        }

        private static float MaximumTranslatedLabelWidth()
        {
            string[] keys =
            {
                "KRWF_RimKata_RangedDodgeDuration",
                "KRWF_RimKata_ProneMissChance",
                "KRWF_RimKata_ProneHuntingStealthBonus",
                "KRWF_RimKata_RangedDodgeChance",
                "KRWF_RimKata_MeleeResponseChance",
                "KRWF_RimKata_ResponseDisarmChance",
                "KRWF_RimKata_MeleeDodgeChance",
                "KRWF_RimKata_InterceptionChance",
                "KRWF_RimKata_InterceptionCriticalChance",
                "KRWF_RimKata_MovingAccuracyMultiplier",
                "KRWF_RimKata_ArmorCooldownReduction",
                "KRWF_RimKata_ResponseCooldownReduction",
                "KRWF_RimKata_SerumDodgeMultiplier",
                "KRWF_RimKata_SerumResponseMultiplier",
                "KRWF_RimKata_SerumInterceptionMultiplier",
                "KRWF_RimKata_MaximumCandidateRange",
                "KRWF_RimKata_CreepJoinerRimKataGeneChance",
                "KRWF_RimKata_CreepJoinerDependencyGeneChance",
                "KRWF_RimKata_AiSecondaryWeaponChance",
                "KRWF_RimKata_CombatFeatures",
                "KRWF_RimKata_EnableFriendlyPawnEffects",
                "KRWF_RimKata_EnableHostilePawnEffects"
            };

            float maximum = 0f;
            for (int i = 0; i < keys.Length; i++)
            {
                string plain = keys[i].Translate();
                maximum = Mathf.Max(maximum, Text.CalcSize(plain).x);
                string meleeGrowth = "KRWF_RimKata_GrowthLabel".Translate(plain, SkillDefOf.Melee.LabelCap);
                string shootingGrowth = "KRWF_RimKata_GrowthLabel".Translate(plain, SkillDefOf.Shooting.LabelCap);
                maximum = Mathf.Max(maximum, Text.CalcSize(meleeGrowth).x, Text.CalcSize(shootingGrowth).x);
            }

            string duration = "KRWF_RimKata_DurationGrowthLabel".Translate("KRWF_RimKata_RangedDodgeDuration".Translate(), SkillDefOf.Melee.LabelCap);
            return Mathf.Max(maximum, Text.CalcSize(duration).x);
        }

        private static void ResetCombat(RimKataSettings settings)
        {
            settings.rangedDodgeDurationTicks = RimKataSettings.DefaultRangedDodgeDurationTicks;
            settings.rangedDodgeDurationGrowthPerLevelTicks = RimKataSettings.DefaultRangedDodgeDurationGrowthPerLevelTicks;
            settings.rangedDodgeDurationBaseTicks = RimKataSettings.DefaultRangedDodgeDurationBaseTicks;
            settings.rangedDodgeDurationFixed = RimKataSettings.DefaultRangedDodgeDurationFixed;
            settings.proneMissChancePercent = RimKataSettings.DefaultProneMissChancePercent;
            settings.proneMissChanceGrowthPerLevelPercent = RimKataSettings.DefaultProneMissChanceGrowthPerLevelPercent;
            settings.proneMissChanceMinimumPercent = RimKataSettings.DefaultProneMissChanceMinimumPercent;
            settings.proneMissChanceFixed = RimKataSettings.DefaultProneMissChanceFixed;
            settings.proneHuntingStealthBonusPercent = RimKataSettings.DefaultProneHuntingStealthBonusPercent;
            settings.proneHuntingStealthBonusGrowthPerLevelPercent = RimKataSettings.DefaultProneHuntingStealthBonusGrowthPerLevelPercent;
            settings.proneHuntingStealthBonusMinimumPercent = RimKataSettings.DefaultProneHuntingStealthBonusMinimumPercent;
            settings.proneHuntingStealthBonusFixed = RimKataSettings.DefaultProneHuntingStealthBonusFixed;
            settings.rangedDodgeChancePercent = RimKataSettings.DefaultRangedDodgeChancePercent;
            settings.rangedDodgeChanceGrowthPerLevelPercent = RimKataSettings.DefaultRangedDodgeChanceGrowthPerLevelPercent;
            settings.rangedDodgeChanceMinimumPercent = RimKataSettings.DefaultRangedDodgeChanceMinimumPercent;
            settings.rangedDodgeChanceFixed = RimKataSettings.DefaultRangedDodgeChanceFixed;
            settings.meleeResponseChancePercent = RimKataSettings.DefaultMeleeResponseChancePercent;
            settings.meleeResponseChanceGrowthPerLevelPercent = RimKataSettings.DefaultMeleeResponseChanceGrowthPerLevelPercent;
            settings.meleeResponseChanceMinimumPercent = RimKataSettings.DefaultMeleeResponseChanceMinimumPercent;
            settings.meleeResponseChanceFixed = RimKataSettings.DefaultMeleeResponseChanceFixed;
            settings.responseDisarmChancePercent = RimKataSettings.DefaultResponseDisarmChancePercent;
            settings.responseDisarmChanceGrowthPerLevelPercent = RimKataSettings.DefaultResponseDisarmChanceGrowthPerLevelPercent;
            settings.responseDisarmChanceMinimumPercent = RimKataSettings.DefaultResponseDisarmChanceMinimumPercent;
            settings.responseDisarmChanceFixed = RimKataSettings.DefaultResponseDisarmChanceFixed;
            settings.meleeDodgeChancePercent = RimKataSettings.DefaultMeleeDodgeChancePercent;
            settings.meleeDodgeChanceGrowthPerLevelPercent = RimKataSettings.DefaultMeleeDodgeChanceGrowthPerLevelPercent;
            settings.meleeDodgeChanceMinimumPercent = RimKataSettings.DefaultMeleeDodgeChanceMinimumPercent;
            settings.meleeDodgeChanceFixed = RimKataSettings.DefaultMeleeDodgeChanceFixed;
            settings.interceptionChancePercent = RimKataSettings.DefaultInterceptionChancePercent;
            settings.interceptionChanceGrowthPerLevelPercent = RimKataSettings.DefaultInterceptionChanceGrowthPerLevelPercent;
            settings.interceptionChanceMinimumPercent = RimKataSettings.DefaultInterceptionChanceMinimumPercent;
            settings.interceptionChanceFixed = RimKataSettings.DefaultInterceptionChanceFixed;
            settings.interceptionCriticalChancePercent = RimKataSettings.DefaultInterceptionCriticalChancePercent;
            settings.interceptionCriticalChanceGrowthPerLevelPercent = RimKataSettings.DefaultInterceptionCriticalChanceGrowthPerLevelPercent;
            settings.interceptionCriticalChanceMinimumPercent = RimKataSettings.DefaultInterceptionCriticalChanceMinimumPercent;
            settings.interceptionCriticalChanceFixed = RimKataSettings.DefaultInterceptionCriticalChanceFixed;
        }

        private static void ResetModifiers(RimKataSettings settings)
        {
            settings.movingAccuracyMultiplierPercent = RimKataSettings.DefaultMovingAccuracyMultiplierPercent;
            settings.movingAccuracyMultiplierGrowthPerLevelPercent = RimKataSettings.DefaultMovingAccuracyMultiplierGrowthPerLevelPercent;
            settings.movingAccuracyMultiplierMinimumPercent = RimKataSettings.DefaultMovingAccuracyMultiplierMinimumPercent;
            settings.movingAccuracyMultiplierFixed = RimKataSettings.DefaultMovingAccuracyMultiplierFixed;
            settings.armorCooldownReductionPercent = RimKataSettings.DefaultArmorCooldownReductionPercent;
            settings.armorCooldownReductionGrowthPerLevelPercent = RimKataSettings.DefaultArmorCooldownReductionGrowthPerLevelPercent;
            settings.armorCooldownReductionMinimumPercent = RimKataSettings.DefaultArmorCooldownReductionMinimumPercent;
            settings.armorCooldownReductionFixed = RimKataSettings.DefaultArmorCooldownReductionFixed;
            settings.responseCooldownReductionPercent = RimKataSettings.DefaultResponseCooldownReductionPercent;
            settings.responseCooldownReductionGrowthPerLevelPercent = RimKataSettings.DefaultResponseCooldownReductionGrowthPerLevelPercent;
            settings.responseCooldownReductionMinimumPercent = RimKataSettings.DefaultResponseCooldownReductionMinimumPercent;
            settings.responseCooldownReductionFixed = RimKataSettings.DefaultResponseCooldownReductionFixed;
        }

        private static void ResetSerum(RimKataSettings settings)
        {
            settings.serumDodgeMultiplierPercent = RimKataSettings.DefaultSerumDodgeMultiplierPercent;
            settings.serumDodgeMultiplierGrowthPerLevelPercent = RimKataSettings.DefaultSerumDodgeMultiplierGrowthPerLevelPercent;
            settings.serumDodgeMultiplierMinimumPercent = RimKataSettings.DefaultSerumDodgeMultiplierMinimumPercent;
            settings.serumDodgeMultiplierFixed = RimKataSettings.DefaultSerumDodgeMultiplierFixed;
            settings.serumResponseMultiplierPercent = RimKataSettings.DefaultSerumResponseMultiplierPercent;
            settings.serumResponseMultiplierGrowthPerLevelPercent = RimKataSettings.DefaultSerumResponseMultiplierGrowthPerLevelPercent;
            settings.serumResponseMultiplierMinimumPercent = RimKataSettings.DefaultSerumResponseMultiplierMinimumPercent;
            settings.serumResponseMultiplierFixed = RimKataSettings.DefaultSerumResponseMultiplierFixed;
            settings.serumInterceptionMultiplierPercent = RimKataSettings.DefaultSerumInterceptionMultiplierPercent;
            settings.serumInterceptionMultiplierGrowthPerLevelPercent = RimKataSettings.DefaultSerumInterceptionMultiplierGrowthPerLevelPercent;
            settings.serumInterceptionMultiplierMinimumPercent = RimKataSettings.DefaultSerumInterceptionMultiplierMinimumPercent;
            settings.serumInterceptionMultiplierFixed = RimKataSettings.DefaultSerumInterceptionMultiplierFixed;
        }
    }
}
