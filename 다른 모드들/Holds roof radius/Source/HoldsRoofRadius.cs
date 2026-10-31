using System;
using System.Collections.Generic;
using UnityEngine;
using RimWorld;
using Verse;

namespace KRWF.HoldsRoofRadius
{
    [StaticConstructorOnStartup]
    public class PlaceWorker_HoldsRoofRadius : PlaceWorker
    {
        public static readonly float RoofRadius;

        static PlaceWorker_HoldsRoofRadius()
        {
            RoofRadius = RoofCollapseUtility.RoofMaxSupportDistance;

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!ShouldAddRadius(def))
                    continue;

                def.placeWorkers ??= new List<Type>();

                Type workerType = typeof(PlaceWorker_HoldsRoofRadius);

                if (!def.placeWorkers.Contains(workerType))
                    def.placeWorkers.Add(workerType);

                AddToggleComp(def.blueprintDef);
                AddToggleComp(def.frameDef);
            }
        }

        private static void AddToggleComp(ThingDef def)
        {
            if (def == null)
                return;

            def.comps ??= new List<CompProperties>();

            if (!def.comps.Exists(props => props.compClass == typeof(Comp_HoldsRoofRadiusToggle)))
                def.comps.Add(new CompProperties(typeof(Comp_HoldsRoofRadiusToggle)));
        }

        public static bool ShouldAddRadius(ThingDef def)
        {
            return def != null
                && def.holdsRoof
                && def.category == ThingCategory.Building
                && def.designationCategory != null
                && def.specialDisplayRadius <= 0f;
        }

        public static void AddSupportCells(
            HashSet<IntVec3> cells,
            IntVec3 center,
            Rot4 rot,
            IntVec2 size,
            Map map)
        {
            foreach (IntVec3 holderCell in GenAdj.CellsOccupiedBy(
                center,
                rot,
                size))
            {
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(
                    holderCell,
                    RoofRadius,
                    true))
                {
                    if (cell.InBounds(map))
                        cells.Add(cell);
                }
            }
        }

        public override void DrawGhost(
            ThingDef def,
            IntVec3 center,
            Rot4 rot,
            Color ghostCol,
            Thing thing = null)
        {
            RoofRadiusDisplay.DrawGhost?.Invoke(def, center, rot, thing);
        }

        internal static void DrawGhostRadius(
            ThingDef def,
            IntVec3 center,
            Rot4 rot,
            Thing thing)
        {
            if (thing != null)
                return;

            Map map = Find.CurrentMap;

            if (map == null)
                return;

            if (def.size.x == 1 && def.size.z == 1)
            {
                GenDraw.DrawRadiusRing(center, RoofRadius);
                return;
            }

            HashSet<IntVec3> cells = new HashSet<IntVec3>();

            AddSupportCells(
                cells,
                center,
                rot,
                def.size,
                map);

            if (cells.Count > 0)
                GenDraw.DrawFieldEdges(new List<IntVec3>(cells));
        }
    }


    public class MapComponent_HoldsRoofRadius : MapComponent
    {
        public MapComponent_HoldsRoofRadius(Map map) : base(map)
        {
        }

        public override void MapComponentUpdate()
        {
            RoofRadiusDisplay.DrawSelected?.Invoke(map);
        }

        internal static void DrawSelectedRadius(Map map)
        {
            if (Find.CurrentMap != map
                || Find.ScreenshotModeHandler.Active)
                return;

            HashSet<IntVec3> cells = new HashSet<IntVec3>();

            foreach (object obj in Find.Selector.SelectedObjects)
            {
                IntVec3 position;
                Rot4 rotation;
                ThingDef buildDef;

                if (obj is Blueprint_Build blueprint
                    && blueprint.Map == map
                    && blueprint.def.entityDefToBuild is ThingDef blueprintBuildDef)
                {
                    position = blueprint.Position;
                    rotation = blueprint.Rotation;
                    buildDef = blueprintBuildDef;
                }
                else if (obj is Frame frame
                    && frame.Map == map
                    && frame.def.entityDefToBuild is ThingDef frameBuildDef)
                {
                    position = frame.Position;
                    rotation = frame.Rotation;
                    buildDef = frameBuildDef;
                }
                else
                {
                    continue;
                }

                if (!PlaceWorker_HoldsRoofRadius.ShouldAddRadius(buildDef))
                    continue;

                PlaceWorker_HoldsRoofRadius.AddSupportCells(
                    cells,
                    position,
                    rotation,
                    buildDef.size,
                    map);
            }
            if (cells.Count > 0)
                GenDraw.DrawFieldEdges(new List<IntVec3>(cells));
        }
    }

    internal static class RoofRadiusDisplay
    {
        private static readonly Action<Map> SelectedRenderer = MapComponent_HoldsRoofRadius.DrawSelectedRadius;
        private static readonly Action<ThingDef, IntVec3, Rot4, Thing> GhostRenderer = PlaceWorker_HoldsRoofRadius.DrawGhostRadius;

        internal static Action<Map> DrawSelected { get; private set; } = SelectedRenderer;
        internal static Action<ThingDef, IntVec3, Rot4, Thing> DrawGhost { get; private set; } = GhostRenderer;

        internal static bool Enabled => DrawSelected != null;

        internal static void Toggle()
        {
            bool enabled = !Enabled;
            // Disconnect both renderers: disabled callbacks do not collect or draw cells.
            DrawSelected = enabled ? SelectedRenderer : null;
            DrawGhost = enabled ? GhostRenderer : null;
        }
    }

    [StaticConstructorOnStartup]
    public class Comp_HoldsRoofRadiusToggle : ThingComp
    {
        private static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("HRR");

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_HoldsRoofRadiusToggle
            {
                defaultLabel = "HRR",
                icon = Icon,
                isActive = () => RoofRadiusDisplay.Enabled,
                toggleAction = RoofRadiusDisplay.Toggle
            };
        }
    }

    public class Command_HoldsRoofRadiusToggle : Command_Toggle
    {
        public override bool InheritInteractionsFrom(Gizmo other)
        {
            // All selected blueprints/frames share one state, so toggle only once.
            return false;
        }
    }
}
