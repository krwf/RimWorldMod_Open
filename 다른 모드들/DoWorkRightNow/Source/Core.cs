using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace KRWF.DWRN
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string HarmonyId = "krwf.doworkrightnow";

        static Bootstrap()
        {
            var harmony = new Harmony(HarmonyId);
            try
            {
                harmony.PatchAll(typeof(Bootstrap).Assembly);
                Log.Message("[Do work right now] 1.0.0 loaded. Work priorities are preserved; forced work is saved per map.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(HarmonyId);
                Log.Error("[Do work right now] Could not apply all required patches; the mod was disabled for this session. " + error);
            }
        }
    }

    /// <summary>
    /// Only the selected work definition is persistent. Tool selection and job
    /// scan caches are deliberately transient. Original pawn priorities are never edited.
    /// </summary>
    public sealed class ForcedWorkMapComponent : MapComponent
    {
        private WorkTypeDef forcedWork;

        public ForcedWorkMapComponent(Map map) : base(map) { }

        public Map Map { get { return map; } }
        public WorkTypeDef ForcedWork { get { return forcedWork; } }
        public bool Enabled { get { return forcedWork != null; } }

        public static ForcedWorkMapComponent For(Map map)
        {
            return map == null ? null : map.GetComponent<ForcedWorkMapComponent>();
        }

        public void SetForcedWork(WorkTypeDef workType)
        {
            if (forcedWork == workType)
                return;

            WorkTypeDef previous = forcedWork;
            forcedWork = workType;
            WorkController.NotifyChanged(this, previous);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref forcedWork, "dwrn_forcedWork");
        }

        public override void MapComponentTick()
        {
            WorkController.Tick(this);
        }
    }
}
