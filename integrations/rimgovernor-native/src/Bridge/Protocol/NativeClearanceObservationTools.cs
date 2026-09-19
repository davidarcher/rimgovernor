#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    public sealed class NativeClearanceObservationTools
    {
        private const string ToolName = "rimgovernor/observations_get_clearance_targets";
        [Tool(ToolName, Title = "Read clearance targets", Description = "Complete bounded census of visible, deconstructible non-player buildings touching Home. Includes exact footprints, roof-support blockers, sealed ancient danger and deconstruction ownership. Read-only; does not admit removal.")]
        [ToolResponse("payload", "string", "Official ProtoJSON ClearanceTargetsReply.", Always = true)]
        public async Task<object> Read(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON ClearanceTargetsRequest string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.ClearanceTargetsRequest.Parser, out var parsed, out var failure))
                return ProtoBoundary.Encode(new Obs.ClearanceTargetsReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.ClearanceTargetsReply { Failure = error });
                try {
                    var player = Faction.OfPlayerSilentFail;
                    if (player == null || map.areaManager?.Home == null || map.listerThings == null || map.designationManager == null || map.roofGrid == null || map.roofCollapseBuffer == null)
                        return Missing(Common.UnavailableReason.NativeComponentMissing, "Player, Home, building or roof trackers unavailable.");
                    Require(map.cellIndices.NumGridCells <= 262144, "Map census exceeds the bounded scan.");
                    // Scoped to Home: a hilly map carries tens of thousands of
                    // natural-rock buildings map-wide (#414), so the census walks
                    // the Home cells (bounded by the grid) and collects the
                    // non-player buildings standing in them.
                    var home = map.areaManager.Home;
                    var buildings = new Dictionary<int, Building>();
                    foreach (var cell in home.ActiveCells)
                        foreach (var thing in cell.GetThingList(map))
                            if (thing is Building b && b.Spawned && b.Faction != player) buildings[b.thingIDNumber] = b;
                    Require(buildings.Count <= 8192, "Non-player buildings touching Home exceed 8192.");
                    var snapshot = new Obs.ClearanceTargetsSnapshot { Context = context };
                    foreach (var building in buildings.Values.OrderBy(b => b.thingIDNumber)) {
                        var rect = building.OccupiedRect();
                        Require(rect.Area > 0 && rect.Area <= 4096, "Building footprint exceeds 4096 cells.");
                        if (rect.Any(c => !c.InBounds(map) || c.Fogged(map))) continue;
                        // OfPlayerSilentFail was checked above: the native method
                        // cannot reach its missing-player Log.Error/pause branch.
                        if (!building.DeconstructibleBy(player)) continue;
                        Require(snapshot.Targets.Count < 256, "Clearance census exceeds 256 rows; no sample returned.");
                        var designated = map.designationManager.DesignationOn(building, DesignationDefOf.Deconstruct) != null;
                        var row = new Obs.ClearanceTarget {
                            EntityId = Id(building.GetUniqueLoadID()), DefName = Id(building.def.defName),
                            Occupied = new Obs.Rectangle { Minimum = Cell(rect.minX, rect.minZ), Maximum = Cell(rect.maxX, rect.maxZ) },
                            Deconstructible = true, Class = Classify(building), InHome = rect.All(c => home[c]),
                            AncientDanger = AncientDanger(map, building, player), Designated = designated,
                            ControllerOwned = designated && WallUpgradeSafety.Pending(building) != null
                        };
                        if (building.Faction != null) row.Faction = Id(building.Faction.GetUniqueLoadID());
                        var blocker = RoofSupportSafety.Blocker(building, out _);
                        if (blocker != null) row.RoofBlocker = blocker;
                        snapshot.Targets.Add(row);
                    }
                    var count = (ulong)snapshot.Targets.Count;
                    snapshot.Completeness = new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = count, Returned = count, Filtered = 0, Unreadable = 0 };
                    var reply = new Obs.ClearanceTargetsReply { Observed = snapshot };
                    Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Clearance reply exceeds 1 MiB.");
                    return ProtoBoundary.Encode(reply);
                }
                catch (ReadLimit limit) { return Missing(Common.UnavailableReason.LimitExceeded, limit.Message); }
                catch (Exception) { return Missing(Common.UnavailableReason.ReadFailed, "Clearance facts could not be read completely."); }
            }, cancellationToken).ConfigureAwait(false);
        }

        private static Obs.ClearanceClass Classify(Building building) =>
            building is Building_AncientCryptosleepCasket ? Obs.ClearanceClass.AncientCasket :
            building.def == ThingDefOf.ShipChunk ? Obs.ClearanceClass.ShipChunk :
            building.def == ThingDefOf.Wall || building is Building_Door ? Obs.ClearanceClass.AncientWallDoor : Obs.ClearanceClass.Other;

        private static bool AncientDanger(Map map, Building building, Faction player)
        {
            var occupied = building.OccupiedRect();
            // The warning trigger can disappear when a colonist approaches,
            // before the room opens. Retain the room-content check as well.
            if (map.listerThings.AllThings.OfType<RectTrigger>().Any(t => t.destroyIfUnfogged
                && t.signalTag?.StartsWith("ancientTempleApproached-", StringComparison.Ordinal) == true
                && t.Rect.CenterCell.Fogged(map) && occupied.Any(c => t.Rect.Contains(c)))) return true;
            var rooms = new HashSet<Room>();
            foreach (var cell in occupied)
                foreach (var offset in GenAdj.CardinalDirectionsAndInside) {
                    var near = cell + offset;
                    if (near.InBounds(map) && near.GetRoom(map) is Room room) rooms.Add(room);
                }
            foreach (var room in rooms) {
                if (room.TouchesMapEdge || room.OpenRoofCount != 0) continue;
                foreach (var thing in room.ContainedAndAdjacentThings) {
                    if (thing is Building_AncientCryptosleepCasket casket && casket.HasAnyContents) return true;
                    if (room.Fogged && (thing is Hive || thing is Pawn pawn && pawn.Faction != null && pawn.Faction.HostileTo(player))) return true;
                }
            }
            return false;
        }
        private static Common.Cell Cell(int x, int z) => new Common.Cell { X = x, Z = z };
        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static object Missing(Common.UnavailableReason reason, string detail) => ProtoBoundary.Encode(new Obs.ClearanceTargetsReply { Unavailable = new Common.Unavailable { Reason = reason, Detail = detail } });
        private static void Require(bool condition, string message) { if (!condition) throw new ReadLimit(message); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
