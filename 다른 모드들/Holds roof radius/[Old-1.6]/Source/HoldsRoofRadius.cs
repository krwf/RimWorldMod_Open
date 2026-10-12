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
            }
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
}