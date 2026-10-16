using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace KRWF.RimKata
{
    public static class RimKataPreviewInfoCard
    {
        [ThreadStatic]
        private static bool drawingPreview;

        internal static bool DrawingPreview => drawingPreview;

        public static void Open(ThingDef def)
        {
            Thing preview = CreatePreview(def);
            Find.WindowStack.Add(preview != null
                ? (Window)new Dialog_RimKataPreviewInfoCard(preview)
                : new Dialog_RimKataPreviewInfoCard(def));
        }

        internal static void Draw(Action draw)
        {
            bool previous = drawingPreview;
            drawingPreview = true;
            try
            {
                draw();
            }
            finally
            {
                drawingPreview = previous;
            }
        }

        private static Thing CreatePreview(ThingDef def)
        {
            if (def == null)
            {
                return null;
            }

            try
            {
                Thing preview = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
                if (preview.def.useHitPoints)
                {
                    preview.HitPoints = preview.MaxHitPoints;
                }

                preview.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, null);
                preview.stackCount = 1;
                return preview;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public sealed class Dialog_RimKataPreviewInfoCard : Dialog_InfoCard
    {
        private Thing preview;

        public Dialog_RimKataPreviewInfoCard(Thing preview)
            : base(preview)
        {
            this.preview = preview;
        }

        public Dialog_RimKataPreviewInfoCard(ThingDef def)
            : base(def)
        {
        }

        public override void DoWindowContents(Rect inRect)
        {
            RimKataPreviewInfoCard.Draw(() => base.DoWindowContents(inRect));
        }

        public override void Close(bool doCloseSound = true)
        {
            base.Close(doCloseSound);
            if (preview != null && !preview.Destroyed)
            {
                preview.Destroy(DestroyMode.Vanish);
            }

            preview = null;
        }
    }

    [HarmonyPatch(typeof(StatDrawEntry), nameof(StatDrawEntry.GetHyperlinks))]
    public static class Patch_StatDrawEntry_RimKataPreviewHyperlinks
    {
        public static bool Prefix(ref IEnumerable<Dialog_InfoCard.Hyperlink> __result)
        {
            if (!RimKataPreviewInfoCard.DrawingPreview)
            {
                return true;
            }

            __result = Array.Empty<Dialog_InfoCard.Hyperlink>();
            return false;
        }
    }

    [HarmonyPatch(typeof(StatWorker), "GetAdditionalOffsetsAndFactorsExplanation")]
    public static class Patch_StatWorker_RimKataPreviewScenarioFactor
    {
        [HarmonyAfter("defaults.1trickPwnyta")]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getFactor = AccessTools.Method(typeof(Scenario), nameof(Scenario.GetStatFactor));
            var getPreviewFactor = AccessTools.Method(typeof(Patch_StatWorker_RimKataPreviewScenarioFactor), nameof(GetScenarioStatFactor));
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(getFactor))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = getPreviewFactor;
                }

                yield return instruction;
            }
        }

        private static float GetScenarioStatFactor(Scenario scenario, StatDef stat)
        {
            if (scenario == null && RimKataPreviewInfoCard.DrawingPreview)
            {
                return 1f;
            }

            return scenario.GetStatFactor(stat);
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.ShouldShowFor))]
    public static class Patch_StatWorker_RimKataMainMenuInfoCard
    {
        public static Exception Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception is NullReferenceException
                && RimKataPreviewInfoCard.DrawingPreview
                && Current.Game?.World == null)
            {
                __result = false;
                return null;
            }

            return __exception;
        }
    }
}
