using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // AreaPlantCutIntent on Actions/Apply:
    // every non-crop plant on cells is ordered cut, wild plants with
    // CutPlant and a harvestable tree with chop-wood (HarvestPlant), the way
    // NativeClearCover chooses. Plants in a growing zone or on a plant
    // grower and sown crops are never touched; there is no cover-fill
    // requirement. A fogged cell, a cell with nothing to cut and a plant
    // already designated are no-ops. The firebreak reads standing plants from the
    // mirror, not from native.
    internal static class NativeAreaPlantCut
    {
        private static bool ChopWood(Plant plant) => plant.def.plant.IsTree && plant.HarvestableNow;

        private static bool Designated(Plant plant)
        {
            var manager = plant.Map.designationManager;
            return manager.DesignationOn(plant, DesignationDefOf.CutPlant) != null || manager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null;
        }

        private static Designator DesignatorFor(Plant plant) => ChopWood(plant) ? (Designator)new Designator_PlantsHarvestWood() : new Designator_PlantsCut { isOrder = true };

        private static bool CropGround(Map map, IntVec3 cell) => map.zoneManager.ZoneAt(cell) is Zone_Growing || cell.GetEdifice(map) is Building_PlantGrower;

        // A wild plant that yields food (berry bushes, forage) is
        // the colony's forage and stays standing; trees are still chopped.
        private static bool Forage(Plant plant) => !plant.def.plant.IsTree && plant.def.plant.harvestedThingDef?.IsNutritionGivingIngestible == true;

        // Plants the area cut owns on a visible cell: non-crop, non-forage plants off crop ground.
        private static IEnumerable<Plant> Plants(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map) || cell.Fogged(map) || CropGround(map, cell)) yield break;
            foreach (var thing in cell.GetThingList(map).ToList())
                if (thing is Plant plant && plant.Spawned && !plant.Destroyed && plant.Position == cell && !plant.IsCrop && !Forage(plant)) yield return plant;
        }


        private static string? Refusal(IEnumerable<RimGovernor.Brain.Domain.Cell>? requested, Map map, out List<IntVec3> cells)
        {
            cells = new List<IntVec3>();
            var rows = requested?.ToList() ?? new List<RimGovernor.Brain.Domain.Cell>();
            if (rows.Count == 0) return "Area plant cut requires at least one cell.";
            var seen = new HashSet<IntVec3>();
            foreach (var c in rows)
            {
                var cell = new IntVec3(c.X, 0, c.Z);
                if (!cell.InBounds(map)) return $"Cell {cell} is out of bounds.";
                if (!seen.Add(cell)) return $"Cell {cell} is repeated.";
                cells.Add(cell);
            }
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.AreaPlantCutIntent? intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var map = GameThread.ResolveMap(context);
            if (map == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Current map required.");
            var refusal = Refusal(intent?.Cells, map, out _);
            return refusal == null ? null : GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, refusal);
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.AreaPlantCutIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var map = GameThread.ResolveMap(context) ?? throw new InvalidOperationException("Area plant cut prerequisites changed before apply: current map required.");
            var refusal = Refusal(intent.Cells, map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Area plant cut prerequisites changed before apply: " + refusal);
            foreach (var cell in cells)
                foreach (var plant in Plants(map, cell).ToList())
                {
                    if (Designated(plant)) continue;
                    var designator = DesignatorFor(plant);
                    if (!designator.CanDesignateThing(plant).Accepted) continue;
                    designator.DesignateThing(plant);
                    if (!Designated(plant)) throw new InvalidOperationException($"Native plant cut designation was not observed at {cell}.");
                }
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.AreaPlantCutCase() };
        }


    }

    internal sealed class AreaPlantCutActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAreaPlantCut.Validate(action.Intent?.AreaPlantCut, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeAreaPlantCut.Apply(action.Intent?.AreaPlantCut ?? throw new InvalidOperationException("Action carries no area-plant-cut intent."), context);
    }
}
