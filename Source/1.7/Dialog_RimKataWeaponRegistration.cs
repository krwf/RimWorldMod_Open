using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal sealed class Dialog_RimKataWeaponRegistration : Window
    {
        private static bool queued;
        private static readonly HashSet<RimKataWeaponExecutionPlan> requested = new HashSet<RimKataWeaponExecutionPlan>();
        private readonly RimKataWeaponExecutionPlan[] plans;
        private readonly string[] labels;
        private readonly bool[] succeeded;
        private readonly bool[] saved;
        private Vector2 scroll;
        private string message;
        private int completed;
        private int registered;
        private int unsaved;
        private bool running;
        private bool closed;

        internal static void ShowPending(IEnumerable<RimKataWeaponExecutionPlan> prepared)
        {
            foreach (var plan in prepared)
                if (NeedsRegistration(plan)) requested.Add(plan);
            ShowRequested();
        }

        private static bool NeedsRegistration(RimKataWeaponExecutionPlan plan)
            => RimKataWeaponExecutionPlans.IsCurrent(plan)
                && plan.Mode == RimKataWeaponExecutionMode.NeedsRegistration;

        private static void ShowRequested()
        {
            if (queued || requested.Count == 0) return;
            queued = true;
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                queued = false;
                if (Find.WindowStack == null || Find.WindowStack.IsOpen<Dialog_RimKataWeaponRegistration>()) return;
                var snapshot = requested.Where(NeedsRegistration).ToArray();
                requested.Clear();
                if (snapshot.Length != 0)
                    Find.WindowStack.Add(new Dialog_RimKataWeaponRegistration(snapshot));
            });
        }

        private Dialog_RimKataWeaponRegistration(RimKataWeaponExecutionPlan[] plans)
        {
            this.plans = plans;
            labels = plans.Select(plan => (plan.Def.LabelCap + " [" + plan.Def.defName + "]"
                + "\n" + (plan.Def.modContentPack?.Name ?? "RimWorld")).ToString()).ToArray();
            succeeded = new bool[plans.Length];
            saved = new bool[plans.Length];
            forcePause = true;
            doCloseX = true;
            closeOnCancel = true;
            closeOnAccept = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            UpdateMessage();
        }

        public override Vector2 InitialSize => new Vector2(700f, 570f);

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (closed || !running) return;
            // Run one registration per update, outside OnGUI and its layout/repaint passes.
            RimKataWeaponExecutionPlan plan = plans[completed];
            try
            {
                if (RimKataWeaponExecutionPlans.IsCurrent(plan))
                    succeeded[completed] = RimKataWeaponExecutionRuntime.Register(plan);
                saved[completed] = succeeded[completed] && plan.CacheStored;
            }
            catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
            {
                // Cleanup failure must not keep the dialog retrying the same weapon forever.
                try { RimKataWeaponExecutionPlans.FailRegistration(plan, exception.Message); }
                catch (Exception cleanup) when (!RimKataSettingsWindowSafety.IsGuiExit(cleanup))
                { Log.Warning("[RimKata] Could not finish weapon registration cleanup: " + cleanup); }
                RimKataWeaponRegistrationFailures.Record(plan.Def, exception.Message);
                Log.Warning("[RimKata] Could not register firing execution for " + plan.Def.defName
                    + ". Original weapon behavior will be used. " + exception);
            }
            if (!succeeded[completed]) RimKataWeaponRegistrationFailures.MarkDisplayed(plan.Def);
            if (succeeded[completed]) registered++;
            if (succeeded[completed] && !saved[completed]) unsaved++;
            completed++;
            if (completed == plans.Length) running = false;
            UpdateMessage();
        }

        private void UpdateMessage()
        {
            var text = new StringBuilder();
            if (completed == plans.Length)
                text.Append("KRWF_RimKata_WeaponRegistrationComplete".Translate(
                    registered, completed - registered, registered - unsaved, unsaved));
            else
            {
                text.Append(running
                    ? "KRWF_RimKata_WeaponRegistrationProgress".Translate(completed, plans.Length)
                    : "KRWF_RimKata_WeaponRegistrationPrompt".Translate());
                text.Append("\n\n").Append("KRWF_RimKata_WeaponRegistrationExplanation".Translate());
            }
            for (int i = 0; i < plans.Length; i++)
            {
                if (completed == plans.Length && saved[i]) continue;
                text.Append("\n\n").Append(labels[i]);
                if (i < completed)
                    text.Append("\n").Append((succeeded[i]
                        ? saved[i] ? "KRWF_RimKata_WeaponRegistrationSuccess" : "KRWF_RimKata_WeaponRegistrationNotSaved"
                        : "KRWF_RimKata_WeaponRegistrationFailure").Translate());
            }
            message = text.ToString();
        }

        public override void OnAcceptKeyPressed()
        {
            if (closed || running) return;
            Event.current?.Use();
            if (completed == plans.Length)
                Close();
            else
            {
                running = true;
                UpdateMessage();
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            const float buttonHeight = 35f;
            const float gap = 20f;
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            bool previousEnabled = GUI.enabled;
            try
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                var area = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - buttonHeight - gap);
                float width = Mathf.Max(1f, area.width - 16f);
                var content = new Rect(0f, 0f, width, Text.CalcHeight(message, width));
                Widgets.BeginScrollView(area, ref scroll, content);
                Widgets.Label(content, message);
                Widgets.EndScrollView();

                float buttonWidth = (inRect.width - gap) * 0.5f;
                var closeRect = new Rect(inRect.x, inRect.yMax - buttonHeight, buttonWidth, buttonHeight);
                if (Widgets.ButtonText(closeRect, "Close".Translate()))
                {
                    Close();
                    return;
                }
                GUI.enabled = previousEnabled && !running;
                if (Widgets.ButtonText(new Rect(closeRect.xMax + gap, closeRect.y, buttonWidth, buttonHeight),
                    "Confirm".Translate()))
                    OnAcceptKeyPressed();
            }
            finally
            {
                GUI.enabled = previousEnabled;
                Text.Font = previousFont;
                Text.Anchor = previousAnchor;
            }
        }

        public override void PostClose()
        {
            if (!closed)
            {
                closed = true;
                running = false;
                // Completed registrations remain accepted; only the untouched remainder is declined.
                for (int i = completed; i < plans.Length; i++)
                    if (RimKataWeaponExecutionPlans.IsCurrent(plans[i]))
                        RimKataWeaponExecutionRuntime.Decline(plans[i]);
            }
            base.PostClose();
            ShowRequested();
            RimKataWeaponRegistrationFailures.ShowPending();
        }
    }
}

