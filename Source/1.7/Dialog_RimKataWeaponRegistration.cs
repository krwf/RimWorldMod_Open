using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        private readonly string[] rows;
        private readonly float[] rowHeights;
        private readonly float[] rowOffsets;
        private readonly bool[] succeeded;
        private readonly bool[] saved;
        private Vector2 scroll;
        private string message;
        private int completed;
        private int registered;
        private int unsaved;
        private long copiedMethods, linkedCalls, originalCalls;
        private float measuredWidth, headerHeight, contentHeight;
        private bool layoutDirty = true;
        private bool finalized;
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
            rows = (string[])labels.Clone();
            rowHeights = new float[plans.Length];
            rowOffsets = new float[plans.Length];
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
            // Keep progress/cancel responsive without making every cheap skip wait a full frame.
            long started = Stopwatch.GetTimestamp();
            int processed = 0;
            do
            {
                RegisterNext();
                processed++;
            } while (completed < plans.Length && processed < 8
                && (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency < 4.0);
            if (completed == plans.Length)
            {
                running = false;
                FinishRegistration();
            }
            UpdateMessage();
        }

        private void RegisterNext()
        {
            RimKataWeaponExecutionPlan plan = plans[completed];
            try
            {
                if (RimKataWeaponExecutionPlans.IsCurrent(plan))
                {
                    succeeded[completed] = RimKataWeaponExecutionRuntime.Register(plan, out var metrics);
                    if (succeeded[completed])
                    {
                        copiedMethods += metrics.methods;
                        linkedCalls += metrics.linkedCalls;
                        originalCalls += metrics.originalCalls;
                    }
                }
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
            if (!succeeded[completed])
            {
                RimKataWeaponRegistrationFailures.MarkDisplayed(plan.Def);
                SetRow(completed, "KRWF_RimKata_WeaponRegistrationFailure");
            }
            if (succeeded[completed]) registered++;
            completed++;
        }

        private void UpdateMessage()
        {
            if (completed == plans.Length)
                message = "KRWF_RimKata_WeaponRegistrationComplete".Translate(
                    registered, completed - registered, registered - unsaved, unsaved).ToString();
            else
            {
                message = (running
                    ? "KRWF_RimKata_WeaponRegistrationProgress".Translate(completed, plans.Length)
                    : "KRWF_RimKata_WeaponRegistrationPrompt".Translate()).ToString()
                    + "\n\n" + "KRWF_RimKata_WeaponRegistrationExplanation".Translate();
            }
            headerHeight = 0f;
            layoutDirty = true;
        }

        private void SetRow(int index, string status)
        {
            rows[index] = labels[index] + "\n" + status.Translate();
            rowHeights[index] = 0f;
            layoutDirty = true;
        }

        private void FinishRegistration()
        {
            if (finalized) return;
            finalized = true;
            try { RimKataWeaponExecutionRuntime.SaveRegistrationDecisions(plans); }
            catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
            {
                foreach (var plan in plans) plan.CacheStored = false;
                Log.Warning("[RimKata] Could not save firing registration decisions. Prepared executions remain available: " + exception);
            }
            for (int i = 0; i < completed; i++)
            {
                saved[i] = succeeded[i] && RimKataWeaponExecutionPlans.IsCurrent(plans[i]) && plans[i].CacheStored;
                if (!succeeded[i]) continue;
                if (!saved[i]) unsaved++;
                SetRow(i, saved[i] ? "KRWF_RimKata_WeaponRegistrationSuccess" : "KRWF_RimKata_WeaponRegistrationNotSaved");
            }
            Log.Message("[RimKata] Firing registration " + (completed == plans.Length ? "completed" : "closed")
                + ": prepared=" + registered + ", failed=" + (completed - registered)
                + ", saved=" + (registered - unsaved) + ", unsaved=" + unsaved
                + ", unprocessed=" + (plans.Length - completed) + ", copied_methods=" + copiedMethods
                + ", linked_calls=" + linkedCalls + ", original_calls=" + originalCalls + ".");
        }

        private void MeasureRows(float width)
        {
            if (measuredWidth != width)
            {
                measuredWidth = width;
                Array.Clear(rowHeights, 0, rowHeights.Length);
                headerHeight = 0f;
                layoutDirty = true;
            }
            if (!layoutDirty) return;
            if (headerHeight == 0f) headerHeight = Text.CalcHeight(message, width);
            contentHeight = headerHeight;
            for (int i = 0; i < rows.Length; i++)
            {
                if (completed == plans.Length && saved[i]) continue;
                if (rowHeights[i] == 0f) rowHeights[i] = Text.CalcHeight(rows[i], width);
                contentHeight += Text.LineHeight;
                rowOffsets[i] = contentHeight;
                contentHeight += rowHeights[i];
            }
            layoutDirty = false;
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
                MeasureRows(width);
                var content = new Rect(0f, 0f, width, contentHeight);
                Widgets.BeginScrollView(area, ref scroll, content);
                if (scroll.y < headerHeight)
                    Widgets.Label(new Rect(0f, 0f, width, headerHeight), message);
                for (int i = 0; i < rows.Length; i++)
                {
                    if (completed == plans.Length && saved[i]) continue;
                    if (rowOffsets[i] > scroll.y + area.height) break;
                    if (rowOffsets[i] + rowHeights[i] < scroll.y) continue;
                    Widgets.Label(new Rect(0f, rowOffsets[i], width, rowHeights[i]), rows[i]);
                }
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
                    {
                        try { RimKataWeaponExecutionRuntime.Decline(plans[i]); }
                        catch (Exception exception) when (!RimKataSettingsWindowSafety.IsGuiExit(exception))
                        { Log.Warning("[RimKata] Could not finish the original-firing decision for " + plans[i].Def.defName + ": " + exception); }
                    }
                FinishRegistration();
            }
            base.PostClose();
            ShowRequested();
            RimKataWeaponRegistrationFailures.ShowPending();
        }
    }
}

