using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    // Notification data only: do not retain plans, weapon instances or executable bundles.
    internal static class RimKataWeaponRegistrationFailures
    {
        private static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> pending = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool queued;

        internal static void ResetForManualRefresh()
        {
            reported.Clear();
            pending.Clear();
        }

        internal static void Record(ThingDef definition, string reason)
        {
            if (definition?.defName == null || !reported.Add(definition.defName)) return;
            pending[definition.defName] = definition.LabelCap + " [" + definition.defName + "]\n"
                + (definition.modContentPack?.Name ?? "RimWorld");
            // Technical reasons remain in the detailed log; the notification states the outcome.
        }

        internal static void MarkDisplayed(ThingDef definition)
        {
            if (definition?.defName == null) return;
            reported.Add(definition.defName);
            pending.Remove(definition.defName);
        }

        internal static void ShowPending()
        {
            if (queued || pending.Count == 0) return;
            queued = true;
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                queued = false;
                if (pending.Count == 0 || Find.WindowStack == null
                    || Find.WindowStack.IsOpen<Dialog_RimKataWeaponRegistration>()) return;
                var message = new StringBuilder("KRWF_RimKata_WeaponRegistrationFailure".Translate().ToString());
                foreach (string label in pending.Values) message.Append("\n\n").Append(label);
                pending.Clear();
                try { Find.WindowStack.Add(new Dialog_MessageBox(message.ToString())); }
                catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
                {
                    Log.Warning("[RimKata] Could not display weapon registration failures: " + exception);
                }
            });
        }
    }
}
