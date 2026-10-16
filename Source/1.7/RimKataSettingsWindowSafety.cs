using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataSettingsWindowSafety
    {
        private static readonly List<string> pendingMessages = new List<string>();

        internal static bool IsGuiExit(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
                if (current is ExitGUIException) return true;
            return false;
        }

        internal static bool Run(string operation, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception exception) when (!IsGuiExit(exception))
            {
                Report(operation, exception);
                return false;
            }
        }

        internal static void Report(string operation, Exception exception)
        {
            Log.Error("[RimKata] Settings operation failed (" + operation + ").\n" + exception);
            ShowPending(exception);
        }

        internal static void ShowPending(Exception exception = null)
        {
            try
            {
                if (exception != null)
                {
                    string message = exception.GetBaseException().Message;
                    if (!pendingMessages.Contains(message)) pendingMessages.Add(message);
                }

                if (pendingMessages.Count == 0) return;
                WindowStack windowStack = Find.WindowStack;
                if (windowStack == null) return;

                string combinedMessage = string.Join("\n\n", pendingMessages);
                // Consume once even if the dialog fails; every full error is already logged.
                pendingMessages.Clear();
                windowStack.Add(new Dialog_MessageBox(
                    "KRWF_RimKata_SettingsOperationFailed".Translate(combinedMessage)));
            }
            catch (Exception notificationException) when (!IsGuiExit(notificationException))
            {
                // An unavailable error dialog must not block the original window's removal.
                Log.Error("[RimKata] Could not display the settings error message.\n" + notificationException);
            }
        }
    }

    public abstract class RimKataSettingsWindow : Window
    {
        public sealed override void PostClose()
        {
            base.PostClose();
            // WindowStack has already removed this window. Never retry a partial commit here.
            RimKataSettingsWindowSafety.Run(GetType().Name, OnSettingsClosed);
        }

        protected abstract void OnSettingsClosed();
    }
}
