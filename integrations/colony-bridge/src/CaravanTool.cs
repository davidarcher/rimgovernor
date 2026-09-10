using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RimBridgeServer.Sdk;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace HomeBridge.BridgeTools
{
    public sealed class CaravanTools
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Tool("home/caravan", Title = "Plan ordinary caravan packing or travel",
            Description = "Catalog native transfer groups, preview or start ordinary pawn assembly/loading, or route an existing player caravan. Requires a paused current map. dryRun defaults true. Never creates caravans instantly or moves cargo directly. Formation receipts certify only assembly started.")]
        public async Task<object> Run(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "catalog, form, move, visit, stop or return", DefaultValue = "catalog")] string action = "catalog",
            [ToolParameter(Description = "Comma-separated exact current-map colonist load IDs for form")] string pawnIds = null,
            [ToolParameter(Description = "Comma-separated catalog cargo group IDs; paired with counts")] string cargoIds = null,
            [ToolParameter(Description = "Comma-separated positive integer counts for each cargo group")] string counts = null,
            [ToolParameter(Description = "Exact player caravan load ID for move/visit/stop/return")] string caravanId = null,
            [ToolParameter(Description = "Observed surface destination tile", DefaultValue = -1)] int destination = -1,
            [ToolParameter(Description = "Observed colony ID; required except catalog")] string colonyId = null,
            [ToolParameter(Description = "Observed load token; required except catalog")] string loadToken = null,
            [ToolParameter(Description = "Observed map ID; required except catalog", DefaultValue = -1)] int mapId = -1,
            [ToolParameter(Description = "True previews without orders", DefaultValue = true)] bool dryRun = true)
        {
            return await ctx.MainThread.InvokeAsync(() => {
                var identity = Current.Game?.GetComponent<ColonyIdentity>();
                if (action != "catalog" && (identity == null || identity.ColonyId != colonyId ||
                    identity.LoadToken != loadToken || Find.CurrentMap?.uniqueID != mapId))
                    return Refuse("Colony, map or load changed; observe before issuing caravan orders");
                var quantities = counts?.Split(',').Select(s => {
                    int value;
                    return int.TryParse(s.Trim(), out value) ? value : -1;
                }).ToArray();
                return Execute(action, pawnIds?.Split(',').Select(s => s.Trim()).ToArray(),
                    cargoIds?.Split(',').Select(s => s.Trim()).ToArray(), quantities,
                    caravanId, destination, dryRun);
            }, cancellationToken);
        }

        private static object Refuse(string reason) => new { success = true, accepted = false, reason };
        private static object Call(Dialog_FormCaravan dialog, string name, params object[] args) =>
            typeof(Dialog_FormCaravan).GetMethod(name, Private).Invoke(dialog, args);
        private static void Set(Dialog_FormCaravan dialog, string name, object value) =>
            typeof(Dialog_FormCaravan).GetField(name, Private).SetValue(dialog, value);
        private static string GroupId(TransferableOneWay group) =>
            group.things.Select(t => t.ThingID).OrderBy(id => id, StringComparer.Ordinal).First();

        private static object Execute(string action, string[] pawnIds, string[] cargoIds, int[] counts,
            string caravanId, int destination, bool dryRun)
        {
            var map = Find.CurrentMap;
            if (Current.Game == null || map == null || !Find.TickManager.Paused)
                return Refuse("A loaded paused map is required");
            if (action != "catalog" && action != "form" && action != "move" && action != "return" && action != "visit" && action != "stop")
                return Refuse("Unknown caravan action");
            if (action == "move" || action == "return" || action == "visit" || action == "stop")
            {
                if (pawnIds != null || cargoIds != null || counts != null)
                    return Refuse("Travel does not accept formation arguments");
                var caravan = Find.WorldObjects.Caravans.SingleOrDefault(c =>
                    c.IsPlayerControlled && c.GetUniqueLoadID() == caravanId);
                if (caravan == null) return Refuse("Player caravan no longer exists");
                if (action == "stop")
                {
                    if (!dryRun) caravan.pather.StopDead();
                    return new { success = true, accepted = true, dryRun, destination = caravan.Tile.tileId,
                        observation = WorldProgressionTools.ReadNow() };
                }
                PlanetTile target = action == "return" ? map.Tile : new PlanetTile(destination);
                if (!target.Valid || target.tileId >= Find.WorldGrid.TilesCount || !caravan.CanReach(target))
                    return Refuse("Destination is invalid or unreachable");
                CaravanArrivalAction arrival = null;
                if (action == "return")
                {
                    if (!map.IsPlayerHome || !CaravanArrivalAction_Enter.CanEnter(caravan, map.Parent))
                        return Refuse("Current home cannot be entered");
                    arrival = new CaravanArrivalAction_Enter(map.Parent);
                }
                if (action == "visit")
                {
                    var settlement = Find.WorldObjects.Settlements.SingleOrDefault(s => s.Tile == target);
                    if (!CaravanArrivalAction_VisitSettlement.CanVisit(caravan, settlement))
                        return Refuse("No eligible settlement visit at this tile");
                    arrival = new CaravanArrivalAction_VisitSettlement(settlement);
                }
                if (dryRun) return new { success = true, accepted = true, dryRun, destination = target.tileId,
                    route = RouteFacts(caravan.Tile, target, caravan.TicksPerMove, caravan.DaysWorthOfFood.days, caravan.DaysWorthOfFood.tillRot, caravan),
                    returnStorage = StorageCandidates(map, caravan.PawnsListForReading.SelectMany(p => p.inventory.innerContainer)) };
                bool started = caravan.pather.StartPath(target, arrival);
                return new { success = true, accepted = started, dryRun, destination = target.tileId,
                    observation = WorldProgressionTools.ReadNow() };
            }
            if (caravanId != null) return Refuse("Formation does not accept a caravan ID");
            // Native dialog calculation without opening UI or taking camera ownership.
            var dialog = new Dialog_FormCaravan(map);
            Set(dialog, "autoSelectTravelSupplies", false);
            Call(dialog, "CalculateAndRecacheTransferables");
            foreach (var group in dialog.transferables) group.ForceToDestination(0);
            if (action == "catalog")
            {
                if (pawnIds != null || cargoIds != null || counts != null || destination != -1)
                    return Refuse("Catalog takes no formation arguments");
                var neighbors = new List<PlanetTile>();
                Find.WorldGrid.GetTileNeighbors(map.Tile, neighbors);
                return new { success = true, accepted = true, dryRun = true, tile = map.Tile.tileId,
                    neighbors = neighbors.Select(t => t.tileId).ToArray(),
                    groups = dialog.transferables.Select(g => new { id = GroupId(g),
                        pawnId = (g.AnyThing as Pawn)?.GetUniqueLoadID(),
                        defName = g.ThingDef.defName, label = g.AnyThing.Label,
                        pawn = g.AnyThing is Pawn, available = g.MaxCount,
                        things = g.things.Select(t => t.ThingID).ToArray() }).ToArray() };
            }
            pawnIds = pawnIds ?? Array.Empty<string>();
            cargoIds = cargoIds ?? Array.Empty<string>();
            counts = counts ?? Array.Empty<int>();
            if (pawnIds.Length == 0 || pawnIds.Distinct().Count() != pawnIds.Length ||
                cargoIds.Length != counts.Length || cargoIds.Distinct().Count() != cargoIds.Length || counts.Any(n => n <= 0))
                return Refuse("Select unique colonists and unique cargo groups with positive paired counts");
            var pawns = new List<Pawn>();
            foreach (var id in pawnIds)
            {
                var group = dialog.transferables.SingleOrDefault(g => g.AnyThing is Pawn p && p.GetUniqueLoadID() == id);
                var pawn = group?.AnyThing as Pawn;
                if (pawn == null || !pawn.IsFreeColonist || pawn.Downed || pawn.Dead || pawn.Drafted || pawn.InMentalState || pawn.GetLord() != null)
                    return Refuse("Colonist is unavailable, drafted or already assigned: " + id);
                pawns.Add(pawn);
                group.ForceToDestination(1);
            }
            if (map.mapPawns.FreeColonistsSpawned.Count <= pawns.Count)
                return Refuse("Leave at least one colonist at home");
            for (int i = 0; i < cargoIds.Length; i++)
            {
                var group = dialog.transferables.SingleOrDefault(g => GroupId(g) == cargoIds[i] && !(g.AnyThing is Pawn));
                if (group == null || counts[i] > group.MaxCount)
                    return Refuse("Cargo group changed or requested quantity is unavailable: " + cargoIds[i]);
                group.ForceToDestination(counts[i]);
            }
            var tile = new PlanetTile(destination);
            if (!tile.Valid || tile.tileId >= Find.WorldGrid.TilesCount || tile == map.Tile)
                return Refuse("Choose a different valid observed surface tile");
            var exit = CaravanExitMapUtility.BestExitTileToGoTo(tile, map);
            if (!exit.Valid) return Refuse("No native caravan exit route");
            Set(dialog, "destinationTile", tile);
            Set(dialog, "startingTile", exit);
            Call(dialog, "Notify_TransferablesChanged");
            if (dialog.MassUsage > dialog.MassCapacity) return Refuse("Cargo exceeds native carrying capacity");
            var food = ((float days, float tillRot))typeof(Dialog_FormCaravan)
                .GetProperty("DaysWorthOfFood", Private).GetValue(dialog);
            if (food.days < 1f) return Refuse("At least one native day of caravan food is required");
            // This native check emits refusal messages, but never creates a lord or transfers cargo.
            if (!(bool)Call(dialog, "CheckForErrors", pawns)) return Refuse("Native caravan eligibility refused; inspect game messages");
            var selected = dialog.transferables.Where(g => !(g.AnyThing is Pawn) && g.CountToTransfer > 0)
                .GroupBy(g => g.ThingDef.defName).Select(g => new { defName = g.Key,
                    count = g.Sum(row => row.CountToTransfer) }).ToArray();
            var available = dialog.transferables.Where(g => !(g.AnyThing is Pawn))
                .GroupBy(g => g.ThingDef.defName).Select(g => new { defName = g.Key,
                    available = g.Sum(row => row.MaxCount) }).ToArray();
            var carried = pawns.SelectMany(p => p.inventory.innerContainer)
                .GroupBy(t => t.def.defName).Select(g => new { defName = g.Key,
                    count = g.Sum(t => t.stackCount) }).ToArray();
            if (dryRun) return new { success = true, accepted = true, dryRun,
                massUsage = dialog.MassUsage, massCapacity = dialog.MassCapacity,
                costList = selected, carriedCargo = carried, materials = new { rows = available },
                homePawns = map.mapPawns.FreeColonistsSpawned.Where(p => !pawns.Contains(p)).Select(p => p.GetUniqueLoadID()).ToArray(),
                homeDoctors = map.mapPawns.FreeColonistsSpawned.Count(p => !pawns.Contains(p) && !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)),
                route = RouteFacts(map.Tile, tile, CaravanTicksPerMoveUtility.GetTicksPerMove(new CaravanTicksPerMoveUtility.CaravanInfo(dialog)), food.days, food.tillRot, null),
                returnStorage = StorageCandidates(map, dialog.transferables.Where(g => !(g.AnyThing is Pawn) && g.CountToTransfer > 0).Select(g => g.AnyThing)) };
            bool accepted = (bool)Call(dialog, "TryFormAndSendCaravan");
            return new { success = true, accepted, dryRun, observation = WorldProgressionTools.ReadNow() };
        }

        private static object RouteFacts(PlanetTile from, PlanetTile to, int ticksPerMove, float foodDays, float foodRotDays, Caravan caravan)
        {
            using (var path = from.Layer.Pather.FindPath(from, to, caravan))
            {
                var settlement = Find.WorldObjects.Settlements.SingleOrDefault(s => s.Tile == to);
                var trade = caravan != null && settlement?.Faction != null && !settlement.Faction.IsPlayer
                    ? CaravanVisitUtility.TradeCommand(caravan, settlement.Faction, settlement.TraderKind) : null;
                return new { reachable = path.Found,
                    estimatedTicks = path.Found ? (int?)CaravanArrivalTimeEstimator.EstimatedTicksToArrive(from, to, path, 0f, ticksPerMove, Find.TickManager.TicksAbs) : null,
                    foodDays, foodRotDays, destination = to.tileId,
                    temperature = GenTemperature.GetTemperatureFromSeasonAtTile(Find.TickManager.TicksAbs, to),
                    settlementId = settlement?.GetUniqueLoadID(), factionId = settlement?.Faction?.GetUniqueLoadID(),
                    hostile = settlement?.Faction?.HostileTo(Faction.OfPlayer) ?? false,
                    canTrade = trade == null ? (bool?)null : settlement.CanTradeNow && !trade.Disabled,
                    tradeReason = trade?.disabledReason,
                    goodwill = settlement?.Faction == null || settlement.Faction.IsPlayer ? (int?)null : settlement.Faction.PlayerGoodwill };
            }
        }

        private static object[] StorageCandidates(Map map, IEnumerable<Thing> things)
        {
            return things.GroupBy(t => t.def).Select(g => (object)new { defName = g.Key.defName,
                cells = map.zoneManager.AllZones.OfType<Zone_Stockpile>()
                    .Where(z => z.GetStoreSettings().filter.Allows(g.Key))
                    .SelectMany(z => z.Cells).Distinct().Count(c => c.Standable(map)) }).ToArray();
        }
    }
}
