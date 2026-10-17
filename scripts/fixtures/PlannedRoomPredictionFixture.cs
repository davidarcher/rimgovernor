#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace HomeBridge.BridgeTools;

// Private disposable acceptance only (#3339): an open 5x5 patch with a wooden bed standing where a planned 3x3 bedroom
// will be closed. The room prediction read runs before anything is built; the fixture then spawns the ring (wood walls
// and a door) and checks the predicted role, impressiveness and stage against the game's own room.
public sealed partial class PawnControlFixture
{
    private static IntVec3 predictionOrigin;
    private static Thing? predictionBed;

    private const int PredictionPatch = 5;

    private static IntVec3 PredictionAt(int x, int z) => new(predictionOrigin.x + x, 0, predictionOrigin.z + z);

    [Tool("test/planned_room_prediction_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Clear and unroof one open 5x5 patch near a colonist and spawn a wooden bed in its 3x3 interior.")]
    public async Task<object> PlannedRoomPredictionPrepare(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var map = Find.CurrentMap ?? throw new InvalidOperationException("Load a colony first.");
            var pawn = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.thingIDNumber).FirstOrDefault(p => !p.Downed)
                ?? throw new InvalidOperationException("No free colonist.");
            predictionOrigin = GenRadial.RadialCellsAround(pawn.Position, 40, true).FirstOrDefault(c =>
                new CellRect(c.x, c.z, PredictionPatch, PredictionPatch).ExpandedBy(1).Cells.All(cell => cell.InBounds(map) && !cell.Fogged(map)
                    && !cell.OnEdge(map) && cell.GetEdifice(map) == null && cell.GetZone(map) == null && !cell.GetTerrain(map).IsWater
                    && cell.GetTerrain(map).passability != Traversability.Impassable && !cell.Roofed(map)
                    && !cell.GetThingList(map).Any(t => t.def.category == ThingCategory.Pawn || t.def.category == ThingCategory.Building))
                && pawn.CanReach(c, PathEndMode.Touch, Danger.None));
            if (predictionOrigin == default) throw new InvalidOperationException("No open, unroofed, reachable 5x5 patch.");
            foreach (var cell in new CellRect(predictionOrigin.x, predictionOrigin.z, PredictionPatch, PredictionPatch).Cells)
            {
                foreach (var thing in cell.GetThingList(map).Where(t => t is Plant || t.def.category is ThingCategory.Item or ThingCategory.Filth).ToList()) thing.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                map.areaManager.BuildRoof[cell] = false;
            }
            var bed = ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer);
            predictionBed = GenSpawn.Spawn(bed, PredictionAt(2, 1), map, Rot4.North, WipeMode.Vanish);
            return new { success = true, origin = new { x = predictionOrigin.x, z = predictionOrigin.z }, bed = predictionBed.GetUniqueLoadID() };
        }, ct).ConfigureAwait(false);

    [Tool("test/planned_room_prediction_read", Description = "UNSAFE FOR MODEL EXECUTION. Predict the prepared planned bedroom through the construction activity read, build its wood ring, and compare the prediction with the game's room.")]
    public async Task<object> PlannedRoomPredictionRead(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            if (predictionOrigin == default || predictionBed is not { Spawned: true }) throw new InvalidOperationException("Prepare planned_room_prediction first.");
            var map = Find.CurrentMap;
            var identity = Context().Identity!;
            Cell C(IntVec3 cell) => new(cell.x, cell.z);
            var interior = new CellRect(PredictionAt(1, 1).x, PredictionAt(1, 1).z, 3, 3);
            var ring = interior.ExpandedBy(1).EdgeCells.ToList();
            var door = PredictionAt(2, 0);
            var pieces = ring.Select(c => new Obs.PlannedRoomPiece(C(c), c == door ? "Door" : "Wall", "WoodLog")).ToArray();
            var started = DateTime.UtcNow;
            var read = NativeConstructionActivity.Read(new(identity, default)
            {
                RoomPredictions = [new("1", "WoodLog", RimGovernor.Brain.Domain.EquatableArray.From(interior.Cells.Select(C)), RimGovernor.Brain.Domain.EquatableArray.From(pieces))],
            });
            var readMs = (DateTime.UtcNow - started).TotalMilliseconds;
            var prediction = read.RoomPredictions.SingleOrDefault() ?? throw new InvalidOperationException("Expected one room prediction, got " + read.RoomPredictions.Count + "; findings " + string.Join(",", read.Findings));

            foreach (var c in ring)
            {
                var piece = ThingMaker.MakeThing(c == door ? ThingDefOf.Door : ThingDefOf.Wall, ThingDefOf.WoodLog);
                piece.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(piece, c, map, Rot4.North, WipeMode.Vanish);
            }
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            var room = predictionBed.GetRoom() ?? throw new InvalidOperationException("The built room has no room at the bed.");
            var impressiveness = room.GetStat(RoomStatDefOf.Impressiveness);
            var stage = RoomStatDefOf.Impressiveness.GetScoreStageIndex(impressiveness);
            var actual = new { role = room.Role?.defName ?? "", impressiveness, stage, cells = room.CellCount, open = room.OpenRoofCount, outdoors = room.PsychologicallyOutdoors,
                wealth = room.GetStat(RoomStatDefOf.Wealth), beauty = room.GetStat(RoomStatDefOf.Beauty), space = room.GetStat(RoomStatDefOf.Space),
                cleanliness = room.GetStat(RoomStatDefOf.Cleanliness), things = room.ContainedAndAdjacentThings.Count };
            if (prediction.Role != actual.role || !prediction.Stage.IsKnown || prediction.Stage.Value != stage
                || !prediction.Impressiveness.IsKnown || Math.Abs(prediction.Impressiveness.Value - impressiveness) > 1e-3
                || prediction.Cells != room.CellCount || prediction.OpenRoofCells != room.OpenRoofCount
                || !prediction.Beds.Contains(predictionBed.GetUniqueLoadID()))
                throw new InvalidOperationException("Room prediction " + prediction + " disagrees with the built room " + actual + ".");
            return new { success = true, readMs, predicted = new { prediction.Role, impressiveness = prediction.Impressiveness.Value, stage = prediction.Stage.Value }, actual };
        }, ct).ConfigureAwait(false);
}
