using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RenameHomeAndUnrestrictedAreas
{
    public class RenameHomeAndUnrestrictedAreasMod : Mod
    {
        internal static RenameHomeAndUnrestrictedAreasMod Instance;
        internal static RenameHomeAndUnrestrictedAreasSettings Settings;

        public RenameHomeAndUnrestrictedAreasMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<RenameHomeAndUnrestrictedAreasSettings>();
            new Harmony("KRWF.RenameHomeAndUnrestrictedAreas").PatchAll();
        }
    }

    public class RenameHomeAndUnrestrictedAreasSettings : ModSettings
    {
        public string homeLabel = "";
        public string unrestrictedLabel = "";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref homeLabel, "homeLabel", "");
            Scribe_Values.Look(ref unrestrictedLabel, "unrestrictedLabel", "");
        }

        public static bool HasCustomHomeLabel
        {
            get { return !string.IsNullOrWhiteSpace(RenameHomeAndUnrestrictedAreasMod.Settings?.homeLabel); }
        }

        public static bool HasCustomUnrestrictedLabel
        {
            get { return !string.IsNullOrWhiteSpace(RenameHomeAndUnrestrictedAreasMod.Settings?.unrestrictedLabel); }
        }

        public static string HomeLabel
        {
            get
            {
                string value = RenameHomeAndUnrestrictedAreasMod.Settings?.homeLabel;
                return string.IsNullOrWhiteSpace(value) ? "Home".Translate().ToString() : value.Trim();
            }
        }

        public static string UnrestrictedLabel
        {
            get
            {
                string value = RenameHomeAndUnrestrictedAreasMod.Settings?.unrestrictedLabel;
                return string.IsNullOrWhiteSpace(value) ? "NoAreaAllowed".Translate().ToString() : value.Trim();
            }
        }
    }

    public class Dialog_RenameHomeAndUnrestrictedAreas : Window
    {
        private const float LabelHorizontalPadding = 20f;
        private const float LabelToTextFieldGap = 10f;
        private const float VanillaRenameTextFieldWidth = 244f;

        private readonly string translatedHomeLabel;
        private readonly string translatedUnrestrictedLabel;
        private readonly float labelColumnWidth;

        private string homeLabel;
        private string unrestrictedLabel;

        public override Vector2 InitialSize => new Vector2(
            labelColumnWidth + LabelToTextFieldGap + VanillaRenameTextFieldWidth + Margin * 2f,
            210f);

        public Dialog_RenameHomeAndUnrestrictedAreas()
        {
            forcePause = true;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            resizeable = false;

            translatedHomeLabel = "Home".Translate().ToString();
            translatedUnrestrictedLabel = "NoAreaAllowed".Translate().ToString();

            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Small;
                labelColumnWidth = Mathf.Ceil(Mathf.Max(
                    Text.CalcSize(translatedHomeLabel).x,
                    Text.CalcSize(translatedUnrestrictedLabel).x) + LabelHorizontalPadding);
            }
            finally
            {
                Text.Font = previousFont;
            }

            homeLabel = RenameHomeAndUnrestrictedAreasSettings.HasCustomHomeLabel
                ? RenameHomeAndUnrestrictedAreasSettings.HomeLabel
                : "";

            unrestrictedLabel = RenameHomeAndUnrestrictedAreasSettings.HasCustomUnrestrictedLabel
                ? RenameHomeAndUnrestrictedAreasSettings.UnrestrictedLabel
                : "";
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "Rename".Translate());

            Text.Font = GameFont.Small;

            float y = 42f;
            float textFieldX = labelColumnWidth + LabelToTextFieldGap;

            TextAnchor previousAnchor = Text.Anchor;
            try
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, y, labelColumnWidth, 30f), translatedUnrestrictedLabel);
                Widgets.Label(new Rect(0f, y + 48f, labelColumnWidth, 30f), translatedHomeLabel);
            }
            finally
            {
                Text.Anchor = previousAnchor;
            }

            unrestrictedLabel = Widgets.TextField(new Rect(textFieldX, y, VanillaRenameTextFieldWidth, 30f), unrestrictedLabel);

            y += 48f;
            homeLabel = Widgets.TextField(new Rect(textFieldX, y, VanillaRenameTextFieldWidth, 30f), homeLabel);

            const float buttonWidth = 120f;
            const float buttonHeight = 38f;
            Rect resetRect = new Rect(inRect.width - buttonWidth * 2f - 10f, inRect.height - buttonHeight, buttonWidth, buttonHeight);
            Rect OKRect = new Rect(inRect.width - buttonWidth, inRect.height - buttonHeight, buttonWidth, buttonHeight);

            if (Widgets.ButtonText(resetRect, "Reset".Translate()))
            {
                homeLabel = "";
                unrestrictedLabel = "";
            }

            if (Widgets.ButtonText(OKRect, "OK".Translate()))
            {
                RenameHomeAndUnrestrictedAreasMod.Settings.homeLabel = string.IsNullOrWhiteSpace(homeLabel) ? "" : homeLabel.Trim();
                RenameHomeAndUnrestrictedAreasMod.Settings.unrestrictedLabel = string.IsNullOrWhiteSpace(unrestrictedLabel) ? "" : unrestrictedLabel.Trim();
                RenameHomeAndUnrestrictedAreasMod.Settings.Write();
                Close();
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_ManageAreas), nameof(Dialog_ManageAreas.DoWindowContents))]
    internal static class Patch_DialogManageAreas_DoWindowContents
    {
        private const float RenameButtonHorizontalPadding = 20f;
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(Dialog_ManageAreas), "map");
        private static readonly MethodInfo DoAreaRowMethod = AccessTools.Method(typeof(Dialog_ManageAreas), "DoAreaRow");

        private static string GetTranslatedInitial(string translationKey)
        {
            string translated = translationKey.Translate().ToString().Trim();
            return string.IsNullOrEmpty(translated) ? "?" : StringInfo.GetNextTextElement(translated);
        }

        private static string GetRenameButtonLabel()
        {
            return GetTranslatedInitial("Home") + " & " + GetTranslatedInitial("NoAreaAllowed");
        }

        public static bool Prefix(Dialog_ManageAreas __instance, Rect inRect)
        {
            Map map = MapField.GetValue(__instance) as Map;
            if (map == null)
            {
                return true;
            }

            Listing_Standard listing = new Listing_Standard();
            listing.ColumnWidth = inRect.width;
            listing.Begin(inRect);

            List<Area> allAreas = map.areaManager.AllAreas;
            int visibleRows = 0;

            for (int i = 0; i < allAreas.Count; i++)
            {
                if (allAreas[i].Mutable)
                {
                    Rect rect = listing.GetRect(24f);
                    DoAreaRowMethod.Invoke(__instance, new object[] { rect, allAreas[i], i });
                    listing.Gap(6f);
                    visibleRows++;
                }
            }

            string renameLabel = GetRenameButtonLabel();
            float renameWidth = Text.CalcSize(renameLabel).x + RenameButtonHorizontalPadding;

            if (map.areaManager.CanMakeNewAllowed())
            {
                for (; visibleRows < 8; visibleRows++)
                {
                    listing.Gap(30f);
                }

                Rect rowRect = listing.GetRect(30f);
                const float gap = 10f;

                Rect renameRect = new Rect(rowRect.x, rowRect.y, renameWidth, rowRect.height);
                Rect newAreaRect = new Rect(rowRect.x + renameWidth + gap, rowRect.y, rowRect.width - renameWidth - gap, rowRect.height);

                if (Widgets.ButtonText(renameRect, renameLabel))
                {
                    Find.WindowStack.Add(new Dialog_RenameHomeAndUnrestrictedAreas());
                }

                if (Widgets.ButtonText(newAreaRect, "NewArea".Translate()))
                {
                    map.areaManager.TryMakeNewAllowed(out var _);
                }
            }
            else
            {
                listing.Gap(6f);
                Rect rowRect = listing.GetRect(30f);
                Rect renameRect = new Rect(rowRect.x, rowRect.y, renameWidth, rowRect.height);

                if (Widgets.ButtonText(renameRect, renameLabel))
                {
                    Find.WindowStack.Add(new Dialog_RenameHomeAndUnrestrictedAreas());
                }
            }

            listing.End();
            return false;
        }
    }

    [HarmonyPatch(typeof(Area_Home), "Label", MethodType.Getter)]
    internal static class Patch_AreaHome_Label
    {
        public static bool Prefix(ref string __result)
        {
            if (!RenameHomeAndUnrestrictedAreasSettings.HasCustomHomeLabel)
            {
                return true;
            }

            __result = RenameHomeAndUnrestrictedAreasSettings.HomeLabel;
            return false;
        }
    }

    [HarmonyPatch(typeof(AreaUtility), nameof(AreaUtility.AreaAllowedLabel_Area))]
    internal static class Patch_AreaUtility_AreaAllowedLabel_Area
    {
        public static void Postfix(Area area, ref string __result)
        {
            if (area == null && RenameHomeAndUnrestrictedAreasSettings.HasCustomUnrestrictedLabel)
            {
                __result = RenameHomeAndUnrestrictedAreasSettings.UnrestrictedLabel;
            }
        }
    }
}
