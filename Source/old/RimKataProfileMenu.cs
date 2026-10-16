using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataProfileMenu
    {
        internal const int MaximumNameLength = 60;

        internal static void Open(RimKataSettings settings, RimKataSettingsUiBuffers buffers)
        {
            if (!RimKataMod.EnsureProfilesInitialized())
            {
                return;
            }

            RimKataProfileStore store = RimKataMod.Profiles;
            var options = new List<FloatMenuOption>();
            foreach (RimKataStoredProfile profile in store.Profiles)
            {
                options.Add(new FloatMenuOption(profile.Name, () =>
                {
                    try
                    {
                        store.Select(settings, profile);
                        buffers.SyncFrom(settings);
                        RimKataMod.ApplyCombatFeatureSettingsChange();
                    }
                    catch (Exception exception)
                    {
                        ShowOperationError(exception);
                    }
                }));
            }

            Action deleteCurrent = store.Profiles.Count > 1 ? () =>
            {
                string profileName = store.Current.Name;
                RimKataConfirmationDialog.Show(
                    "KRWF_RimKata_DeleteProfileConfirmation".Translate(profileName),
                    delegate
                    {
                        try
                        {
                            store.DeleteCurrent(settings);
                            buffers.SyncFrom(settings);
                            RimKataMod.ApplyCombatFeatureSettingsChange();
                        }
                        catch (Exception exception)
                        {
                            ShowOperationError(exception);
                        }
                    });
            } : (Action)null;
            options.Add(new FloatMenuOption("KRWF_RimKata_ProfileDelete".Translate(), deleteCurrent));
            options.Add(new FloatMenuOption("KRWF_RimKata_ProfileAdd".Translate(), () =>
                Find.WindowStack.Add(new Dialog_RimKataProfileName(
                    store, settings, buffers, false, NextDefaultName(store)))));
            options.Add(new FloatMenuOption("KRWF_RimKata_ProfileRename".Translate(), () =>
                Find.WindowStack.Add(new Dialog_RimKataProfileName(
                    store, settings, buffers, true, store.Current.Name))));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static string NextDefaultName(RimKataProfileStore store)
        {
            for (int number = 1; ; number++)
            {
                string name = "KRWF_RimKata_ProfileDefaultName".Translate(number);
                if (!NameExists(store, name, null))
                {
                    return name;
                }
            }
        }

        internal static bool NameExists(RimKataProfileStore store, string name, string excludedId)
        {
            foreach (RimKataStoredProfile profile in store.Profiles)
            {
                if (profile.Id != excludedId
                    && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ShowOperationError(Exception exception)
        {
            Find.WindowStack.Add(new Dialog_MessageBox(
                "KRWF_RimKata_ProfileOperationFailed".Translate(exception.Message)));
        }
    }

    internal sealed class Dialog_RimKataProfileName : Window
    {
        private const string NameControl = "RimKataProfileName";
        private readonly RimKataProfileStore store;
        private readonly RimKataSettings settings;
        private readonly RimKataSettingsUiBuffers buffers;
        private readonly bool rename;
        private readonly string renamedProfileId;
        private string name;
        private string validationError;
        private bool focusPending = true;

        internal Dialog_RimKataProfileName(
            RimKataProfileStore store,
            RimKataSettings settings,
            RimKataSettingsUiBuffers buffers,
            bool rename,
            string initialName)
        {
            this.store = store;
            this.settings = settings;
            this.buffers = buffers;
            this.rename = rename;
            renamedProfileId = rename ? store.Current.Id : null;
            name = initialName;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
            closeOnCancel = true;
        }

        public override Vector2 InitialSize => new Vector2(520f, 260f);

        public override void DoWindowContents(Rect inRect)
        {
            bool accept = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (accept)
            {
                Event.current.Use();
            }

            GameFont previousFont = Text.Font;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f),
                (rename ? "KRWF_RimKata_ProfileRename" : "KRWF_RimKata_ProfileAdd").Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 44f, inRect.width, 25f), "KRWF_RimKata_ProfileName".Translate());
            GUI.SetNextControlName(NameControl);
            name = Widgets.TextField(new Rect(0f, 74f, inRect.width, 32f), name);
            if (focusPending && Event.current.type == EventType.Repaint)
            {
                GUI.FocusControl(NameControl);
                focusPending = false;
            }

            if (!string.IsNullOrEmpty(validationError))
            {
                Color previousColor = GUI.color;
                GUI.color = Color.red;
                Widgets.Label(new Rect(0f, 114f, inRect.width, 50f), validationError);
                GUI.color = previousColor;
            }

            const float buttonGap = 12f;
            float buttonWidth = (inRect.width - buttonGap) * 0.5f;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - 35f, buttonWidth, 35f),
                "KRWF_RimKata_ProfileCancel".Translate()))
            {
                Close();
            }
            else if (Widgets.ButtonText(new Rect(buttonWidth + buttonGap, inRect.height - 35f, buttonWidth, 35f),
                "KRWF_RimKata_ProfileConfirm".Translate()) || accept)
            {
                TryAccept();
            }

            Text.Font = previousFont;
        }

        private void TryAccept()
        {
            string trimmedName = name.Trim();
            if (trimmedName.Length == 0)
            {
                validationError = "KRWF_RimKata_ProfileNameBlank".Translate();
                return;
            }

            if (trimmedName.Length > RimKataProfileMenu.MaximumNameLength)
            {
                validationError = "KRWF_RimKata_ProfileNameTooLong".Translate(RimKataProfileMenu.MaximumNameLength);
                return;
            }

            if (RimKataProfileMenu.NameExists(store, trimmedName, renamedProfileId))
            {
                validationError = "KRWF_RimKata_ProfileNameDuplicate".Translate();
                return;
            }

            try
            {
                if (rename)
                {
                    store.RenameCurrent(settings, trimmedName);
                }
                else
                {
                    store.Add(settings, trimmedName);
                }

                buffers.SyncFrom(settings);
                RimKataMod.RefreshSettingsSnapshot();
            }
            catch (Exception exception)
            {
                RimKataProfileMenu.ShowOperationError(exception);
                return;
            }

            Close();
        }
    }
}
