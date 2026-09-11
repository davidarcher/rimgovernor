#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools
{
    // Private setup only: the controller must place and observe all sleeping spots.
    public sealed class RoutineSleepingFixture
    {
        private static readonly object HistoryGate = new object();
        private static string? historyPath;
        private static int historyCount;
        private static long historyBytes;

        // Passive private-fixture capture avoids a second GABS connection and
        // preserves SDK events beyond its bounded diagnostic query window.
        private static void StartHistory()
        {
            if (historyPath != null) throw new InvalidOperationException("History capture already started.");
            var path = Path.Combine(GenFilePaths.ConfigFolderPath, "routine-shelter-operations.jsonl");
            if (File.Exists(path)) throw new InvalidOperationException("Fresh diagnostic output required.");
            var journal = AccessTools.TypeByName("RimBridgeServer.Core.OperationJournal");
            var record = AccessTools.TypeByName("RimBridgeServer.Core.OperationEventRecord");
            var publish = journal == null || record == null ? null : AccessTools.Method(journal, "Publish", new[] { record });
            if (publish == null || publish.ReturnType != typeof(void)) throw new InvalidOperationException("SDK event publication contract unavailable.");
            File.WriteAllText(path, "");
            historyPath = path;
            new Harmony("rimgovernor.fixture.shelter-history").Patch(publish,
                postfix: new HarmonyMethod(typeof(RoutineSleepingFixture), nameof(RecordHistory)));
        }

        private static void RecordHistory(object __0)
        {
            try {
                lock (HistoryGate) {
                    if (historyPath == null || historyCount >= 100000) return;
                    var line = JsonConvert.SerializeObject(__0);
                    if (line.Length > 65536) return;
                    var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                    if (historyBytes + bytes > 64 * 1024 * 1024) return;
                    File.AppendAllText(historyPath, line + "\n");
                    historyBytes += bytes;
                    historyCount++;
                }
            }
            catch { /* Missing events fail the independent sequence audit. */ }
        }

        [Tool("test/routine_sleeping_prepare", Description = "UNSAFE FOR MODEL EXECUTION. Disposable test setup: clear an outdoor starter site or create an empty roofed room near existing colonists; remove starting injuries. Outdoor mode creates no buildings or roofs. Never creates sleeping spots or edits controller results.")]
        public async Task<object> Prepare(IRimBridgeContext ctx, CancellationToken cancellationToken, bool outdoorSite = false)
        {
            return await ctx.MainThread.InvokeAsync<object>(() => {
                var map = Find.CurrentMap;
                if (map == null || !Find.TickManager.Paused) throw new InvalidOperationException("Paused disposable map required.");
                var people = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
                if (people.Count < 1 || people.Count > 8) throw new InvalidOperationException("Require 1..8 colonists.");
                var center = new IntVec3((int)people.Average(p => p.Position.x), 0, (int)people.Average(p => p.Position.z));
                var candidates = GenRadial.RadialCellsAround(center, outdoorSite ? 18 : 14, true).Where(c => c.DistanceToSquared(center) >= 36);
                var size = outdoorSite ? 9 : 7;
                var room = candidates.Select(c => new CellRect(c.x - size / 2, c.z - size / 2, size, size)).FirstOrDefault(r => r.Cells.All(c =>
                    c.InBounds(map) && !c.Fogged(map) && (outdoorSite ? c.GetTerrain(map).passability != Traversability.Impassable : c.Standable(map)) && map.zoneManager.ZoneAt(c) == null &&
                    (!outdoorSite || c.GetRoof(map) == null && c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light)) &&
                    c.GetThingList(map).All(t => t is Plant || outdoorSite && (t is Pawn || t.def.category == ThingCategory.Item))));
                if (room.Width != size) throw new InvalidOperationException("No clear bounded room site.");
                if (outdoorSite) StartHistory();
                foreach (var cell in room.Cells) {
                    foreach (var plant in cell.GetThingList(map).OfType<Plant>().ToList()) plant.Destroy(DestroyMode.Vanish);
                    if (!outdoorSite && (cell.x == room.minX || cell.x == room.maxX || cell.z == room.minZ || cell.z == room.maxZ)) {
                        var definition = cell.x == room.CenterCell.x && cell.z == room.minZ ? ThingDefOf.Door : ThingDefOf.Wall;
                        var building = ThingMaker.MakeThing(definition, ThingDefOf.WoodLog);
                        building.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(building, cell, map);
                    }
                    if (!outdoorSite) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                }
                foreach (var pawn in people)
                    foreach (var injury in pawn.health.hediffSet.hediffs.OfType<Hediff_Injury>().ToList()) pawn.health.RemoveHediff(injury);
                map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                return new { success = true, tick = Find.TickManager.TicksGame, colonists = people.Count,
                    center = new { x = room.CenterCell.x, z = room.CenterCell.z },
                    interior = room.ContractedBy(1).Cells.Select(c => new { x = c.x, z = c.z }).ToArray(),
                    sleepingSpotsCreated = 0, outdoorSite, shellPiecesCreated = outdoorSite ? 0 : 24, roofCellsCreated = outdoorSite ? 0 : 49,
                    operationHistoryPath = historyPath };
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
