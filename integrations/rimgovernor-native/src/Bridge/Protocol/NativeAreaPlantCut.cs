#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // AreaPlantCutIntent on Actions/Apply (#1547):
    // every non-crop plant on cells is ordered cut, wild plants with
    // CutPlant and a harvestable tree with chop-wood (HarvestPlant), the way
    // NativeClearCover chooses. Plants in a growing zone or on a plant
    // grower and sown crops are never touched; there is no cover-fill
    // requirement. A fogged cell, a cell with nothing to cut and a plant
    // already designated are no-ops. The firebreak reads standing plants from the
    // mirror, not from native (#2273).
    internal static class NativeAreaPlantCut
    {
        internal const int MaxCells = 1024;

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


        private static string? Refusal(IEnumerable<Common.Cell>? requested, Map map, out List<IntVec3> cells)
        {
            cells = new List<IntVec3>();
            var rows = requested?.ToList() ?? new List<Common.Cell>();
            if (rows.Count == 0 || rows.Count > MaxCells) return $"Area plant cut requires 1..{MaxCells} cells.";
            var seen = new HashSet<IntVec3>();
            foreach (var c in rows)
            {
                if (c == null || !c.HasX || !c.HasZ) return "Area plant cut cell requires x and z.";
                var cell = new IntVec3(c.X, 0, c.Z);
                if (!cell.InBounds(map)) return $"Cell {cell} is out of bounds.";
                if (!seen.Add(cell)) return $"Cell {cell} is repeated.";
                cells.Add(cell);
            }
            return null;
        }

        internal static Common.Failure? Validate(Operations.AreaPlantCutIntent? intent, Common.ObservationContext context)
        {
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current map required.");
            var refusal = Refusal(intent?.Cells, map, out _);
            return refusal == null ? null : ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, refusal);
        }

        internal static Receipts.EffectEvidence Apply(Operations.AreaPlantCutIntent intent, Common.ObservationContext context)
        {
            var map = ProtoBoundary.ResolveMap(context) ?? throw new InvalidOperationException("Area plant cut prerequisites changed before apply: current map required.");
            var refusal = Refusal(intent.Cells, map, out var cells);
            if (refusal != null) throw new InvalidOperationException("Area plant cut prerequisites changed before apply: " + refusal);
            var effect = new Receipts.AreaPlantCutEffect { Cut = 0, Chopped = 0, Adopted = 0 };
            foreach (var cell in cells)
                foreach (var plant in Plants(map, cell).ToList())
                {
                    if (Designated(plant)) { effect.Adopted++; continue; }
                    var designator = DesignatorFor(plant);
                    if (!designator.CanDesignateThing(plant).Accepted) continue;
                    var chop = ChopWood(plant);
                    designator.DesignateThing(plant);
                    if (!Designated(plant)) throw new InvalidOperationException($"Native plant cut designation was not observed at {cell}.");
                    if (chop) effect.Chopped++; else effect.Cut++;
                }
            return new Receipts.EffectEvidence { AreaPlantCut = effect };
        }


    }

    internal sealed class AreaPlantCutActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeAreaPlantCut.Validate(action.AreaPlantCut, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeAreaPlantCut.Apply(action.AreaPlantCut, context);
    }
}
