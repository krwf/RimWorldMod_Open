using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public sealed class Dialog_RimKataModFeatures : Window
    {
        private const float RowHeight = 30f;
        private const float HeaderHeight = 32f;
        private const float HeaderLineGap = 5f;
        private const float SectionGap = 8f;
        private const float FieldWidth = 86f;
        private const float ColumnGap = 12f;
        private const float ButtonHeight = 30f;
        private const float ButtonGap = 8f;
        private const float BottomGap = 10f;
        private const float ScrollbarWidth = 18f;
        private const float ContentHeight = RowHeight * 11f + HeaderHeight * 3f + (SectionGap + HeaderLineGap) * 2f;
        private readonly RimKataSettings settings;
        private readonly RimKataSettingsUiBuffers mainBuffers;
        private bool commitChangesOnClose;
        private Vector2 scrollPosition;
        private int touchCandidateLimit;
        private int shortCandidateLimit;
        private int mediumCandidateLimit;
        private int longCandidateLimit;
        private int beyondCandidateLimit;
        private bool showRangedWeaponCooldown;
        private bool showMeleeWeaponAimTime;
        private bool showFocusedAttackLine;
        private bool smoothAimTransition;
        private bool crawlFireDefaultAllowed;
        private bool autoSubdueReleaseDowned;
        private string touchCandidateBuffer;
        private string shortCandidateBuffer;
        private string mediumCandidateBuffer;
        private string longCandidateBuffer;
        private string beyondCandidateBuffer;

        private static readonly string[] NumericLabelKeys =
        {
            "KRWF_RimKata_TouchCandidateLimit",
            "KRWF_RimKata_ShortCandidateLimit",
            "KRWF_RimKata_MediumCandidateLimit",
            "KRWF_RimKata_LongCandidateLimit",
            "KRWF_RimKata_BeyondCandidateLimit"
        };

        private static readonly string[] CheckboxLabelKeys =
        {
            "KRWF_RimKata_ShowRangedWeaponCooldown",
            "KRWF_RimKata_ShowMeleeWeaponAimTime",
            "KRWF_RimKata_ShowFocusedAttackLine",
            "KRWF_RimKata_SmoothAimTransition",
            "KRWF_RimKata_CrawlFireDefaultAllowed",
            "KRWF_RimKata_AutoSubdueReleaseDowned"
        };

        private static readonly string[] HeaderKeys =
        {
            "KRWF_RimKata_CandidateLimitHeader",
            "KRWF_RimKata_VisualGuidanceHeader",
            "KRWF_RimKata_DefaultsHeader"
        };

        public Dialog_RimKataModFeatures(RimKataSettings settings, RimKataSettingsUiBuffers buffers)
        {
            this.settings = settings;
            mainBuffers = buffers;
            if (settings != null)
            {
                touchCandidateLimit = settings.touchCandidateLimit;
                shortCandidateLimit = settings.shortCandidateLimit;
                mediumCandidateLimit = settings.mediumCandidateLimit;
                longCandidateLimit = settings.longCandidateLimit;
                beyondCandidateLimit = settings.beyondCandidateLimit;
                showRangedWeaponCooldown = settings.showRangedWeaponCooldown;
                showMeleeWeaponAimTime = settings.showMeleeWeaponAimTime;
                showFocusedAttackLine = settings.showFocusedAttackLine;
                smoothAimTransition = settings.smoothAimTransition;
                crawlFireDefaultAllowed = settings.crawlFireDefaultAllowed;
                autoSubdueReleaseDowned = settings.autoSubdueReleaseDowned;
            }
            touchCandidateBuffer = touchCandidateLimit.ToString();
            shortCandidateBuffer = shortCandidateLimit.ToString();
            mediumCandidateBuffer = mediumCandidateLimit.ToString();
            longCandidateBuffer = longCandidateLimit.ToString();
            beyondCandidateBuffer = beyondCandidateLimit.ToString();

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
                for (int i = 0; i < CheckboxLabelKeys.Length; i++)
                    width = Mathf.Max(width, Text.CalcSize(CheckboxLabelKeys[i].Translate()).x + 30f);
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
            DrawIntRow(viewRect.width, ref y, NumericLabelKeys[0], ref touchCandidateLimit, ref touchCandidateBuffer);
            DrawIntRow(viewRect.width, ref y, NumericLabelKeys[1], ref shortCandidateLimit, ref shortCandidateBuffer);
            DrawIntRow(viewRect.width, ref y, NumericLabelKeys[2], ref mediumCandidateLimit, ref mediumCandidateBuffer);
            DrawIntRow(viewRect.width, ref y, NumericLabelKeys[3], ref longCandidateLimit, ref longCandidateBuffer);
            DrawIntRow(viewRect.width, ref y, NumericLabelKeys[4], ref beyondCandidateLimit, ref beyondCandidateBuffer);
            y += SectionGap;
            Widgets.DrawLineHorizontal(0f, y, viewRect.width);
            y += HeaderLineGap;
            DrawHeader(viewRect.width, ref y, HeaderKeys[1]);
            DrawCheckbox(viewRect.width, ref y, CheckboxLabelKeys[0], ref showRangedWeaponCooldown);
            DrawCheckbox(viewRect.width, ref y, CheckboxLabelKeys[1], ref showMeleeWeaponAimTime);
            DrawCheckbox(viewRect.width, ref y, CheckboxLabelKeys[2], ref showFocusedAttackLine);
            DrawCheckbox(viewRect.width, ref y, CheckboxLabelKeys[3], ref smoothAimTransition);
            y += SectionGap;
            Widgets.DrawLineHorizontal(0f, y, viewRect.width);
            y += HeaderLineGap;
            DrawHeader(viewRect.width, ref y, HeaderKeys[2]);
            DrawCheckbox(viewRect.width, ref y, CheckboxLabelKeys[4], ref crawlFireDefaultAllowed);
            DrawCheckbox(viewRect.width, ref y, CheckboxLabelKeys[5], ref autoSubdueReleaseDowned);
            Widgets.EndScrollView();
            DrawButtons(new Rect(inRect.x, inRect.yMax - ButtonHeight, inRect.width, ButtonHeight));
            Text.Font = previousFont;
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!commitChangesOnClose || settings == null)
                return;

            bool changed = settings.touchCandidateLimit != touchCandidateLimit
                || settings.shortCandidateLimit != shortCandidateLimit
                || settings.mediumCandidateLimit != mediumCandidateLimit
                || settings.longCandidateLimit != longCandidateLimit
                || settings.beyondCandidateLimit != beyondCandidateLimit
                || settings.showRangedWeaponCooldown != showRangedWeaponCooldown
                || settings.showMeleeWeaponAimTime != showMeleeWeaponAimTime
                || settings.showFocusedAttackLine != showFocusedAttackLine
                || settings.smoothAimTransition != smoothAimTransition
                || settings.crawlFireDefaultAllowed != crawlFireDefaultAllowed
                || settings.autoSubdueReleaseDowned != autoSubdueReleaseDowned;
            settings.touchCandidateLimit = touchCandidateLimit;
            settings.shortCandidateLimit = shortCandidateLimit;
            settings.mediumCandidateLimit = mediumCandidateLimit;
            settings.longCandidateLimit = longCandidateLimit;
            settings.beyondCandidateLimit = beyondCandidateLimit;
            settings.showRangedWeaponCooldown = showRangedWeaponCooldown;
            settings.showMeleeWeaponAimTime = showMeleeWeaponAimTime;
            settings.showFocusedAttackLine = showFocusedAttackLine;
            settings.smoothAimTransition = smoothAimTransition;
            settings.crawlFireDefaultAllowed = crawlFireDefaultAllowed;
            settings.autoSubdueReleaseDowned = autoSubdueReleaseDowned;
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

        private static void DrawRowLabel(Rect rect, string label)
        {
            TextAnchor previousAnchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect, label);
            Text.Anchor = previousAnchor;
        }

        private static void DrawCheckbox(float width, ref float y, string key, ref bool value)
        {
            RimKataFeatureWindowUtility.DrawCheckbox(new Rect(0f, y, width, RowHeight), key.Translate(), ref value);
            y += RowHeight;
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
