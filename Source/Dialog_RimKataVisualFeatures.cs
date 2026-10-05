using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataVisualFeatures : Window
    {
        private const float RowHeight = 30f;
        private const float HeaderHeight = 32f;
        private const float FieldWidth = 86f;
        private const float ColumnGap = 12f;
        private const float ButtonHeight = 30f;
        private const float ButtonGap = 8f;
        private const float BottomGap = 10f;
        private const float ScrollbarWidth = 18f;
        private const float ContentHeight = RowHeight * 18f + HeaderHeight;
        private readonly RimKataSettings settings;
        private readonly RimKataSettingsUiBuffers mainBuffers;
        private bool commitChangesOnClose;
        private Vector2 scrollPosition;
        private float immediateTumbleChancePercent;
        private float responseAttackerSpinChancePercent;
        private float responseAccidentalFireChancePercent;
        private float responseWeaponDurabilityLossChancePercent;
        private int responseWeaponDurabilityLossAmount;
        private float unarmedWeaponStealChancePercent;
        private int proneResumeDelayTicks;
        private float meleeFallChancePercent;
        private int meleeFallDurationTicks;
        private float fallenFriendlyFireAvoidChancePercent;
        private int breachSlideDurationTicks;
        private int breachWaitDurationTicks;
        private float slidingChancePercent;
        private float flyingKickDamageMultiplierPercent;
        private float kickChancePercent;
        private float shakeOffChancePercent;
        private int subdueImpactStunTicks;
        private float strengthIncreasePercent;
        private string immediateTumbleBuffer;
        private string responseAttackerSpinBuffer;
        private string responseAccidentalFireBuffer;
        private string durabilityChanceBuffer;
        private string durabilityAmountBuffer;
        private string unarmedWeaponStealBuffer;
        private string proneResumeDelayBuffer;
        private string meleeFallChanceBuffer;
        private string meleeFallDurationBuffer;
        private string fallenFriendlyFireAvoidChanceBuffer;
        private string breachSlideDurationBuffer;
        private string breachWaitDurationBuffer;
        private string slidingChanceBuffer;
        private string flyingKickDamageBuffer;
        private string kickChanceBuffer;
        private string shakeOffChanceBuffer;
        private string subdueImpactStunBuffer;
        private string strengthIncreaseBuffer;

        private static readonly string[] NumericLabelKeys =
        {
            "KRWF_RimKata_ImmediateTumbleChance",
            "KRWF_RimKata_ResponseAttackerSpinChance",
            "KRWF_RimKata_ResponseAccidentalFireChance",
            "KRWF_RimKata_ResponseWeaponDurabilityLossChance",
            "KRWF_RimKata_ResponseWeaponDurabilityLossAmount",
            "KRWF_RimKata_UnarmedWeaponStealChance",
            "KRWF_RimKata_ProneResumeDelay",
            "KRWF_RimKata_MeleeFallChance",
            "KRWF_RimKata_MeleeFallDuration",
            "KRWF_RimKata_FallenFriendlyFireAvoidChance",
            "KRWF_RimKata_BreachSlideDuration",
            "KRWF_RimKata_BreachWaitDuration",
            "KRWF_RimKata_FlyingKickDamage",
            "KRWF_RimKata_KickChance",
            "KRWF_RimKata_SlidingChance",
            "KRWF_RimKata_ShakeOffChance",
            "KRWF_RimKata_SubdueImpactStunDuration",
            "KRWF_RimKata_AllowedStrengthIncrease"
        };

        private static readonly string[] HeaderKeys =
        {
            "KRWF_RimKata_ModVisualsHeader"
        };

        public Dialog_RimKataVisualFeatures(RimKataSettings settings, RimKataSettingsUiBuffers buffers)
        {
            this.settings = settings;
            mainBuffers = buffers;
            if (settings != null)
            {
                immediateTumbleChancePercent = settings.immediateTumbleChancePercent;
                responseAttackerSpinChancePercent = settings.responseAttackerSpinChancePercent;
                responseAccidentalFireChancePercent = settings.responseAccidentalFireChancePercent;
                responseWeaponDurabilityLossChancePercent = settings.responseWeaponDurabilityLossChancePercent;
                responseWeaponDurabilityLossAmount = settings.responseWeaponDurabilityLossAmount;
                unarmedWeaponStealChancePercent = settings.unarmedWeaponStealChancePercent;
                proneResumeDelayTicks = settings.proneResumeDelayTicks;
                meleeFallChancePercent = settings.meleeFallChancePercent;
                meleeFallDurationTicks = settings.meleeFallDurationTicks;
                fallenFriendlyFireAvoidChancePercent = settings.fallenFriendlyFireAvoidChancePercent;
                breachSlideDurationTicks = settings.breachSlideDurationTicks;
                breachWaitDurationTicks = settings.breachWaitDurationTicks;
                slidingChancePercent = settings.slidingChancePercent;
                flyingKickDamageMultiplierPercent = settings.flyingKickDamageMultiplierPercent;
                kickChancePercent = settings.kickChancePercent;
                shakeOffChancePercent = settings.shakeOffChancePercent;
                subdueImpactStunTicks = settings.subdueImpactStunTicks;
                strengthIncreasePercent = settings.strengthIncreasePercent;
            }
            immediateTumbleBuffer = immediateTumbleChancePercent.ToString();
            responseAttackerSpinBuffer = responseAttackerSpinChancePercent.ToString();
            responseAccidentalFireBuffer = responseAccidentalFireChancePercent.ToString();
            durabilityChanceBuffer = responseWeaponDurabilityLossChancePercent.ToString();
            durabilityAmountBuffer = responseWeaponDurabilityLossAmount.ToString();
            unarmedWeaponStealBuffer = unarmedWeaponStealChancePercent.ToString();
            proneResumeDelayBuffer = proneResumeDelayTicks.ToString();
            meleeFallChanceBuffer = meleeFallChancePercent.ToString();
            meleeFallDurationBuffer = meleeFallDurationTicks.ToString();
            fallenFriendlyFireAvoidChanceBuffer = fallenFriendlyFireAvoidChancePercent.ToString();
            breachSlideDurationBuffer = breachSlideDurationTicks.ToString();
            breachWaitDurationBuffer = breachWaitDurationTicks.ToString();
            slidingChanceBuffer = slidingChancePercent.ToString();
            flyingKickDamageBuffer = flyingKickDamageMultiplierPercent.ToString();
            kickChanceBuffer = kickChancePercent.ToString();
            shakeOffChanceBuffer = shakeOffChancePercent.ToString();
            subdueImpactStunBuffer = subdueImpactStunTicks.ToString();
            strengthIncreaseBuffer = strengthIncreasePercent.ToString();

            doCloseX = false;
            doCloseButton = false;
            closeOnClickedOutside = true;
            closeOnAccept = false;
            closeOnCancel = false;
            absorbInputAroundWindow = true;
            resizeable = false;
            draggable = true;
        }

        public override Vector2 InitialSize
        {
            get
            {
                GameFont previousFont = Text.Font;
                Text.Font = GameFont.Small;
                float width = ButtonWidth("Close") + ButtonGap + ButtonWidth("Confirm");
                for (int i = 0; i < NumericLabelKeys.Length; i++)
                    width = Mathf.Max(width, Text.CalcSize(NumericLabelKeys[i].Translate()).x + FieldWidth + ColumnGap + 24f);
                Text.Font = GameFont.Medium;
                for (int i = 0; i < HeaderKeys.Length; i++)
                    width = Mathf.Max(width, Text.CalcSize(HeaderKeys[i].Translate()).x);
                Text.Font = previousFont;

                float maximumWidth = Mathf.Max(220f, UI.screenWidth - 80f);
                float maximumHeight = Mathf.Max(180f, UI.screenHeight - 80f);
                float height = Mathf.Min(ContentHeight + BottomGap + ButtonHeight + Margin * 2f, maximumHeight);
                bool scrolls = height < ContentHeight + BottomGap + ButtonHeight + Margin * 2f;
                return new Vector2(Mathf.Clamp(width + Margin * 2f + (scrolls ? ScrollbarWidth : 0f), 300f, maximumWidth), height);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (settings == null)
            {
                Close();
                return;
            }

            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Small;
            Rect outRect = new Rect(inRect.x, inRect.y, inRect.width, Mathf.Max(1f, inRect.height - ButtonHeight - BottomGap));
            bool scrolls = ContentHeight > outRect.height;
            Rect viewRect = new Rect(0f, 0f, Mathf.Max(1f, outRect.width - (scrolls ? ScrollbarWidth : 0f)), ContentHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            float y = 0f;
            DrawHeader(viewRect.width, ref y, HeaderKeys[0]);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[0], ref immediateTumbleChancePercent, ref immediateTumbleBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[1], ref responseAttackerSpinChancePercent, ref responseAttackerSpinBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[2], ref responseAccidentalFireChancePercent, ref responseAccidentalFireBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[3], ref responseWeaponDurabilityLossChancePercent, ref durabilityChanceBuffer);
            DrawIntRow(viewRect.width, ref y, NumericLabelKeys[4], ref responseWeaponDurabilityLossAmount, ref durabilityAmountBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[5], ref unarmedWeaponStealChancePercent, ref unarmedWeaponStealBuffer);
            DrawTickRow(viewRect.width, ref y, NumericLabelKeys[6], ref proneResumeDelayTicks, ref proneResumeDelayBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[7], ref meleeFallChancePercent, ref meleeFallChanceBuffer);
            DrawTickRow(viewRect.width, ref y, NumericLabelKeys[8], ref meleeFallDurationTicks, ref meleeFallDurationBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[9], ref fallenFriendlyFireAvoidChancePercent, ref fallenFriendlyFireAvoidChanceBuffer);
            DrawTickRow(viewRect.width, ref y, NumericLabelKeys[10], ref breachSlideDurationTicks, ref breachSlideDurationBuffer);
            DrawTickRow(viewRect.width, ref y, NumericLabelKeys[11], ref breachWaitDurationTicks, ref breachWaitDurationBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[12], ref flyingKickDamageMultiplierPercent, ref flyingKickDamageBuffer, float.MaxValue);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[13], ref kickChancePercent, ref kickChanceBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[14], ref slidingChancePercent, ref slidingChanceBuffer);
            DrawPercentRow(viewRect.width, ref y, NumericLabelKeys[15], ref shakeOffChancePercent, ref shakeOffChanceBuffer);
            DrawTickRow(viewRect.width, ref y, NumericLabelKeys[16], ref subdueImpactStunTicks, ref subdueImpactStunBuffer);
            RimKataSettingsDrawer.DrawFloatField(new Rect(0f, y + 2f, FieldWidth, RowHeight - 4f),
                ref strengthIncreasePercent, ref strengthIncreaseBuffer, 0f, float.MaxValue, "%");
            strengthIncreasePercent = RimKataStrengthRule.SanitizePercent(strengthIncreasePercent);
            DrawRowLabel(new Rect(FieldWidth + ColumnGap, y, viewRect.width - FieldWidth - ColumnGap, RowHeight),
                NumericLabelKeys[17].Translate());
            y += RowHeight;
            Widgets.EndScrollView();
            DrawButtons(new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight));
            Text.Font = previousFont;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose || settings == null)
                return;

            bool changed = settings.immediateTumbleChancePercent != immediateTumbleChancePercent
                || settings.responseAttackerSpinChancePercent != responseAttackerSpinChancePercent
                || settings.responseAccidentalFireChancePercent != responseAccidentalFireChancePercent
                || settings.responseWeaponDurabilityLossChancePercent != responseWeaponDurabilityLossChancePercent
                || settings.responseWeaponDurabilityLossAmount != responseWeaponDurabilityLossAmount
                || settings.unarmedWeaponStealChancePercent != unarmedWeaponStealChancePercent
                || settings.proneResumeDelayTicks != proneResumeDelayTicks
                || settings.meleeFallChancePercent != meleeFallChancePercent
                || settings.meleeFallDurationTicks != meleeFallDurationTicks
                || settings.fallenFriendlyFireAvoidChancePercent != fallenFriendlyFireAvoidChancePercent
                || settings.breachSlideDurationTicks != breachSlideDurationTicks
                || settings.breachWaitDurationTicks != breachWaitDurationTicks
                || settings.slidingChancePercent != slidingChancePercent
                || settings.flyingKickDamageMultiplierPercent != flyingKickDamageMultiplierPercent
                || settings.kickChancePercent != kickChancePercent
                || settings.shakeOffChancePercent != shakeOffChancePercent
                || settings.subdueImpactStunTicks != subdueImpactStunTicks
                || settings.strengthIncreasePercent != strengthIncreasePercent;
            settings.immediateTumbleChancePercent = immediateTumbleChancePercent;
            settings.responseAttackerSpinChancePercent = responseAttackerSpinChancePercent;
            settings.responseAccidentalFireChancePercent = responseAccidentalFireChancePercent;
            settings.responseWeaponDurabilityLossChancePercent = responseWeaponDurabilityLossChancePercent;
            settings.responseWeaponDurabilityLossAmount = responseWeaponDurabilityLossAmount;
            settings.unarmedWeaponStealChancePercent = unarmedWeaponStealChancePercent;
            settings.proneResumeDelayTicks = proneResumeDelayTicks;
            settings.meleeFallChancePercent = meleeFallChancePercent;
            settings.meleeFallDurationTicks = meleeFallDurationTicks;
            settings.fallenFriendlyFireAvoidChancePercent = fallenFriendlyFireAvoidChancePercent;
            settings.breachSlideDurationTicks = breachSlideDurationTicks;
            settings.breachWaitDurationTicks = breachWaitDurationTicks;
            settings.slidingChancePercent = slidingChancePercent;
            settings.flyingKickDamageMultiplierPercent = flyingKickDamageMultiplierPercent;
            settings.kickChancePercent = kickChancePercent;
            settings.shakeOffChancePercent = shakeOffChancePercent;
            settings.subdueImpactStunTicks = subdueImpactStunTicks;
            settings.strengthIncreasePercent = RimKataStrengthRule.SanitizePercent(strengthIncreasePercent);
            mainBuffers?.SyncFrom(settings);
            if (changed)
                RimKataMod.ApplyCombatFeatureSettingsChange();
        }

        private static void DrawHeader(float width, ref float y, string key)
        {
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, y, width, HeaderHeight), key.Translate());
            Text.Font = previousFont;
            Text.Anchor = previousAnchor;
            y += HeaderHeight;
        }

        private static void DrawIntRow(float width, ref float y, string key, ref int value, ref string buffer)
        {
            Widgets.TextFieldNumeric(new Rect(0f, y + 2f, FieldWidth, RowHeight - 4f), ref value, ref buffer, 1, 999);
            DrawRowLabel(new Rect(FieldWidth + ColumnGap, y, width - FieldWidth - ColumnGap, RowHeight), key.Translate());
            y += RowHeight;
        }

        private static void DrawPercentRow(float width, ref float y, string key, ref float value, ref string buffer,
            float maximum = 100f)
        {
            RimKataSettingsDrawer.DrawFloatField(
                new Rect(0f, y + 2f, FieldWidth, RowHeight - 4f),
                ref value, ref buffer, 0f, maximum, "%");
            DrawRowLabel(new Rect(FieldWidth + ColumnGap, y, width - FieldWidth - ColumnGap, RowHeight), key.Translate());
            y += RowHeight;
        }

        private static void DrawTickRow(float width, ref float y, string key, ref int value, ref string buffer)
        {
            RimKataSettingsDrawer.DrawIntField(
                new Rect(0f, y + 2f, FieldWidth, RowHeight - 4f),
                ref value, ref buffer,
                RimKataSettings.MinimumGroundPoseDurationTicks,
                RimKataSettings.MaximumGroundPoseDurationTicks, "tick");
            DrawRowLabel(new Rect(FieldWidth + ColumnGap, y, width - FieldWidth - ColumnGap, RowHeight), key.Translate());
            y += RowHeight;
        }

        private static void DrawRowLabel(Rect rect, string label)
        {
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect, label);
            Text.Anchor = previousAnchor;
        }

        private static float ButtonWidth(string key)
        {
            return Mathf.Max(90f, Text.CalcSize(key.Translate()).x + 28f);
        }

        private void DrawButtons(Rect rect)
        {
            float closeWidth = ButtonWidth("Close");
            float confirmWidth = ButtonWidth("Confirm");
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
            if (Widgets.ButtonText(new Rect(x + closeWidth + ButtonGap, rect.y, confirmWidth, rect.height), "Confirm".Translate()))
            {
                commitChangesOnClose = true;
                Close();
            }
        }
    }
}
