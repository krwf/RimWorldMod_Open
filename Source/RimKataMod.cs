using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    [StaticConstructorOnStartup]
    public static class RimKataBootstrap
    {
        static RimKataBootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                // Profile initialization refreshes prepared weapons after all
                // mods have finished initializing their runtime definitions.
                if (!RimKataMod.EnsureProfilesInitialized())
                {
                    RimKataPreparedWeaponData.RefreshDefinitions();
                }
            });
            LongEventHandler.ExecuteWhenFinished(() => RimKataActivationSettings.Apply());
            Harmony harmony;
            try
            {
                harmony = new Harmony("krwf.rimkata");
            }
            catch (Exception exception)
            {
                Log.Error("[RimKata] Could not create the Harmony instance.\n" + exception);
                return;
            }

            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception exception)
            {
                Log.Error("[RimKata] Core Harmony patching failed; initialization will continue.\n" + exception);
            }

            try
            {
                Patch_Projectile_Impact_Context.Apply(harmony);
            }
            catch (Exception exception)
            {
                Log.Error("[RimKata] Projectile.Impact patch discovery failed; initialization will continue.\n" + exception);
            }

            LongEventHandler.ExecuteWhenFinished(() => RimKataWeaponRenderProbe.Initialize(harmony));
        }
    }

    public sealed class RimKataMod : Mod
    {
        private sealed class RuntimeSettingsSnapshot
        {
            private readonly RimKataSettingsProfile scalarSettings;
            private readonly bool enableFriendlyPawnEffects;
            private readonly bool enableHostilePawnEffects;
            private readonly float creepJoinerDependencyGeneChancePercent;
            private readonly float creepJoinerRimKataGeneChancePercent;
            private readonly bool enableRimKataA;
            private readonly bool enableRimKataP;
            private readonly bool enableRimKataI;
            private readonly bool enableRimKataG;
            private readonly bool enableSerumDependency;
            private readonly float aiSecondaryWeaponChancePercent;
            private readonly string activeProfileId;
            private readonly string[] enabledWeaponDefNames;
            private readonly string[] enabledArmorDefNames;
            private readonly string[] twoHandWeaponDefNames;
            private readonly string[] oneHandWeaponOverrideDefNames;

            private RuntimeSettingsSnapshot(RimKataSettings settings)
            {
                scalarSettings = RimKataSettingsProfile.Capture(settings);
                enableFriendlyPawnEffects = settings?.enableFriendlyPawnEffects ?? true;
                enableHostilePawnEffects = settings?.enableHostilePawnEffects ?? true;
                creepJoinerDependencyGeneChancePercent = settings?.creepJoinerDependencyGeneChancePercent ?? 0f;
                creepJoinerRimKataGeneChancePercent = settings?.creepJoinerRimKataGeneChancePercent ?? 0f;
                enableRimKataA = settings?.enableRimKataA ?? true;
                enableRimKataP = settings?.enableRimKataP ?? true;
                enableRimKataI = settings?.enableRimKataI ?? true;
                enableRimKataG = settings?.enableRimKataG ?? true;
                enableSerumDependency = settings?.enableSerumDependency ?? true;
                aiSecondaryWeaponChancePercent = settings?.aiSecondaryWeaponChancePercent ?? 0f;
                activeProfileId = settings?.ActiveProfileId;
                enabledWeaponDefNames = CaptureList(settings?.enabledWeaponDefNames);
                enabledArmorDefNames = CaptureList(settings?.enabledArmorDefNames);
                twoHandWeaponDefNames = CaptureList(settings?.twoHandWeaponDefNames);
                oneHandWeaponOverrideDefNames = CaptureList(settings?.oneHandWeaponOverrideDefNames);
            }

            public static RuntimeSettingsSnapshot Capture(RimKataSettings settings)
            {
                return new RuntimeSettingsSnapshot(settings);
            }

            public bool Matches(RimKataSettings settings)
            {
                return settings != null
                    && scalarSettings.Matches(settings)
                    && enableFriendlyPawnEffects == settings.enableFriendlyPawnEffects
                    && enableHostilePawnEffects == settings.enableHostilePawnEffects
                    && creepJoinerDependencyGeneChancePercent == settings.creepJoinerDependencyGeneChancePercent
                    && creepJoinerRimKataGeneChancePercent == settings.creepJoinerRimKataGeneChancePercent
                    && enableRimKataA == settings.enableRimKataA
                    && enableRimKataP == settings.enableRimKataP
                    && enableRimKataI == settings.enableRimKataI
                    && enableRimKataG == settings.enableRimKataG
                    && enableSerumDependency == settings.enableSerumDependency
                    && aiSecondaryWeaponChancePercent == settings.aiSecondaryWeaponChancePercent
                    && string.Equals(activeProfileId, settings.ActiveProfileId, StringComparison.Ordinal)
                    && ListMatches(enabledWeaponDefNames, settings.enabledWeaponDefNames)
                    && ListMatches(enabledArmorDefNames, settings.enabledArmorDefNames)
                    && ListMatches(twoHandWeaponDefNames, settings.twoHandWeaponDefNames)
                    && ListMatches(oneHandWeaponOverrideDefNames, settings.oneHandWeaponOverrideDefNames);
            }

            public void RestoreValues(RimKataSettings settings)
            {
                // List dialogs commit independently and refresh this snapshot.
                // Only scalar controls can still be pending in the main window.
                scalarSettings.ApplyTo(settings);
                settings.enableFriendlyPawnEffects = enableFriendlyPawnEffects;
                settings.enableHostilePawnEffects = enableHostilePawnEffects;
                settings.creepJoinerDependencyGeneChancePercent = creepJoinerDependencyGeneChancePercent;
                settings.creepJoinerRimKataGeneChancePercent = creepJoinerRimKataGeneChancePercent;
                settings.aiSecondaryWeaponChancePercent = aiSecondaryWeaponChancePercent;
            }

            private static string[] CaptureList(List<string> values)
            {
                return values == null
                    ? null
                    : values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            }

            private static bool ListMatches(string[] snapshot, List<string> current)
            {
                if (snapshot == null || current == null)
                {
                    return snapshot == null && current == null;
                }

                if (snapshot.Length != current.Count)
                {
                    return false;
                }

                string[] currentValues = CaptureList(current);
                for (int i = 0; i < snapshot.Length; i++)
                {
                    if (!string.Equals(snapshot[i], currentValues[i], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private static RimKataMod instance;
        private readonly RimKataSettingsUiBuffers uiBuffers = new RimKataSettingsUiBuffers();
        private Vector2 scrollPosition;
        private RuntimeSettingsSnapshot settingsBeforeEdit;
        private bool confirmSettingsOnClose;
        private bool profilesInitializationAttempted;

        public static RimKataSettings Settings { get; private set; }
        internal static RimKataProfileStore Profiles { get; private set; }

        public RimKataMod(ModContentPack content) : base(content)
        {
            instance = this;
            Settings = GetSettings<RimKataSettings>();
            RimKataAllowedWeaponStore.ConfigureRoot(content.RootDir);
            RimKataAllowedWeaponStore.InstallCaptureHook();
            Profiles = new RimKataProfileStore(content.RootDir);
            uiBuffers.SyncFrom(Settings);
        }

        public override string SettingsCategory()
        {
            return "KRWF_RimKata_SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (!profilesInitializationAttempted)
            {
                EnsureProfilesInitialized();
            }

            settingsBeforeEdit ??= RuntimeSettingsSnapshot.Capture(Settings);
            RimKataSettingsDrawer.Draw(inRect, Settings, uiBuffers, ref scrollPosition);
        }

        internal void BeginSettingsWindow()
        {
            confirmSettingsOnClose = false;
            settingsBeforeEdit = null;
            uiBuffers.SyncFrom(Settings);
        }

        internal void PrepareSettingsWindowClose()
        {
            if (!confirmSettingsOnClose && settingsBeforeEdit != null)
            {
                bool friendlyBefore = Settings.enableFriendlyPawnEffects;
                bool hostileBefore = Settings.enableHostilePawnEffects;
                RuntimeSettingsSnapshot discardedValues = RuntimeSettingsSnapshot.Capture(Settings);
                settingsBeforeEdit.RestoreValues(Settings);
                // WriteSettings must also invalidate caches affected by a reverted edit.
                settingsBeforeEdit = discardedValues;
                uiBuffers.SyncFrom(Settings);
                if (friendlyBefore != Settings.enableFriendlyPawnEffects
                    || hostileBefore != Settings.enableHostilePawnEffects)
                {
                    RimKataEligibilityCache.RefreshPermissions();
                    RimKataColonistBarWeaponCache.RefreshAll();
                }
            }

            confirmSettingsOnClose = false;
        }

        internal void DrawSettingsWindowButtons(Rect inRect, Window window)
        {
            const float buttonHeight = 30f;
            const float buttonGap = 8f;
            string closeLabel = "Close".Translate();
            string confirmLabel = "Confirm".Translate();
            float closeWidth = Mathf.Max(90f, Text.CalcSize(closeLabel).x + 28f);
            float confirmWidth = Mathf.Max(90f, Text.CalcSize(confirmLabel).x + 28f);
            float scale = Mathf.Min(1f, Mathf.Max(0f, inRect.width - buttonGap) / (closeWidth + confirmWidth));
            closeWidth *= scale;
            confirmWidth *= scale;
            float x = inRect.x + (inRect.width - closeWidth - buttonGap - confirmWidth) * 0.5f;
            Rect closeRect = new Rect(x, inRect.yMax - buttonHeight, closeWidth, buttonHeight);
            if (Widgets.ButtonText(closeRect, closeLabel))
            {
                window.Close();
            }
            else if (Widgets.ButtonText(new Rect(closeRect.xMax + buttonGap, closeRect.y, confirmWidth, buttonHeight), confirmLabel))
            {
                confirmSettingsOnClose = true;
                window.Close();
            }
        }

        private void PersistSettings()
        {
            Settings.SanitizeCreepJoinerGeneChances();
            if (Profiles?.IsInitialized == true)
            {
                try
                {
                    Profiles.SaveCurrent(Settings);
                }
                catch (Exception exception)
                {
                    ShowProfileError(exception);
                }
            }

            base.WriteSettings();
        }

        public override void WriteSettings()
        {
            PersistSettings();
            bool settingsChanged = settingsBeforeEdit != null
                && !settingsBeforeEdit.Matches(Settings);
            RimKataTargetAccess.Rebuild();
            RimKataEquipmentUtility.InvalidateCaches();
            if (settingsChanged)
            {
                RimKataWeaponSlotUtility.NotifyCombatFeaturesChanged();
            }

            settingsBeforeEdit = null;
        }

        internal static bool EnsureProfilesInitialized()
        {
            if (Profiles?.IsInitialized == true)
            {
                return true;
            }

            if (instance == null || Profiles == null)
            {
                return false;
            }

            instance.profilesInitializationAttempted = true;
            try
            {
                Profiles.Initialize(Settings);
                instance.uiBuffers.SyncFrom(Settings);
                ApplyCombatFeatureSettingsChange();
                return true;
            }
            catch (Exception exception)
            {
                ShowProfileError(exception);
                return false;
            }
        }

        private static void ShowProfileError(Exception exception)
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                "KRWF_RimKata_ProfileOperationFailed".Translate(exception.GetBaseException().Message)));
        }

        internal static void ApplyCombatFeatureSettingsChange()
        {
            RimKataTargetAccess.Rebuild();
            RimKataEquipmentUtility.InvalidateCaches();
            RimKataWeaponSlotUtility.NotifyCombatFeaturesChanged();
            RefreshSettingsSnapshot();
        }

        internal static void CommitListSettings()
        {
            ApplyCombatFeatureSettingsChange();
            instance?.PersistSettings();
        }

        internal static void ApplyEligibilitySettingsChange()
        {
            RimKataTargetAccess.Rebuild();
            RimKataEquipmentUtility.InvalidateCaches();
            RimKataEligibilityCache.RefreshSettings();
            RimKataWeaponSlotUtility.NormalizeAllSpawnedLoadouts();
            RefreshSettingsSnapshot();
        }

        internal static void RefreshSettingsSnapshot()
        {
            if (instance?.settingsBeforeEdit != null)
            {
                instance.settingsBeforeEdit = RuntimeSettingsSnapshot.Capture(Settings);
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), MethodType.Constructor, typeof(Mod))]
    public static class Patch_DialogModSettings_RimKataWindowBehavior
    {
        public static void Postfix(Dialog_ModSettings __instance, Mod mod)
        {
            if (mod is RimKataMod rimKataMod)
            {
                __instance.resizeable = true;
                __instance.draggable = true;
                __instance.doCloseX = false;
                __instance.doCloseButton = false;
                __instance.closeOnAccept = false;
                rimKataMod.BeginSettingsWindow();
            }
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.DoWindowContents))]
    public static class Patch_DialogModSettings_RimKataButtons
    {
        public static void Postfix(Dialog_ModSettings __instance, Mod ___mod, Rect inRect)
        {
            if (___mod is RimKataMod rimKataMod)
                rimKataMod.DrawSettingsWindowButtons(inRect, __instance);
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.PreClose))]
    public static class Patch_DialogModSettings_RimKataConfirm
    {
        public static void Prefix(Mod ___mod)
        {
            if (___mod is RimKataMod rimKataMod)
                rimKataMod.PrepareSettingsWindowClose();
        }
    }

    [HarmonyPatch(typeof(Dialog_ModSettings), nameof(Dialog_ModSettings.InitialSize), MethodType.Getter)]
    public static class Patch_DialogModSettings_RimKataInitialSize
    {
        public static void Postfix(Mod ___mod, ref Vector2 __result)
        {
            if (___mod is RimKataMod)
            {
                __result = RimKataSettingsDrawer.RecommendedWindowSize();
            }
        }
    }
}
