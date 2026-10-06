using System;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal sealed class Dialog_RimKataEquipmentRefresh : Dialog_MessageBox
    {
        private const string LoadingText = "loading...";
        private const float LoadingFrameSeconds = 0.15f;

        private RimKataEquipmentRefreshOperation operation;
        private Vector2 messageScrollPosition;
        private float loadingStartedAt;
        private string loadingMessage = string.Empty;
        private string failure;
        private int loadingFrame;
        private bool commitRequested;
        private bool disposed;

        internal Dialog_RimKataEquipmentRefresh()
            : base(string.Empty, "Confirm".Translate(), null, "Close".Translate())
        {
            doCloseX = true;
            closeOnAccept = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize
        {
            get
            {
                Vector2 size = base.InitialSize;
                size.y *= 0.5f;
                return size;
            }
        }

        private bool CanConfirm => !disposed && !commitRequested && operation != null
            && operation.Complete && string.IsNullOrEmpty(failure) && string.IsNullOrEmpty(operation.Error);

        public override void PreOpen()
        {
            base.PreOpen();
            loadingStartedAt = Time.realtimeSinceStartup;
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (disposed || !string.IsNullOrEmpty(failure))
                return;

            try
            {
                if (operation == null)
                    operation = new RimKataEquipmentRefreshOperation();

                if (commitRequested)
                {
                    commitRequested = false;
                    if (CanConfirm)
                    {
                        operation.Commit();
                        Close();
                    }
                    return;
                }

                if (!operation.Complete && string.IsNullOrEmpty(operation.Error))
                    operation.Step();

                int nextFrame = (int)((Time.realtimeSinceStartup - loadingStartedAt) / LoadingFrameSeconds)
                    % (LoadingText.Length + 1);
                if (nextFrame != loadingFrame)
                {
                    loadingFrame = nextFrame;
                    loadingMessage = LoadingText.Substring(0, loadingFrame);
                }
            }
            catch (Exception exception)
            {
                failure = string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message;
                Log.Error("[RimKata] Equipment refresh failed: " + exception);
            }
        }

        public override void OnAcceptKeyPressed()
        {
            if (CanConfirm)
                commitRequested = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            const float buttonHeight = 35f;
            const float buttonGap = 20f;
            Rect messageArea = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - buttonHeight - 5f);
            string error = !string.IsNullOrEmpty(failure) ? failure : operation?.Error;
            string message = !string.IsNullOrEmpty(error)
                ? "KRWF_RimKata_EquipmentRefreshFailed".Translate(error).ToString()
                : operation != null && operation.Complete
                    ? "KRWF_RimKata_EquipmentRefreshReady".Translate("Confirm".Translate().ToString()).ToString()
                    : loadingMessage;
            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            bool previousEnabled = GUI.enabled;
            try
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                float textHeight = Text.CalcHeight(message, messageArea.width);
                if (textHeight <= messageArea.height)
                {
                    float textY = Mathf.Min(inRect.center.y - textHeight * 0.5f, messageArea.yMax - textHeight);
                    Widgets.Label(new Rect(messageArea.x, textY, messageArea.width, textHeight), message);
                }
                else
                {
                    float textWidth = Mathf.Max(1f, messageArea.width - 16f);
                    Rect viewRect = new Rect(0f, 0f, textWidth, Text.CalcHeight(message, textWidth));
                    Widgets.BeginScrollView(messageArea, ref messageScrollPosition, viewRect);
                    Widgets.Label(viewRect, message);
                    Widgets.EndScrollView();
                }

                float buttonWidth = (inRect.width - buttonGap) * 0.5f;
                Rect closeRect = new Rect(inRect.x, inRect.yMax - buttonHeight, buttonWidth, buttonHeight);
                if (Widgets.ButtonText(closeRect, "Close".Translate()))
                {
                    Close();
                    return;
                }
                GUI.enabled = previousEnabled && CanConfirm;
                if (Widgets.ButtonText(new Rect(closeRect.xMax + buttonGap, closeRect.y, buttonWidth, buttonHeight),
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
            disposed = true;
            commitRequested = false;
            try
            {
                operation?.Dispose();
            }
            finally
            {
                operation = null;
                base.PostClose();
            }
        }
    }
}
