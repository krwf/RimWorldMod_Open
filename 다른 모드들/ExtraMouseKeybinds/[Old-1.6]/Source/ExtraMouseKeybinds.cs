using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using UnityEngine;
using Verse;
using Verse.Sound;
using Verse.Steam;

namespace ExtraMouseKeybinds
{
    [Flags]
    public enum ExtraModifier
    {
        Default = 0,
        Ctrl = 1,
        Alt = 2,
        Shift = 4
    }
    public static class ExtraBindingModifierState
    {
        public static Dictionary<string, ExtraModifier> WorkingModifiers;

        public static void BeginEditing()
        {
            ExtraMouseKeybindsSettings settings = ExtraMouseKeybindsMod.Settings;

            WorkingModifiers = settings?.bindingModifiers != null
                ? new Dictionary<string, ExtraModifier>(settings.bindingModifiers)
                : new Dictionary<string, ExtraModifier>();
        }

        public static void ClearWorking()
        {
            if (WorkingModifiers == null)
            {
                BeginEditing();
            }

            WorkingModifiers.Clear();
        }

        public static void Commit()
        {
            ExtraMouseKeybindsSettings settings = ExtraMouseKeybindsMod.Settings;

            if (settings == null)
            {
                return;
            }

            settings.bindingModifiers = WorkingModifiers != null
                ? new Dictionary<string, ExtraModifier>(WorkingModifiers)
                : new Dictionary<string, ExtraModifier>();

            Patch_KeyBindingDef_KeyDownEvent.InvalidateManagedKeyCache();
            settings.Write();
        }

        public static ExtraModifier Get(KeyBindingDef keyDef, KeyPrefs.BindingSlot slot)
        {
            Dictionary<string, ExtraModifier> source = WorkingModifiers ?? ExtraMouseKeybindsMod.Settings?.bindingModifiers;

            if (source == null)
            {
                return ExtraModifier.Default;
            }

            string id = Patch_DialogDefineBinding_DoWindowContents.MakeBindingId(keyDef, slot);

            return source.TryGetValue(id, out ExtraModifier modifier)
                ? modifier
                : ExtraModifier.Default;
        }

        public static void Set(KeyBindingDef keyDef, KeyPrefs.BindingSlot slot, ExtraModifier modifier)
        {
            if (WorkingModifiers == null)
            {
                BeginEditing();
            }

            string id = Patch_DialogDefineBinding_DoWindowContents.MakeBindingId(keyDef, slot);

            if (modifier == ExtraModifier.Default)
            {
                WorkingModifiers.Remove(id);
            }
            else
            {
                WorkingModifiers[id] = modifier;
            }
        }
    }
    public class ExtraMouseKeybindsSettings : ModSettings
    {
        public const int CurrentSettingsVersion = 1;

        public int settingsVersion = CurrentSettingsVersion;

        public bool dollyMouse0 = false;
        public bool dollyMouse1 = false;
        public bool dollyMouse2 = true;
        public bool dollyMouse3 = true;
        public bool dollyMouse4 = true;
        public bool dollyMouse5 = false;
        public bool dollyMouse6 = false;

        public KeyCode dollyKeyA = KeyCode.None;
        public KeyCode dollyKeyB = KeyCode.None;

        public bool closeTabMouse3 = false;
        public bool closeTabMouse4 = false;
        public bool closeTabMouse5 = false;
        public bool closeTabMouse6 = false;

        public bool holdMouse3 = false;
        public bool holdMouse4 = false;
        public bool holdMouse5 = false;
        public bool holdMouse6 = false;

        public Dictionary<string, ExtraModifier> bindingModifiers = new Dictionary<string, ExtraModifier>();

        public override void ExposeData()
        {
            Scribe_Values.Look(
                ref settingsVersion,
                "settingsVersion",
                CurrentSettingsVersion,
                forceSave: true
            );

            Scribe_Values.Look(ref dollyMouse0, "dollyMouse0", false);
            Scribe_Values.Look(ref dollyMouse1, "dollyMouse1", false);
            Scribe_Values.Look(ref dollyMouse2, "dollyMouse2", true);
            Scribe_Values.Look(ref dollyMouse3, "dollyMouse3", true);
            Scribe_Values.Look(ref dollyMouse4, "dollyMouse4", true);
            Scribe_Values.Look(ref dollyMouse5, "dollyMouse5", false);
            Scribe_Values.Look(ref dollyMouse6, "dollyMouse6", false);

            Scribe_Values.Look(ref dollyKeyA, "dollyKeyA", KeyCode.None);
            Scribe_Values.Look(ref dollyKeyB, "dollyKeyB", KeyCode.None);

            Scribe_Values.Look(ref closeTabMouse3, "closeTabMouse3", false);
            Scribe_Values.Look(ref closeTabMouse4, "closeTabMouse4", false);
            Scribe_Values.Look(ref closeTabMouse5, "closeTabMouse5", false);
            Scribe_Values.Look(ref closeTabMouse6, "closeTabMouse6", false);

            Scribe_Values.Look(ref holdMouse3, "holdMouse3", false);
            Scribe_Values.Look(ref holdMouse4, "holdMouse4", false);
            Scribe_Values.Look(ref holdMouse5, "holdMouse5", false);
            Scribe_Values.Look(ref holdMouse6, "holdMouse6", false);

            Scribe_Collections.Look(
                ref bindingModifiers,
                "bindingModifiers",
                LookMode.Value,
                LookMode.Value
                );

            if (Scribe.mode == LoadSaveMode.PostLoadInit && bindingModifiers == null)
            {
                bindingModifiers = new Dictionary<string, ExtraModifier>();
            }
        }
    }

    internal static class ExtraMouseInputState
    {
        private static int sampledFrame = -1;
        private static int sampledButtons;
        private static bool anyKeyActive;

        public static int DownButtons { get; private set; }
        public static int HeldButtons { get; private set; }
        public static bool AnyInput => anyKeyActive || DownButtons != 0 || HeldButtons != 0;

        public static int Refresh(int mouseButtonMask)
        {
            int frame = Time.frameCount;

            if (sampledFrame != frame)
            {
                sampledFrame = frame;
                sampledButtons = 0;
                DownButtons = 0;
                HeldButtons = 0;
                anyKeyActive = Input.anyKey || Input.anyKeyDown;
            }

            int missingButtons = mouseButtonMask & ~sampledButtons & 0x7f;

            for (int button = 0; missingButtons != 0; button++)
            {
                int bit = 1 << button;

                if ((missingButtons & bit) == 0)
                {
                    continue;
                }

                // Keep direct mouse sampling as a fallback for extra buttons and short clicks.
                if (Input.GetMouseButtonDown(button))
                {
                    DownButtons |= bit;
                }

                if (Input.GetMouseButton(button))
                {
                    HeldButtons |= bit;
                }

                sampledButtons |= bit;
                missingButtons &= ~bit;
            }

            return frame;
        }
    }

    public class ExtraMouseKeybindsMod : Mod
    {
        public static ExtraMouseKeybindsSettings Settings;

        private readonly Listing_Standard settingsListing = new Listing_Standard { maxOneColumn = true };
        private Vector2 settingsScrollPosition;
        private Vector2 settingsViewportSize = new Vector2(-1f, -1f);
        private object settingsLayoutLanguage;
        private float settingsContentWidth;
        private float settingsContentHeight;
        private float settingsResetWidth;
        private bool settingsNeedScroll;

        private static readonly Dictionary<int, float> mouseNextRepeatTime = new Dictionary<int, float>();

        private const float FallbackInitialRepeatDelaySeconds = 0.5f;
        private const float FallbackRepeatIntervalSeconds = 0.05f;
        private static float initialRepeatDelaySeconds = FallbackInitialRepeatDelaySeconds;
        private static float repeatIntervalSeconds = FallbackRepeatIntervalSeconds;

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardParameter(uint action, uint parameter, out uint value, uint flags);

