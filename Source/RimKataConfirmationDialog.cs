using System;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    internal static class RimKataConfirmationDialog
    {
        internal static void Show(TaggedString message, Action confirmedAction)
        {
            Find.WindowStack.Add(new Dialog_RimKataConfirmation(message, confirmedAction));
        }

        private sealed class Dialog_RimKataConfirmation : Dialog_MessageBox
        {
            private readonly TaggedString confirmationMessage;
            private Vector2 messageScrollPosition;

            internal Dialog_RimKataConfirmation(TaggedString message, Action confirmedAction)
                : base(message, "Confirm".Translate(), confirmedAction, "Close".Translate(),
                    null, null, false, confirmedAction)
            {
                confirmationMessage = message;
                closeOnClickedOutside = true;
            }

            public override void DoWindowContents(Rect inRect)
            {
                const float buttonHeight = 35f;
                const float buttonGap = 20f;
                Rect messageArea = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - buttonHeight - 5f);
                GameFont previousFont = Text.Font;
                TextAnchor previousAnchor = Text.Anchor;
                try
                {
                    Text.Font = GameFont.Small;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    float textHeight = Text.CalcHeight(confirmationMessage, messageArea.width);
                    if (textHeight <= messageArea.height)
                    {
                        float textY = Mathf.Min(inRect.center.y - textHeight * 0.5f, messageArea.yMax - textHeight);
                        Widgets.Label(new Rect(messageArea.x, textY, messageArea.width, textHeight), confirmationMessage);
                    }
                    else
                    {
                        float textWidth = Mathf.Max(1f, messageArea.width - 16f);
                        Rect viewRect = new Rect(0f, 0f, textWidth, Text.CalcHeight(confirmationMessage, textWidth));
                        Widgets.BeginScrollView(messageArea, ref messageScrollPosition, viewRect);
                        Widgets.Label(viewRect, confirmationMessage);
                        Widgets.EndScrollView();
                    }

                    float buttonWidth = (inRect.width - buttonGap) * 0.5f;
                    Rect closeRect = new Rect(inRect.x, inRect.yMax - buttonHeight, buttonWidth, buttonHeight);
                    if (Widgets.ButtonText(closeRect, "Close".Translate()))
                        Close();
                    else if (Widgets.ButtonText(new Rect(closeRect.xMax + buttonGap, closeRect.y, buttonWidth, buttonHeight), "Confirm".Translate()))
                        OnAcceptKeyPressed();
                }
                finally
                {
                    Text.Font = previousFont;
                    Text.Anchor = previousAnchor;
                }
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
        }
    }
}
