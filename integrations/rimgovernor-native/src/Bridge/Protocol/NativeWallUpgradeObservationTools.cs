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
    // Read-only census behind Observations/ListWallUpgradeSites, sharing the
    // legacy home/wall_upgrade_sites geometry (WallUpgradeSafety). Two row
    // kinds serve the two WallRemoval steps the Go boundary re-validates by
    // exact (origin, normal) geometry:
    //  - with target_id: replacement candidates for that one colonist wall,
    //    one row per admissible normal, target == original, listed while the
    //    backup cells are open ground and again once same-stuff stone
    //    backups stand in all of them (completed_backups, demolition ready);
    //  - without target_id: cleanup sites, a completed stone wall whose
    //    straight-side backup cells still hold same-stuff colonist stone
    //    walls; target is the first remaining backup in backup-cell order so
    //    successive reads name each backup in turn.
    // player_owned marks a deconstruct designation the native removal ledger
    // does not claim. It remains untouched until an explicit RemoveWall
    // operation adopts it with a receipt. Workers, stock, geometry and the roof-support
    // snapshot are not projected: builders are checked at admission, stock
    // through ListSupplies and cells through GetCells.
    public sealed class NativeWallUpgradeObservationTools
    {
        internal const string ToolName = "rimgovernor/observations_list_wall_upgrade_sites";
        private const int MaxRows = 256;
        private const int MaxWalls = 8192;

        public NativeWallUpgradeObservationTools() { WallUpgradeSafety.Install(); }

        [Tool(ToolName, Title = "Read typed wall upgrade sites", Description = "Bounded stone-shell replacement geometry: with target_id, every admissible replacement normal of that colonist wall (enclosed roofed interior, exterior backup cells, colonist side supports, corner access) with stone material costs; without it, completed permanent walls whose backups remain, naming the next backup to clear. No designation or work is admitted.")]
        [ToolResponse("payload", "string", "Official ProtoJSON WallUpgradeSitesReply.", Always = true)]
        public async Task<object> ListSites(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Official ProtoJSON WallUpgradeSitesRequest string in raw transport value.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, ToolName, request, Obs.WallUpgradeSitesRequest.Parser, out var parsed, out var failure)
                || !Validate(parsed, out failure)) return ProtoBoundary.Encode(new Obs.WallUpgradeSitesReply { Failure = failure });
            return await ProtoBoundary.OnMainThread(ctx, () => {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                    return ProtoBoundary.Encode(new Obs.WallUpgradeSitesReply { Failure = error });
                try
                {
                    if (Faction.OfPlayerSilentFail == null || map.listerBuildings == null || map.roofGrid == null || map.zoneManager == null)
                        return ProtoBoundary.Encode(new Obs.WallUpgradeSitesReply { Unavailable = Unavailable(Common.UnavailableReason.NativeComponentMissing, "Player faction or map structure trackers are unavailable.") });
                    var walls = map.listerBuildings.allBuildingsColonist.Where(b => b.def == ThingDefOf.Wall && b.Spawned).ToList();
                    Require(walls.Count <= MaxWalls, "Colonist wall census exceeds " + MaxWalls + " walls.");
                    var rows = new List<Obs.WallUpgradeSite>();
                    if (parsed.HasTargetId)
                    {
                        var wall = walls.FirstOrDefault(b => b.GetUniqueLoadID() == parsed.TargetId);
                        if (wall == null)
                            return ProtoBoundary.Encode(new Obs.WallUpgradeSitesReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No spawned colonist wall with that id is on the current map.") });
                        var materials = Materials();
                        foreach (var normal in WallUpgradeSafety.Directions)
                            if (Replacement(map, wall, normal, materials, context) is Obs.WallUpgradeSite row) rows.Add(row);
                    }
                    else
                    {
                        foreach (var wall in walls.Where(WallUpgradeSafety.Stone).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
                            foreach (var normal in WallUpgradeSafety.Directions.Where(n => !WallUpgradeSafety.Corner(n)))
                                if (Cleanup(map, wall, normal, context) is Obs.WallUpgradeSite row) rows.Add(row);
                    }
                    var limit = parsed.Page?.HasLimit == true ? (int)parsed.Page.Limit : MaxRows;
                    Require(rows.Count <= limit, "Wall upgrade site census exceeds the page limit; sites are never sampled.");
                    var snapshot = new Obs.WallUpgradeSnapshot { Context = context, Completeness = Complete(rows.Count) };
                    snapshot.Sites.AddRange(rows);
                    return Encode(new Obs.WallUpgradeSitesReply { Observed = snapshot });
                }
                catch (ReadLimit limit) { return ProtoBoundary.Encode(new Obs.WallUpgradeSitesReply { Unavailable = Unavailable(Common.UnavailableReason.LimitExceeded, limit.Message) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.WallUpgradeSitesReply { Unavailable = Unavailable(Common.UnavailableReason.ReadFailed, "Wall, room or roof facts could not be read completely.") }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static Building? ColonistWallById(Map map, string id) =>
            map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.def == ThingDefOf.Wall && b.Spawned && b.GetUniqueLoadID() == id);

        internal static bool Validate(Obs.WallUpgradeSitesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, optional target id and page limit 1..256 are required; cursors are unsupported.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.Page == null || (!request.Page.HasLimit || request.Page.Limit >= 1 && request.Page.Limit <= MaxRows) && (!request.Page.HasCursor || request.Page.Cursor.Length == 0))
                && (!request.HasTargetId || ProtoBoundary.IsIdentifier(request.TargetId));
        }

        internal static bool Interior(Map map, IntVec3 inside)
        {
            if (!inside.InBounds(map) || inside.Fogged(map) || !inside.Roofed(map) || !inside.Standable(map)) return false;
            var room = inside.GetRoom(map);
            return room != null && !room.TouchesMapEdge && room.OpenRoofCount == 0;
        }

        internal static Building? ColonistWall(Map map, IntVec3 cell)
        {
            if (!cell.InBounds(map)) return null;
            var edifice = cell.GetEdifice(map);
            return edifice != null && edifice.def == ThingDefOf.Wall && edifice.Faction == Faction.OfPlayer ? edifice : null;
        }

        internal static Obs.WallUpgradeSite? Replacement(Map map, Building wall, IntVec3 normal, List<Obs.WallMaterialOption> materials, Common.ObservationContext context)
        {
            var origin = wall.Position;
            var outside = origin + normal;
            if (!Interior(map, origin - normal) || !outside.InBounds(map)) return null;
            var left = ColonistWall(map, WallUpgradeSafety.LeftCell(origin, normal));
            var right = ColonistWall(map, WallUpgradeSafety.RightCell(origin, normal));
            if (left == null || right == null) return null;
            var cells = WallUpgradeSafety.BackupCells(origin, normal).ToList();
            if (cells.Any(c => !c.InBounds(map) || c.Fogged(map) || !RoofSupportSafety.GeometryKnown(map, c))) return null;
            // A straight site is listed while its backup cells are still open
            // exterior ground (a fresh candidate) and again once every cell
            // holds a same-stuff colonist stone wall (demolition ready, the
            // exterior now being the backups themselves); anything in between
            // is not a site.
            var backups = CompletedBackups(map, cells);
            if (backups == null && (outside.GetRoom(map)?.TouchesMapEdge != true || !cells.All(c => Open(map, c)))) return null;
            string? blocker = null;
            if (WallUpgradeSafety.Corner(normal))
            {
                blocker = RoofSupportSafety.Blocker(wall, out _);
                if (blocker == null && !WallUpgradeSafety.CornerAccess(map, origin, normal)) blocker = "Corner salvage and construction access is unavailable";
            }
            // A straight site's demolition waits for its backups, so a fresh
            // candidate is judged with them standing: a wall whose removal
            // would still drop a roof (a fixture-laid or distant-held roof
            // within support range) is not a site, and no backups are ever
            // committed to it (#293).
            else blocker = RoofSupportSafety.Blocker(wall, backups == null ? cells : null, out _);
            var row = Site(map, wall, wall, normal, cells, context, backups);
            if (blocker != null) row.Blocker = blocker;
            row.ReplacementMaterials.AddRange(materials);
            return row;
        }

        private static bool Open(Map map, IntVec3 cell) => cell.Standable(map) && map.zoneManager.ZoneAt(cell) == null
            && !cell.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t is Plant || t.def.category == ThingCategory.Item);

        /// <summary>Every backup cell's same-stuff colonist stone wall, or null when any cell lacks one.</summary>
        internal static List<Building>? CompletedBackups(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return null;
            var backups = cells.Select(c => ColonistWall(map, c)).ToList();
            if (backups.Any(b => b == null || !WallUpgradeSafety.Stone(b) || b.Stuff != backups[0]!.Stuff)) return null;
            return backups.Select(b => b!).ToList();
        }

        internal static Obs.WallUpgradeSite? Cleanup(Map map, Building permanent, IntVec3 normal, Common.ObservationContext context)
        {
            var origin = permanent.Position;
            if (!Interior(map, origin - normal)) return null;
            if (ColonistWall(map, WallUpgradeSafety.LeftCell(origin, normal)) == null || ColonistWall(map, WallUpgradeSafety.RightCell(origin, normal)) == null) return null;
            var cells = WallUpgradeSafety.BackupCells(origin, normal).ToList();
            var backups = cells.Select(c => ColonistWall(map, c)).Where(b => b != null && WallUpgradeSafety.Stone(b) && b.Stuff == permanent.Stuff).Select(b => b!).ToList();
            if (backups.Count == 0) return null;
            var row = Site(map, backups[0], permanent, normal, cells, context, backups);
            row.Replacement = NativeBuildingObservationTools.Project(permanent);
            var blocker = RoofSupportSafety.Blocker(backups[0], out _);
            if (blocker != null) row.Blocker = blocker;
            return row;
        }

        private static Obs.WallUpgradeSite Site(Map map, Building target, Building original, IntVec3 normal, List<IntVec3> cells, Common.ObservationContext context, List<Building>? backups)
        {
            var row = new Obs.WallUpgradeSite { TargetPresent = true, Normal = new Common.Cell { X = normal.x, Z = normal.z },
                Original = NativeBuildingObservationTools.Project(original),
                LeftSupport = NativeBuildingObservationTools.Project(ColonistWall(map, WallUpgradeSafety.LeftCell(original.Position, normal))!),
                RightSupport = NativeBuildingObservationTools.Project(ColonistWall(map, WallUpgradeSafety.RightCell(original.Position, normal))!) };
            row.Target = row.Original.Building.Id == target.GetUniqueLoadID() ? row.Original.Building.Clone() : NativeBuildingObservationTools.Project(target).Building;
            row.Target.Snapshot = NativeBuildingObservationTools.Token(target, context);
            foreach (var cell in cells) row.BackupCells.Add(new Common.Cell { X = cell.x, Z = cell.z });
            foreach (var backup in backups ?? new List<Building>()) row.CompletedBackups.Add(NativeBuildingObservationTools.Project(backup));
            var designation = map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct);
            var pending = WallUpgradeSafety.Pending(target);
            row.Designated = designation != null;
            if (pending != null) row.RemovalId = pending.Id;
            // A designation no record of ours claims: evidence of an old order,
            // adopted by admission rather than preserved (#461).
            row.PlayerOwned = designation != null && pending == null;
            row.Snapshot = NativeObservationSnapshot.Snapshot("wall-site", context, target.GetUniqueLoadID(), w => {
                w.Write(original.GetUniqueLoadID()); w.Write(normal.x); w.Write(normal.z); w.Write(target.HitPoints);
                w.Write(row.Designated); w.Write(row.PlayerOwned); w.Write(row.RemovalId ?? "");
                w.Write(row.LeftSupport.Building.Id); w.Write(row.RightSupport.Building.Id);
                foreach (var backup in row.CompletedBackups) w.Write(backup.Building.Id);
            });
            return row;
        }

        internal static List<Obs.WallMaterialOption> Materials()
        {
            var options = new List<Obs.WallMaterialOption>();
            foreach (var stuff in GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(s => s.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) == true).OrderBy(s => s.defName, StringComparer.Ordinal))
            {
                var option = new Obs.WallMaterialOption { Stuff = Id(stuff.defName) };
                foreach (var cost in ThingDefOf.Wall.CostListAdjusted(stuff))
                    option.Costs.Add(new Obs.Quantity { DefName = Id(cost.thingDef.defName), Units = cost.count });
                options.Add(option);
            }
            Require(options.Count <= MaxRows, "Stone material catalog exceeds bound.");
            return options;
        }

        private static string Id(string value) => ProtoBoundary.IsIdentifier(value) ? value : throw new InvalidOperationException("Native identifier unavailable.");
        private static Common.Unavailable Unavailable(Common.UnavailableReason reason, string detail) => new Common.Unavailable { Reason = reason, Detail = detail };
        private static Obs.Completeness Complete(int count) => new Obs.Completeness { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };
        private static object Encode(IMessage reply)
        {
            Require(Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) <= 1024 * 1024, "Complete reply exceeds 1 MiB.");
            return ProtoBoundary.Encode(reply);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new ReadLimit(message); }
        private sealed class ReadLimit : Exception { internal ReadLimit(string message) : base(message) {} }
    }
}