        public static void RefreshKeyboardRepeatSettings()
        {
            initialRepeatDelaySeconds = FallbackInitialRepeatDelaySeconds;
            repeatIntervalSeconds = FallbackRepeatIntervalSeconds;

            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
            {
                return;
            }

            try
            {
                const uint GetKeyboardDelay = 0x0016;
                const uint GetKeyboardSpeed = 0x000A;

                if (GetKeyboardParameter(GetKeyboardDelay, 0, out uint delay, 0) && delay <= 3 &&
                    GetKeyboardParameter(GetKeyboardSpeed, 0, out uint speed, 0) && speed <= 31)
                {
                    initialRepeatDelaySeconds = (delay + 1) * 0.25f;
                    // Windows exposes a scaled speed setting, not an exact hardware interval.
                    repeatIntervalSeconds = 1f / (2.5f + speed * (27.5f / 31f));
                }
            }
            catch (DllNotFoundException)
            {
                // Other platforms or unavailable native APIs retain the previous defaults.
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        public ExtraMouseKeybindsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ExtraMouseKeybindsSettings>();
            RefreshKeyboardRepeatSettings();
            new Harmony("KRWF.extramousekeybinds").PatchAll();
        }

        private void CheckboxLeft(Listing_Standard listing, string label, ref bool value)
        {
            float rowHeight = Mathf.Max(24f, Text.CalcHeight(label, Mathf.Max(1f, listing.ColumnWidth - 30f)));
            Rect row = listing.GetRect(rowHeight);

            Rect checkboxRect = new Rect(row.x, row.y, 24f, 24f);
            Rect labelRect = new Rect(row.x + 30f, row.y, row.width - 30f, row.height);

            Widgets.CheckboxDraw(
                checkboxRect.x,
                checkboxRect.y,
                value,
                disabled: false
            );

            Widgets.Label(labelRect, label);

            if (Widgets.ButtonInvisible(row))
            {
                value = !value;

                if (value)
                    SoundDefOf.Checkbox_TurnedOn.PlayOneShotOnCamera();
                else
                    SoundDefOf.Checkbox_TurnedOff.PlayOneShotOnCamera();
            }
        }

        public static bool ShouldIgnoreTabClose(int button)
        {
            ExtraMouseKeybindsSettings s = ExtraMouseKeybindsMod.Settings;

            switch (button)
            {
                case 2: return true;
                case 3: return s?.closeTabMouse3 ?? false;
                case 4: return s?.closeTabMouse4 ?? false;
                case 5: return s?.closeTabMouse5 ?? false;
                case 6: return s?.closeTabMouse6 ?? false;
                default: return false;
            }
        }

        public static bool MouseButtonInputActive(int button)
        {
            if (button < 0 || button > 6)
            {
                return false;
            }

            int bit = 1 << button;
            ExtraMouseInputState.Refresh(bit);
            return MouseButtonInputActive(
                button,
                (ExtraMouseInputState.DownButtons & bit) != 0,
                (ExtraMouseInputState.HeldButtons & bit) != 0
            );
        }

        internal static void ClearMouseRepeatState()
        {
            if (mouseNextRepeatTime.Count != 0)
            {
                mouseNextRepeatTime.Clear();
            }
        }

        internal static void PruneMouseRepeatState(int mouseButtonMask)
        {
            for (int button = 3; button <= 6; button++)
            {
                if ((mouseButtonMask & (1 << button)) == 0)
                {
                    mouseNextRepeatTime.Remove(button);
                }
            }
        }

        internal static bool MouseButtonInputActive(int button, bool pressed, bool held)
        {
            ExtraMouseKeybindsSettings s = ExtraMouseKeybindsMod.Settings;

            bool hold =
                button == 3 ? s?.holdMouse3 ?? false :
                button == 4 ? s?.holdMouse4 ?? false :
                button == 5 ? s?.holdMouse5 ?? false :
                button == 6 ? s?.holdMouse6 ?? false :
                false;

            if (!hold)
            {
                mouseNextRepeatTime.Remove(button);
                return pressed;
            }

            if (pressed)
            {
                mouseNextRepeatTime[button] = Time.unscaledTime + initialRepeatDelaySeconds;
                return true;
            }

            if (!held)
            {
                mouseNextRepeatTime.Remove(button);
                return false;
            }

            if (!mouseNextRepeatTime.TryGetValue(button, out float nextRepeatTime))
            {
                mouseNextRepeatTime[button] = Time.unscaledTime + initialRepeatDelaySeconds;
                return false;
            }

            float now = Time.unscaledTime;

            if (now < nextRepeatTime)
            {
                return false;
            }

            do
            {
                nextRepeatTime += repeatIntervalSeconds;
            }
            while (nextRepeatTime <= now);

            mouseNextRepeatTime[button] = nextRepeatTime;
            return true;
        }

        private void DrawDivider(Listing_Standard listing)
        {
            listing.Gap(4f);
            Rect rect = listing.GetRect(1f);
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            listing.Gap(4f);
        }

        public override string SettingsCategory()
        {
            return "KRWF_EMK_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            UpdateSettingsLayout(inRect);
            Listing_Standard listing = settingsListing;
            Rect contentRect = new Rect(0f, 0f, settingsContentWidth, settingsContentHeight);
            if (settingsNeedScroll)
            {
                Widgets.BeginScrollView(inRect, ref settingsScrollPosition, contentRect);
                listing.Begin(contentRect);
            }
            else
            {
                settingsScrollPosition = Vector2.zero;
                listing.Begin(inRect);
            }

            listing.Label("KRWF_EMK_CameraDollyButtons".Translate());

            //listing.Gap(6f);
            //listing.Label("KRWF_EMK_DollyDescription".Translate());

            //listing.Gap(6f);
            CheckboxLeft(listing, "KRWF_EMK_m0Click".Translate(), ref Settings.dollyMouse0);
            CheckboxLeft(listing, "KRWF_EMK_m1Click".Translate(), ref Settings.dollyMouse1);
            CheckboxLeft(listing, "KRWF_EMK_m2Click".Translate(), ref Settings.dollyMouse2);
            CheckboxLeft(listing, "KRWF_EMK_m3Click".Translate(), ref Settings.dollyMouse3);
            CheckboxLeft(listing, "KRWF_EMK_m4Click".Translate(), ref Settings.dollyMouse4);
            CheckboxLeft(listing, "KRWF_EMK_m5Click".Translate(), ref Settings.dollyMouse5);
            CheckboxLeft(listing, "KRWF_EMK_m6Click".Translate(), ref Settings.dollyMouse6);

            listing.Gap(2f);
            Rect resetDollyRect = listing.GetRect(28f);
            resetDollyRect.width = settingsResetWidth;
            if (Widgets.ButtonText(resetDollyRect, "Reset".Translate()))
            {
                Settings.dollyMouse0 = false;
                Settings.dollyMouse1 = false;
                Settings.dollyMouse2 = true;
                Settings.dollyMouse3 = true;
                Settings.dollyMouse4 = true;
                Settings.dollyMouse5 = false;
                Settings.dollyMouse6 = false;

                Settings.Write();
            }

            DrawDivider(listing);

            listing.Label("KRWF_EMK_CameraDollyKeys".Translate());
            Rect keyRow = listing.GetRect(ExtraMouseSettingsLayout.BindingRowHeight);
            float keyWidth = Mathf.Min(
                ExtraMouseSettingsLayout.BindingWidth,
                Mathf.Max(1f, (keyRow.width - ExtraMouseSettingsLayout.BindingGap) / 2f));
            Rect keyA = new Rect(keyRow.x, keyRow.y + 3f, keyWidth, ExtraMouseSettingsLayout.BindingHeight);
            Rect keyB = new Rect(keyA.xMax + ExtraMouseSettingsLayout.BindingGap, keyA.y, keyWidth, keyA.height);
            DrawDollyKeyButton(keyA, true);
            DrawDollyKeyButton(keyB, false);

            listing.Gap(2f);
            Rect resetKeysRect = listing.GetRect(ExtraMouseSettingsLayout.ResetHeight);
            resetKeysRect.width = settingsResetWidth;
            if (Widgets.ButtonText(resetKeysRect, "Reset".Translate()))
            {
                Settings.dollyKeyA = KeyCode.None;
                Settings.dollyKeyB = KeyCode.None;
                Settings.Write();
            }

            DrawDivider(listing);

            listing.Label("KRWF_EMK_DisableTabClosing".Translate());

            //listing.Gap(6f);
            CheckboxLeft(listing, "KRWF_EMK_m3Click".Translate(), ref Settings.closeTabMouse3);
            CheckboxLeft(listing, "KRWF_EMK_m4Click".Translate(), ref Settings.closeTabMouse4);
            CheckboxLeft(listing, "KRWF_EMK_m5Click".Translate(), ref Settings.closeTabMouse5);
            CheckboxLeft(listing, "KRWF_EMK_m6Click".Translate(), ref Settings.closeTabMouse6);

            listing.Gap(2f);
            Rect resetCloseTabRect = listing.GetRect(28f);
            resetCloseTabRect.width = settingsResetWidth;
            if (Widgets.ButtonText(resetCloseTabRect, "Reset".Translate()))
            {
                Settings.closeTabMouse3 = false;
                Settings.closeTabMouse4 = false;
                Settings.closeTabMouse5 = false;
                Settings.closeTabMouse6 = false;

                Settings.Write();
            }

            DrawDivider(listing);

            listing.Label("KRWF_EMK_HoldInputMode".Translate());

            //listing.Gap(6f);
            CheckboxLeft(listing, "KRWF_EMK_m3Click".Translate(), ref Settings.holdMouse3);
            CheckboxLeft(listing, "KRWF_EMK_m4Click".Translate(), ref Settings.holdMouse4);
            CheckboxLeft(listing, "KRWF_EMK_m5Click".Translate(), ref Settings.holdMouse5);
            CheckboxLeft(listing, "KRWF_EMK_m6Click".Translate(), ref Settings.holdMouse6);

            listing.Gap(2f);
            Rect resetHoldRect = listing.GetRect(28f);
            resetHoldRect.width = settingsResetWidth;
            if (Widgets.ButtonText(resetHoldRect, "Reset".Translate()))
            {
                Settings.holdMouse3 = false;
                Settings.holdMouse4 = false;
                Settings.holdMouse5 = false;
                Settings.holdMouse6 = false;

                Settings.Write();
            }

            listing.End();
            if (settingsNeedScroll)
            {
                Widgets.EndScrollView();
            }
        }

        private void UpdateSettingsLayout(Rect inRect)
        {
            Vector2 viewportSize = new Vector2(inRect.width, inRect.height);
            object language = LanguageDatabase.activeLanguage;
            if (settingsViewportSize == viewportSize && ReferenceEquals(settingsLayoutLanguage, language))
            {
                return;
            }

            settingsViewportSize = viewportSize;
            settingsLayoutLanguage = language;
            settingsContentWidth = Mathf.Max(1f, inRect.width);
            settingsContentHeight = ExtraMouseSettingsLayout.GetContentHeight(settingsContentWidth);
            settingsNeedScroll = settingsContentHeight > inRect.height;
            if (settingsNeedScroll)
            {
                settingsContentWidth = Mathf.Max(1f, inRect.width - ExtraMouseSettingsLayout.ScrollbarWidth);
                settingsContentHeight = ExtraMouseSettingsLayout.GetContentHeight(settingsContentWidth);
            }

            settingsResetWidth = Mathf.Min(settingsContentWidth, ExtraMouseSettingsLayout.GetResetWidth());
            settingsScrollPosition.y = Mathf.Clamp(
                settingsScrollPosition.y, 0f, Mathf.Max(0f, settingsContentHeight - inRect.height));
        }

        private void DrawDollyKeyButton(Rect rect, bool primary)
        {
            KeyCode key = primary ? Settings.dollyKeyA : Settings.dollyKeyB;
            TooltipHandler.TipRegionByKey(rect, "BindingButtonToolTip");
            if (!Widgets.ButtonText(rect, key.ToStringReadable()))
            {
                return;
            }

            if (Event.current.button == 1)
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("ResetBinding".Translate(), () => SetDollyKey(primary, KeyCode.None)),
                    new FloatMenuOption("ClearBinding".Translate(), () => SetDollyKey(primary, KeyCode.None))
                }));
            }
            else
            {
                Find.WindowStack.Add(new Dialog_CameraDollyKey(keyCode => SetDollyKey(primary, keyCode)));
            }
        }

        private void SetDollyKey(bool primary, KeyCode key)
        {
            if (primary)
            {
                Settings.dollyKeyA = key;
            }
            else
            {
                Settings.dollyKeyB = key;
            }

            Settings.Write();
        }
    }

    [HarmonyPatch(typeof(Dialog_DefineBinding), nameof(Dialog_DefineBinding.DoWindowContents))]
    public static class Patch_DialogDefineBinding_DoWindowContents    {

        private static readonly AccessTools.FieldRef<Dialog_DefineBinding, KeyPrefsData> KeyPrefsDataRef =
            AccessTools.FieldRefAccess<Dialog_DefineBinding, KeyPrefsData>("keyPrefsData");

        private static readonly AccessTools.FieldRef<Dialog_DefineBinding, KeyBindingDef> KeyDefRef =
            AccessTools.FieldRefAccess<Dialog_DefineBinding, KeyBindingDef>("keyDef");

        private static readonly AccessTools.FieldRef<Dialog_DefineBinding, KeyPrefs.BindingSlot> SlotRef =
            AccessTools.FieldRefAccess<Dialog_DefineBinding, KeyPrefs.BindingSlot>("slot");

        private static void EraseConflictingBinding(
            KeyPrefsData keyPrefsData,
            KeyBindingDef newDef,
            KeyPrefs.BindingSlot newSlot,
            KeyCode newKey,
            ExtraModifier newModifier)
        {
            foreach (KeyBindingDef oldDef in DefDatabase<KeyBindingDef>.AllDefs)
            {
                EraseConflictingSlot(keyPrefsData, newDef, newSlot, oldDef, KeyPrefs.BindingSlot.A, newKey, newModifier);
                EraseConflictingSlot(keyPrefsData, newDef, newSlot, oldDef, KeyPrefs.BindingSlot.B, newKey, newModifier);
            }
        }
                
        private static void EraseConflictingSlot(
            KeyPrefsData keyPrefsData,
            KeyBindingDef newDef,
            KeyPrefs.BindingSlot newSlot,
            KeyBindingDef oldDef,
            KeyPrefs.BindingSlot oldSlot,
            KeyCode newKey,
            ExtraModifier newModifier)
        {
            if (oldDef == newDef && oldSlot == newSlot)
            {
                return;
            }

            KeyCode oldKey = keyPrefsData.GetBoundKeyCode(oldDef, oldSlot);

            if (oldKey != newKey)
            {
                return;
            }

            ExtraModifier oldModifier = ExtraBindingModifierState.Get(oldDef, oldSlot);

            if (oldModifier != newModifier)
            {
                return;
            }

            keyPrefsData.SetBinding(oldDef, oldSlot, KeyCode.None);
            ExtraBindingModifierState.Set(oldDef, oldSlot, ExtraModifier.Default);

            Messages.Message(
                "KeyBindingOverwritten".Translate(oldDef.LabelCap),
                MessageTypeDefOf.TaskCompletion,
                historical: false
            );
        }

        private static int lastBindingHandledFrame = -1;
        private static ExtraModifier selectedModifier = ExtraModifier.Default;
        private static Dialog_DefineBinding currentDialog;

        public static bool Prefix(Dialog_DefineBinding __instance, Rect inRect)
        {
            Event ev = Event.current;

            if (!ReferenceEquals(currentDialog, __instance))
            {
                currentDialog = __instance;
                selectedModifier = ExtraModifier.Default;
                lastBindingHandledFrame = -1;
            }

            if (lastBindingHandledFrame == Time.frameCount)
            {
                if (ev != null)
                {
                    ev.Use();
                }

                return false;
            }

            if (ev != null && ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape)
            {
                selectedModifier = ExtraModifier.Default;
                return true;
            }

            KeyBindingDef keyDef = KeyDefRef(__instance);
            KeyPrefs.BindingSlot slot = SlotRef(__instance);

            Rect modifierRect = GetModifierRect(inRect);

            if (ev != null && ev.type == EventType.MouseDown && modifierRect.Contains(ev.mousePosition))
            {
                return true;
            }

            KeyCode keyCode = DetectInputKey(ev);

            if (keyCode == KeyCode.None)
            {
                return true;
            }

            KeyPrefsData keyPrefsData = KeyPrefsDataRef(__instance);

            ExtraModifier modifierToSave = selectedModifier;

            EraseConflictingBinding(keyPrefsData, keyDef, slot, keyCode, modifierToSave);

            if (!keyPrefsData.SetBinding(keyDef, slot, keyCode))
            {
                return true;
            }

            SaveModifier(keyDef, slot, modifierToSave);

            lastBindingHandledFrame = Time.frameCount;

            if (ev != null)
            {
                ev.Use();
            }

            __instance.Close();

            selectedModifier = ExtraModifier.Default;

            return false;
        }

        public static void Postfix(Rect inRect)
        {
            DrawModifierSelector(inRect);
        }

        private static Rect GetModifierRect(Rect inRect)
        {
            return new Rect(inRect.x + 18f, inRect.yMax - 42f, inRect.width - 36f, 34f);
        }

        private static void DrawModifierSelector(Rect inRect)
        {
            Rect row = GetModifierRect(inRect);

            float gap = 6f;
            float width = (row.width - gap * 3f) / 4f;

            DrawModifierButton(new Rect(row.x, row.y, width, row.height), ExtraModifier.Default, "Default");
            DrawModifierButton(new Rect(row.x + (width + gap), row.y, width, row.height), ExtraModifier.Ctrl, "Ctrl");
            DrawModifierButton(new Rect(row.x + (width + gap) * 2f, row.y, width, row.height), ExtraModifier.Alt, "Alt");
            DrawModifierButton(new Rect(row.x + (width + gap) * 3f, row.y, width, row.height), ExtraModifier.Shift, "Shift");
        }

        private static void DrawModifierButton(Rect rect, ExtraModifier modifier, string label)
        {
            bool selected = modifier == ExtraModifier.Default
                ? selectedModifier == ExtraModifier.Default
                : (selectedModifier & modifier) != 0;

            string text = selected ? "> " + label + " <" : label;

            if (Verse.Widgets.ButtonText(rect, text))
            {
                if (modifier == ExtraModifier.Default)
                {
                    selectedModifier = ExtraModifier.Default;
                }
                else
                {
                    selectedModifier ^= modifier;
                }

                if (Event.current != null)
                {
                    Event.current.Use();
                }
            }
        }

        private static void SaveModifier(KeyBindingDef keyDef, KeyPrefs.BindingSlot slot, ExtraModifier modifier)
        {
            ExtraBindingModifierState.Set(keyDef, slot, modifier);
        }

        public static string MakeBindingId(KeyBindingDef keyDef, KeyPrefs.BindingSlot slot)
        {
            return keyDef.defName + "|" + slot.ToString();
        }

        private static KeyCode DetectInputKey(Event ev)
        {
            if (ev != null)
            {
                if (ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None)
                {
                    if (ev.keyCode == KeyCode.Escape)
                    {
                        return KeyCode.None;
                    }

                    if (IsPureModifier(ev.keyCode))
                    {
                        return KeyCode.None;
                    }

                    return ev.keyCode;
                }

                if (ev.type == EventType.MouseDown)
                {
                    return MouseButtonToKeyCode(ev.button);
                }
            }

            return DetectMouseKeyGlobal();
        }

        private static KeyCode DetectMouseKeyGlobal()
        {
            for (int i = 1; i <= 6; i++)
            {
                if (Input.GetMouseButtonDown(i))
                {
                    return MouseButtonToKeyCode(i);
                }
            }

            return KeyCode.None;
        }

        private static bool IsPureModifier(KeyCode keyCode)
        {
            return keyCode == KeyCode.LeftControl ||
                   keyCode == KeyCode.RightControl ||
                   keyCode == KeyCode.LeftAlt ||
                   keyCode == KeyCode.RightAlt ||
                   keyCode == KeyCode.LeftShift ||
                   keyCode == KeyCode.RightShift;
        }

        private static KeyCode MouseButtonToKeyCode(int button)
        {
            switch (button)
            {
                case 0: return KeyCode.Mouse0;
                case 1: return KeyCode.Mouse1;
                case 2: return KeyCode.Mouse2;
                case 3: return KeyCode.Mouse3;
                case 4: return KeyCode.Mouse4;
                case 5: return KeyCode.Mouse5;
                case 6: return KeyCode.Mouse6;
                default: return KeyCode.None;
            }
        }
    }

    [HarmonyPatch(typeof(KeyBindingDef), "get_KeyDownEvent")]
    public static class Patch_KeyBindingDef_KeyDownEvent
    {
        private const ExtraModifier SupportedModifiers =
            ExtraModifier.Ctrl | ExtraModifier.Alt | ExtraModifier.Shift;

        private static int handledFrame = -1;
        private static readonly HashSet<KeyBindingDef> handledBindings = new HashSet<KeyBindingDef>();

        private static int cachedInputFrame = -1;
        private static bool mouseInputSampled;
        private static readonly HashSet<KeyCode> cachedKeys = new HashSet<KeyCode>();

        private static bool managedKeyCacheValid;
        private static KeyPrefsData cachedKeyPrefsData;
        private static int managedMouseButtonsMask;
        private static int sharedMouseChordButtonsMask;
        private static readonly int[] sharedMouseChordButtonsByModifier = new int[8];
        private static readonly HashSet<KeyCode> modifierManagedKeys = new HashSet<KeyCode>();
        private static readonly Dictionary<KeyBindingDef, RuntimeBinding> allBindings =
            new Dictionary<KeyBindingDef, RuntimeBinding>();
        private static readonly Dictionary<KeyBindingDef, RuntimeBinding> managedBindings =
            new Dictionary<KeyBindingDef, RuntimeBinding>();

        private struct RuntimeBinding
        {
            public KeyCode keyA;
            public KeyCode keyB;
            public ExtraModifier modifierA;
            public ExtraModifier modifierB;
            public bool customA;
            public bool customB;
        }

        public static void InvalidateManagedKeyCache()
        {
            managedKeyCacheValid = false;
            cachedKeyPrefsData = null;
        }

        private static bool AlreadyHandledThisFrame(KeyBindingDef keyDef)
        {
            if (handledFrame != Time.frameCount)
            {
                handledFrame = Time.frameCount;
                handledBindings.Clear();
            }

            return !handledBindings.Add(keyDef);
        }

        private static KeyCode MouseButtonToKeyCode(int button)
        {
            switch (button)
            {
                case 0: return KeyCode.Mouse0;
                case 1: return KeyCode.Mouse1;
                case 2: return KeyCode.Mouse2;
                case 3: return KeyCode.Mouse3;
                case 4: return KeyCode.Mouse4;
                case 5: return KeyCode.Mouse5;
                case 6: return KeyCode.Mouse6;
                default: return KeyCode.None;
            }
        }
        private static bool IsMouseKey(KeyCode keyCode)
        {
            return
                   keyCode == KeyCode.Mouse0 ||
                   keyCode == KeyCode.Mouse1 ||
                   keyCode == KeyCode.Mouse2 ||
                   keyCode == KeyCode.Mouse3 ||
                   keyCode == KeyCode.Mouse4 ||
                   keyCode == KeyCode.Mouse5 ||
                   keyCode == KeyCode.Mouse6;
        }

        private static int MouseKeyToButtonMask(KeyCode keyCode)
        {
            return IsMouseKey(keyCode) ? 1 << ((int)keyCode - (int)KeyCode.Mouse0) : 0;
        }

        private static bool IsRelevantInputKey(KeyCode keyCode)
        {
            // A base key must be checked even without its modifier, to suppress vanilla's false match.
            return modifierManagedKeys.Contains(keyCode) ||
                   (managedMouseButtonsMask & MouseKeyToButtonMask(keyCode)) != 0;
        }

        public static void Postfix(KeyBindingDef __instance, ref bool __result)
        {
            KeyPrefsData data = KeyPrefs.KeyPrefsData;

            if (data == null)
            {
                return;
            }

            EnsureManagedKeyCache(data);

            Event ev = Event.current;

            // No relevant input: do not look up every individual key binding.
            if (!HasRelevantInput(ev) ||
                !managedBindings.TryGetValue(__instance, out RuntimeBinding binding))
            {
                return;
            }

            KeyCode keyA = binding.keyA;
            KeyCode keyB = binding.keyB;

            // Keep keyboard event collection in its original order; mouse chords also use raw input.
            HashSet<KeyCode> currentKeys = DetectCurrentKeys(ev);

            bool matchedCustomA = binding.customA && currentKeys.Contains(keyA);
            bool matchedCustomB = binding.customB && currentKeys.Contains(keyB);

            if (!matchedCustomA && !matchedCustomB)
            {
                return;
            }

            bool customInputBlocked = CustomInputBlocked(ev, keyA, keyB);

            bool customResult =
                !customInputBlocked &&
                ((matchedCustomA && ModifierMatches(binding.modifierA)) ||
                 (matchedCustomB && ModifierMatches(binding.modifierB)));

            bool unmanagedVanillaResult =
                __result &&
                VanillaResultCanComeFromUnmanagedSlot(
                    ev,
                    keyA,
                    keyB,
                    binding.customA,
                    binding.customB
                );

            if (customResult && AlreadyHandledThisFrame(__instance))
            {
                customResult = false;
            }

            __result = unmanagedVanillaResult || customResult;
        }

        private static void EnsureManagedKeyCache(KeyPrefsData data)
        {
            if (managedKeyCacheValid && ReferenceEquals(cachedKeyPrefsData, data))
            {
                return;
            }

            managedKeyCacheValid = false;
            cachedKeyPrefsData = data;
            managedMouseButtonsMask = 0;
            sharedMouseChordButtonsMask = 0;
            Array.Clear(sharedMouseChordButtonsByModifier, 0, sharedMouseChordButtonsByModifier.Length);
            modifierManagedKeys.Clear();
            allBindings.Clear();
            managedBindings.Clear();

            foreach (KeyBindingDef keyDef in DefDatabase<KeyBindingDef>.AllDefs)
            {
                if (!data.keyPrefs.TryGetValue(keyDef, out KeyBindingData keyData))
                {
                    continue;
                }

                RuntimeBinding binding = new RuntimeBinding
                {
                    keyA = keyData.keyBindingA,
                    keyB = keyData.keyBindingB,
                    modifierA = GetSavedModifierUncached(keyDef, KeyPrefs.BindingSlot.A),
                    modifierB = GetSavedModifierUncached(keyDef, KeyPrefs.BindingSlot.B)
                };

                allBindings[keyDef] = binding;

                AddModifierManagedKey(binding.keyA, binding.modifierA);
                AddModifierManagedKey(binding.keyB, binding.modifierB);
                AddSharedMouseChord(binding.keyA, binding.modifierA);
                AddSharedMouseChord(binding.keyB, binding.modifierB);
            }

            foreach (KeyValuePair<KeyBindingDef, RuntimeBinding> pair in allBindings)
            {
                RuntimeBinding binding = pair.Value;

                binding.customA =
                    IsMouseKey(binding.keyA) ||
                    binding.modifierA != ExtraModifier.Default ||
                    modifierManagedKeys.Contains(binding.keyA);

                binding.customB =
                    IsMouseKey(binding.keyB) ||
                    binding.modifierB != ExtraModifier.Default ||
                    modifierManagedKeys.Contains(binding.keyB);

                if (binding.customA || binding.customB)
                {
                    managedBindings[pair.Key] = binding;
                    managedMouseButtonsMask |= MouseKeyToButtonMask(binding.keyA) |
                                               MouseKeyToButtonMask(binding.keyB);
                }
            }

            ExtraMouseKeybindsMod.PruneMouseRepeatState(managedMouseButtonsMask);
            mouseInputSampled = false;
            managedKeyCacheValid = true;
        }

        private static void AddModifierManagedKey(KeyCode keyCode, ExtraModifier modifier)
        {
            if (modifier == ExtraModifier.Default || keyCode == KeyCode.None)
            {
                return;
            }

            modifierManagedKeys.Add(keyCode);
        }

        private static void AddSharedMouseChord(KeyCode keyCode, ExtraModifier modifier)
        {
            // Extra buttons already have raw-input polling. Only registered chords extend it to Mouse0-2.
            int buttonMask = MouseKeyToButtonMask(keyCode) & 0x07;
            int modifierIndex = (int)(modifier & SupportedModifiers);

            if (buttonMask == 0 || modifierIndex == 0)
            {
                return;
            }

            sharedMouseChordButtonsMask |= buttonMask;
            sharedMouseChordButtonsByModifier[modifierIndex] |= buttonMask;
        }

        private static bool HasRelevantInput(Event ev)
        {
            int frame = ExtraMouseInputState.Refresh(managedMouseButtonsMask);

            if (cachedInputFrame != frame)
            {
                cachedInputFrame = frame;
                mouseInputSampled = false;
                cachedKeys.Clear();
            }

            EventType eventType = ev != null ? ev.type : EventType.Ignore;

            // GUI events and already-collected input can outlive a physical press in this frame.
            if (!ExtraMouseInputState.AnyInput &&
                eventType != EventType.KeyDown && eventType != EventType.MouseDown &&
                cachedKeys.Count == 0)
            {
                if (!mouseInputSampled)
                {
                    ExtraMouseKeybindsMod.ClearMouseRepeatState();
                    mouseInputSampled = true;
                }

                return false;
            }

            if (cachedKeys.Count != 0)
            {
                return true;
            }

            if (eventType == EventType.KeyDown &&
                ev.keyCode != KeyCode.None && IsRelevantInputKey(ev.keyCode))
            {
                return true;
            }

            if (eventType == EventType.MouseDown &&
                IsRelevantInputKey(MouseButtonToKeyCode(ev.button)))
            {
                return true;
            }

            // A registered mouse chord can share a click even after another UI consumes the GUI event.
            if ((ExtraMouseInputState.DownButtons & sharedMouseChordButtonsMask) != 0 ||
                ((ExtraMouseInputState.DownButtons | ExtraMouseInputState.HeldButtons) &
                 managedMouseButtonsMask & 0x78) != 0)
            {
                return true;
            }

            if (!mouseInputSampled)
            {
                ExtraMouseKeybindsMod.ClearMouseRepeatState();
                mouseInputSampled = true;
            }

            return false;
        }

        private static HashSet<KeyCode> DetectCurrentKeys(Event ev)
        {
            // HasRelevantInput prepared the frame, but deliberately did not collect GUI events.
            EventType eventType = ev != null ? ev.type : EventType.Ignore;

            if (eventType == EventType.KeyDown)
            {
                KeyCode keyCode = ev.keyCode;

                if (keyCode != KeyCode.None && IsRelevantInputKey(keyCode))
                {
                    cachedKeys.Add(keyCode);
                }
            }
            else if (eventType == EventType.MouseDown)
            {
                KeyCode mouseKey = MouseButtonToKeyCode(ev.button);

                if (mouseKey != KeyCode.None && IsRelevantInputKey(mouseKey))
                {
                    cachedKeys.Add(mouseKey);
                }
            }

            if (!mouseInputSampled)
            {
                mouseInputSampled = true;

                int sharedClicks = ExtraMouseInputState.DownButtons & sharedMouseChordButtonsMask;

                if (sharedClicks != 0)
                {
                    // Exact registered modifiers only: do not revive plain clicks or unrelated chords.
                    sharedClicks &= sharedMouseChordButtonsByModifier[(int)GetHeldModifiers()];

                    for (int i = 0; i <= 2; i++)
                    {
                        if ((sharedClicks & (1 << i)) != 0)
                        {
                            cachedKeys.Add(MouseButtonToKeyCode(i));
                        }
                    }
                }

                for (int i = 3; i <= 6; i++)
                {
                    int bit = 1 << i;

                    if ((managedMouseButtonsMask & bit) != 0 &&
                        ExtraMouseKeybindsMod.MouseButtonInputActive(
                            i,
                            (ExtraMouseInputState.DownButtons & bit) != 0,
                            (ExtraMouseInputState.HeldButtons & bit) != 0))
                    {
                        cachedKeys.Add(MouseButtonToKeyCode(i));
                    }
                }
            }

            return cachedKeys;
        }

        private static bool CustomInputBlocked(Event ev, KeyCode keyA, KeyCode keyB)
        {
            if (Find.WindowStack != null && Find.WindowStack.AnySearchWidgetFocused)
            {
                return true;
            }

            return
                ev != null &&
                ev.command &&
                !IsCommandKey(keyA) &&
                !IsCommandKey(keyB);
        }

        private static bool IsCommandKey(KeyCode keyCode)
        {
            return
                keyCode == KeyCode.LeftCommand ||
                keyCode == KeyCode.RightCommand;
        }

        private static bool VanillaResultCanComeFromUnmanagedSlot(
            Event ev,
            KeyCode keyA,
            KeyCode keyB,
            bool customA,
            bool customB)
        {
            if (ev == null || ev.type != EventType.KeyDown || ev.keyCode == KeyCode.None)
            {
                return false;
            }

            return
                (!customA && ev.keyCode == keyA) ||
                (!customB && ev.keyCode == keyB);
        }

        private static bool ModifierMatches(ExtraModifier savedModifier)
        {
            ExtraModifier saved = savedModifier & SupportedModifiers;
            return saved == GetHeldModifiers();
        }

        private static ExtraModifier GetSavedModifierUncached(
            KeyBindingDef keyDef,
            KeyPrefs.BindingSlot slot)
        {
            ExtraMouseKeybindsSettings settings = ExtraMouseKeybindsMod.Settings;

            if (settings?.bindingModifiers == null || settings.bindingModifiers.Count == 0)
            {
                return ExtraModifier.Default;
            }

            string id = Patch_DialogDefineBinding_DoWindowContents.MakeBindingId(keyDef, slot);

            if (settings.bindingModifiers.TryGetValue(id, out ExtraModifier modifier))
            {
                return modifier;
            }

            return ExtraModifier.Default;
        }

        private static ExtraModifier GetHeldModifiers()
        {
            ExtraModifier result = ExtraModifier.Default;

            if (CtrlHeld())
            {
                result |= ExtraModifier.Ctrl;
            }

            if (AltHeld())
            {
                result |= ExtraModifier.Alt;
            }

            if (ShiftHeld())
            {
                result |= ExtraModifier.Shift;
            }

            return result;
        }

        private static bool CtrlHeld()
        {
            return Input.GetKey(KeyCode.LeftControl) ||
                   Input.GetKey(KeyCode.RightControl);
        }

        private static bool AltHeld()
        {
            return Input.GetKey(KeyCode.LeftAlt) ||
                   Input.GetKey(KeyCode.RightAlt);
        }

        private static bool ShiftHeld()
        {
            return Input.GetKey(KeyCode.LeftShift) ||
                   Input.GetKey(KeyCode.RightShift);
        }
    }

    [HarmonyPatch]
    public static class Patch_KeyPrefs_InvalidateManagedKeyCache
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(KeyPrefsData), nameof(KeyPrefsData.SetBinding));
            yield return AccessTools.Method(typeof(KeyPrefsData), nameof(KeyPrefsData.ResetToDefaults));
            yield return AccessTools.Method(
                typeof(KeyPrefsData),
                nameof(KeyPrefsData.EraseConflictingBindingsForKeyCode)
            );
            yield return AccessTools.Method(
                typeof(KeyPrefsData),
                nameof(KeyPrefsData.AddMissingDefaultBindings)
            );
            yield return AccessTools.Method(typeof(KeyPrefs), nameof(KeyPrefs.Save));
        }

        public static void Postfix()
        {
            Patch_KeyBindingDef_KeyDownEvent.InvalidateManagedKeyCache();
        }
    }

    [HarmonyPatch(typeof(CameraDriver), "CameraDriverOnGUI")]
    public static class Patch_CameraDriver_CameraDriverOnGUI
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mouseDragMethod = AccessTools.Method(typeof(UnityGUIBugsFixer), "MouseDrag");
            MethodInfo extraMouseDragMethod =
                AccessTools.Method(typeof(Patch_CameraDriver_CameraDriverOnGUI), nameof(ExtraMouseDrag));

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, mouseDragMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, extraMouseDragMethod);
                }
                else
                {
                    yield return instruction;
                }
            }
        }

        private static Vector2 lastMousePosition;
        private static bool initialized;

        public static bool ExtraMouseDrag(int button)
        {
            ExtraMouseKeybindsSettings s = ExtraMouseKeybindsMod.Settings;

            if (s == null)
            {
                if (Find.WindowStack.WindowOfType<Dialog_DefineBinding>() != null)
                {
                    return false;
                }

                return UnityGUIBugsFixer.MouseDrag(button);
            }

            int dollyButtons =
                (s.dollyMouse0 ? 1 << 0 : 0) |
                (s.dollyMouse1 ? 1 << 1 : 0) |
                (s.dollyMouse2 ? 1 << 2 : 0) |
                (s.dollyMouse3 ? 1 << 3 : 0) |
                (s.dollyMouse4 ? 1 << 4 : 0) |
                (s.dollyMouse5 ? 1 << 5 : 0) |
                (s.dollyMouse6 ? 1 << 6 : 0);

            ExtraMouseInputState.Refresh(dollyButtons);
            bool held = (ExtraMouseInputState.HeldButtons & dollyButtons) != 0 ||
                        ExtraCameraKeyboardInput.IsHeld(s.dollyKeyA, s.dollyKeyB);

            if (held && Find.WindowStack.WindowOfType<Dialog_DefineBinding>() != null)
            {
                return false;
            }

            // Keep the idle position current so the next press cannot look like a stale drag.
            Vector2 current = Event.current != null
                ? Event.current.mousePosition
                : (Vector2)Input.mousePosition;

            if (!initialized)
            {
                lastMousePosition = current;
                initialized = true;
            }

            bool moved = held && current != lastMousePosition;
            lastMousePosition = current;

            return moved;
        }
    }

    [HarmonyPatch(typeof(Dialog_KeyBindings), "DrawKeyEntry")]
    public static class Patch_Dialog_KeyBindings_DrawKeyEntry_Label
    {
        private static readonly MethodInfo ToStringReadableMethod =
            AccessTools.Method(typeof(GenText), nameof(GenText.ToStringReadable), new[] { typeof(KeyCode) });

        private static readonly MethodInfo FormatBindingLabelMethod =
            AccessTools.Method(typeof(Patch_Dialog_KeyBindings_DrawKeyEntry_Label), nameof(FormatBindingLabel));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int slotIndex = 0;

            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;

                if (instruction.Calls(ToStringReadableMethod))
                {
                    KeyPrefs.BindingSlot slot =
                        slotIndex == 0 ? KeyPrefs.BindingSlot.A : KeyPrefs.BindingSlot.B;

                    yield return new CodeInstruction(OpCodes.Ldarg_1);
                    yield return new CodeInstruction(OpCodes.Ldc_I4, (int)slot);
                    yield return new CodeInstruction(OpCodes.Call, FormatBindingLabelMethod);

                    slotIndex++;
                }
            }
        }

        public static string FormatBindingLabel(string baseText, KeyBindingDef keyDef, KeyPrefs.BindingSlot slot)
        {
            ExtraModifier modifier = GetSavedModifier(keyDef, slot);
            string prefix = string.Empty;

            if ((modifier & ExtraModifier.Ctrl) != 0)
            {
                prefix += "[C]+";
            }

            if ((modifier & ExtraModifier.Alt) != 0)
            {
                prefix += "[A]+";
            }

            if ((modifier & ExtraModifier.Shift) != 0)
            {
                prefix += "[S]+";
            }

            return prefix + baseText;
        }

        private static ExtraModifier GetSavedModifier(KeyBindingDef keyDef, KeyPrefs.BindingSlot slot)
        {
            return ExtraBindingModifierState.Get(keyDef, slot);
        }
    }

    [HarmonyPatch(typeof(GenText), nameof(GenText.ToStringReadable))]
    public static class Patch_GenText_ToStringReadable
    {
        public static void Postfix(KeyCode k, ref string __result)
        {
            switch (k)
            {
                case KeyCode.Mouse0:
                    __result = "[m0]";
                    break;

                case KeyCode.Mouse1:
                    __result = "[m1]";
                    break;

                case KeyCode.Mouse2:
                    __result = "[m2]";
                    break;

                case KeyCode.Mouse3:
                    __result = "[m3]";
                    break;

                case KeyCode.Mouse4:
                    __result = "[m4]";
                    break;

                case KeyCode.Mouse5:
                    __result = "[m5]";
                    break;

                case KeyCode.Mouse6:
                    __result = "[m6]";
                    break;
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_KeyBindings), nameof(Dialog_KeyBindings.DoWindowContents))]
    public static class Patch_Dialog_KeyBindings_DoWindowContents_ModifierLifecycle
    {
        private static readonly MethodInfo ResetToDefaultsMethod =
            AccessTools.Method(typeof(KeyPrefsData), nameof(KeyPrefsData.ResetToDefaults));

        private static readonly MethodInfo SaveMethod =
            AccessTools.Method(typeof(KeyPrefs), nameof(KeyPrefs.Save));

        private static readonly MethodInfo ClearWorkingMethod =
            AccessTools.Method(typeof(ExtraBindingModifierState), nameof(ExtraBindingModifierState.ClearWorking));

        private static readonly MethodInfo CommitMethod =
            AccessTools.Method(typeof(ExtraBindingModifierState), nameof(ExtraBindingModifierState.Commit));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;

                if (instruction.Calls(ResetToDefaultsMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, ClearWorkingMethod);
                }

                if (instruction.Calls(SaveMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, CommitMethod);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_KeyBindings), MethodType.Constructor)]
    public static class Patch_Dialog_KeyBindings_Constructor
    {
        public static void Postfix()
        {
            ExtraBindingModifierState.BeginEditing();
        }
    }

    [HarmonyPatch(typeof(ModMetaData), nameof(ModMetaData.ModIconPath), MethodType.Getter)]
    public static class Patch_ModMetaData_ModIconPath
    {
        public static bool Prefix(ModMetaData __instance, ref string __result)
        {
            if (__instance.PackageId != "KRWF.ExtraMouseKeybinds")
            {
                return true;
            }

            __result = "Icon.png";
            return false;
        }
    }

    [HarmonyPatch(typeof(WorldCameraDriver), "WorldCameraDriverOnGUI")]
    public static class Patch_WorldCameraDriver_WorldCameraDriverOnGUI
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mouseDragMethod = AccessTools.Method(typeof(UnityGUIBugsFixer), "MouseDrag");
            MethodInfo extraMouseDragMethod =
                AccessTools.Method(typeof(Patch_CameraDriver_CameraDriverOnGUI), nameof(Patch_CameraDriver_CameraDriverOnGUI.ExtraMouseDrag));

            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, mouseDragMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, extraMouseDragMethod);
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }

    [HarmonyPatch(typeof(MainTabsRoot), nameof(MainTabsRoot.HandleLowPriorityShortcuts))]
    public static class Patch_MainTabsRoot_HandleLowPriorityShortcuts
    {
        private static readonly MethodInfo EventButtonGetter =
            AccessTools.PropertyGetter(typeof(Event), nameof(Event.button));

        private static readonly MethodInfo ShouldIgnoreTabCloseMethod =
            AccessTools.Method(typeof(ExtraMouseKeybindsMod), nameof(ExtraMouseKeybindsMod.ShouldIgnoreTabClose));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int replacementCount = 0;

            for (int i = 0; i < codes.Count - 2; i++)
            {
                bool isTabCloseButtonCheck =
                    codes[i].Calls(EventButtonGetter) &&
                    codes[i + 1].opcode == OpCodes.Ldc_I4_2 &&
                    (codes[i + 2].opcode == OpCodes.Beq || codes[i + 2].opcode == OpCodes.Beq_S);

                if (!isTabCloseButtonCheck)
                {
                    continue;
                }

                codes[i + 1].opcode = OpCodes.Call;
                codes[i + 1].operand = ShouldIgnoreTabCloseMethod;

                codes[i + 2].opcode =
                    codes[i + 2].opcode == OpCodes.Beq_S
                        ? OpCodes.Brtrue_S
                        : OpCodes.Brtrue;

                replacementCount++;
            }

            if (replacementCount != 1)
            {
                Log.Error(
                    "[ExtraMouseKeybinds] Expected one tab-close button check, found " +
                    replacementCount + "."
                );
            }

            return codes;
        }
    }

    internal static class ExtraCameraKeyboardInput
    {
        private static int sampledFrame = -1;
        private static KeyCode sampledKeyA;
        private static KeyCode sampledKeyB;
        private static bool sampledHeld;

        // The caller refreshes ExtraMouseInputState before checking keyboard input.
        internal static bool IsHeld(KeyCode keyA, KeyCode keyB)
        {
            if (keyA == KeyCode.None && keyB == KeyCode.None)
            {
                return false;
            }

            int frame = Time.frameCount;

            if (sampledFrame == frame && sampledKeyA == keyA && sampledKeyB == keyB)
            {
                return sampledHeld;
            }

            sampledFrame = frame;
            sampledKeyA = keyA;
            sampledKeyB = keyB;
            sampledHeld = ExtraMouseInputState.AnyInput &&
                ((keyA != KeyCode.None && Input.GetKey(keyA)) ||
                 (keyB != KeyCode.None && keyB != keyA && Input.GetKey(keyB)));

            return sampledHeld;
        }
    }

    internal sealed class Dialog_CameraDollyKey : Window
    {
        private readonly Action<KeyCode> onKeyChosen;
        private bool finished;

        public override Vector2 InitialSize => new Vector2(
            Mathf.Min(420f, UI.screenWidth),
            Mathf.Min(180f, UI.screenHeight));

        internal Dialog_CameraDollyKey(Action<KeyCode> onKeyChosen)
        {
            this.onKeyChosen = onKeyChosen ?? throw new ArgumentNullException(nameof(onKeyChosen));
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
            closeOnCancel = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Event ev = Event.current;

            if (!finished && ev != null && ev.type == EventType.KeyDown)
            {
                KeyCode key = ev.keyCode;

                if (key == KeyCode.Escape)
                {
                    finished = true;
                    ev.Use();
                    Close();
                    return;
                }

                // Mouse and joystick codes follow keyboard codes in Unity's KeyCode enum.
                if (key > KeyCode.None && key < KeyCode.Mouse0)
                {
                    finished = true;
                    ev.Use();
                    onKeyChosen(key);
                    Close();
                    return;
                }
            }

            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            bool previousWordWrap = Text.WordWrap;

            try
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.WordWrap = true;
                Widgets.Label(inRect, "PressAnyKeyOrEsc".Translate());
            }
            finally
            {
                Text.Font = previousFont;
                Text.Anchor = previousAnchor;
                Text.WordWrap = previousWordWrap;
            }
        }
    }

    internal static class ExtraMouseSettingsLayout
    {
        internal const float BindingWidth = 140f;
        internal const float BindingHeight = 28f;
        internal const float BindingGap = 4f;
        internal const float BindingRowHeight = 34f;
        internal const float ResetHeight = 28f;
        internal const float ScrollbarWidth = 20f;
        internal const float HeaderHeight = 40f;
        internal const float FooterHeight = 40f;

        private const float WindowMargins = 36f;
        private const float ScreenEdgeGap = 20f;
        private const float TitleRightPadding = 12f;
        private const float WidthRoundingPadding = 4f;
        private const float CheckboxLabelOffset = 30f;
        private const float CheckboxHeight = 24f;
        private const float LabelSpacing = 2f;
        private const float ResetSpacing = 2f;
        private const float DividerHeight = 9f;

        private static readonly string[] SectionLabelKeys =
        {
            "KRWF_EMK_CameraDollyButtons",
            "KRWF_EMK_CameraDollyKeys",
            "KRWF_EMK_DisableTabClosing",
            "KRWF_EMK_HoldInputMode"
        };

        private static readonly string[] CheckboxLabelKeys =
        {
            "KRWF_EMK_m0Click",
            "KRWF_EMK_m1Click",
            "KRWF_EMK_m2Click",
            "KRWF_EMK_m3Click",
            "KRWF_EMK_m4Click",
            "KRWF_EMK_m5Click",
            "KRWF_EMK_m6Click"
        };

        private static readonly AccessTools.FieldRef<Dialog_ModSettings, Mod> ModRef =
            AccessTools.FieldRefAccess<Dialog_ModSettings, Mod>("mod");

        internal static ExtraMouseKeybindsMod GetMod(Dialog_ModSettings window)
        {
            return ModRef(window) as ExtraMouseKeybindsMod;
        }

        internal static float GetResetWidth()
        {
            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Small;
                return Mathf.Max(70f, Mathf.Ceil(Text.CalcSize("Reset".Translate()).x) + 20f);
            }
            finally
            {
                Text.Font = previousFont;
            }
        }

        internal static float GetContentHeight(float width)
        {
            GameFont previousFont = Text.Font;
            bool previousWordWrap = Text.WordWrap;
            try
            {
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                float labelWidth = Mathf.Max(1f, width);
                float checkboxLabelWidth = Mathf.Max(1f, width - CheckboxLabelOffset);
                float height = BindingRowHeight + SectionLabelKeys.Length * (ResetSpacing + ResetHeight)
                    + (SectionLabelKeys.Length - 1) * DividerHeight;

                foreach (string key in SectionLabelKeys)
                {
                    height += Text.CalcHeight(key.Translate(), labelWidth) + LabelSpacing;
                }

                for (int i = 0; i < CheckboxLabelKeys.Length; i++)
                {
                    float rowHeight = Mathf.Max(
                        CheckboxHeight,
                        Text.CalcHeight(CheckboxLabelKeys[i].Translate(), checkboxLabelWidth));
                    // Mouse 0-2 occur once; Mouse 3-6 occur in all three mouse sections.
                    height += rowHeight * (i < 3 ? 1 : 3);
                }

                return Mathf.Ceil(height);
            }
            finally
            {
                Text.WordWrap = previousWordWrap;
                Text.Font = previousFont;
            }
        }

        internal static Vector2 GetPreferredWindowSize()
        {
            GameFont previousFont = Text.Font;
            try
            {
                Text.Font = GameFont.Small;
                float contentWidth = Mathf.Max(BindingWidth * 2f + BindingGap, GetResetWidth());
                foreach (string key in SectionLabelKeys)
                {
                    contentWidth = Mathf.Max(contentWidth, Text.CalcSize(key.Translate()).x);
                }

                foreach (string key in CheckboxLabelKeys)
                {
                    contentWidth = Mathf.Max(contentWidth,
                        Text.CalcSize(key.Translate()).x + CheckboxLabelOffset);
                }

                Text.Font = GameFont.Medium;
                contentWidth = Mathf.Max(contentWidth,
                    Text.CalcSize("KRWF_EMK_SettingsCategory".Translate()).x + TitleRightPadding);

                // UI dimensions already account for the user's UI scale.
                float maxWidth = Mathf.Max(1f, UI.screenWidth - ScreenEdgeGap);
                float maxHeight = Mathf.Max(1f, UI.screenHeight - ScreenEdgeGap);
                float width = Mathf.Min(maxWidth,
                    Mathf.Ceil(contentWidth + WidthRoundingPadding) + WindowMargins);
                float height = GetContentHeight(width - WindowMargins)
                    + WindowMargins + HeaderHeight + FooterHeight;

                if (height > maxHeight)
                {
                    // Reserve the scrollbar without narrowing the measured content when space permits.
                    width = Mathf.Min(maxWidth, width + ScrollbarWidth);
                    height = GetContentHeight(width - WindowMargins - ScrollbarWidth)
                        + WindowMargins + HeaderHeight + FooterHeight;
                }

                return new Vector2(width, Mathf.Min(height, maxHeight));
            }
            finally
            {
                Text.Font = previousFont;
            }
        }

        internal static void DrawWindowContents(ExtraMouseKeybindsMod mod, Rect inRect)
        {
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            bool previousWordWrap = Text.WordWrap;
            try
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, Mathf.Max(1f, inRect.width - TitleRightPadding), 35f),
                    mod.SettingsCategory());

                Text.Font = GameFont.Small;
                mod.DoSettingsWindowContents(new Rect(0f, HeaderHeight, inRect.width,
                    Mathf.Max(0f, inRect.height - HeaderHeight - FooterHeight)));
            }
            finally
            {
                Text.WordWrap = previousWordWrap;
                Text.Anchor = previousAnchor;
                Text.Font = previousFont;
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), "get_InitialSize")]
    internal static class Patch_ExtraMouseSettings_InitialSize
    {
        private static void Postfix(Dialog_ModSettings __instance, ref Vector2 __result)
        {
            if (ExtraMouseSettingsLayout.GetMod(__instance) != null)
            {
                __result = ExtraMouseSettingsLayout.GetPreferredWindowSize();
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents))]
    internal static class Patch_ExtraMouseSettings_DoWindowContents
    {
        private static bool Prefix(Dialog_ModSettings __instance, Rect inRect)
        {
            ExtraMouseKeybindsMod mod = ExtraMouseSettingsLayout.GetMod(__instance);
            if (mod == null)
            {
                return true;
            }

            ExtraMouseSettingsLayout.DrawWindowContents(mod, inRect);
            return false;
        }
    }
}
