using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using Common = RimGovernor.Contracts.Wire.Common;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Read-only census behind Observations/ListWallUpgradeSites, on the
    // WallUpgradeSafety geometry. Two row
    // kinds serve the two WallRemoval steps the boundary re-validates by
    // exact (origin, normal) geometry:
    //  - with target_id: replacement candidates for that one colonist wall,
    //    one row per admissible normal, target == original, listed while the
    //    backup cells are open ground and again once same-stuff stone
    //    backups stand in all of them (completed_backups, demolition ready);
    //  - without target_id: cleanup sites, a completed stone wall whose
    //    straight-side backup cells still hold same-stuff colonist stone
    //    walls; target is the first remaining backup in backup-cell order so
    //    successive reads name each backup in turn.
    // A standing deconstruct designation no removal record claims is adopted by
    // the RemoveWall operation; designated and removal_id report it.
    // Workers, stock, geometry and the roof-support
    // snapshot are not projected: the brain owns builders and stock (its material
    // budget); cells come through GetCells.
    internal static class NativeWallUpgradeObservationTools
    {
        // A refused request (invalid, or a target id no colonist wall carries) throws NativeRefusedException; a census
        // that cannot be read completely throws an unavailable fault.
        internal static Obs.WallUpgradeSnapshot HandleListWallUpgradeSites(Obs.WallUpgradeSitesRequest request)
        {
            if (!Validate(request, out var failure)) throw NativeFaults.Refused(failure);
            if (!ProtoBoundary.ValidateIdentity(request.Scope?.ExpectedIdentity, out var map, out var context, out var error))
                throw NativeFaults.Refused(error);
            if (Faction.OfPlayerSilentFail == null || map.listerBuildings == null || map.roofGrid == null || map.zoneManager == null)
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.NativeComponentMissing, Detail = "Player faction or map structure trackers are unavailable." });
            var walls = map.listerBuildings.allBuildingsColonist.Where(b => b.def == ThingDefOf.Wall && b.Spawned).ToList();
            Building? target = null;
            if (request.TargetId is not null && (target = walls.ById(request.TargetId)) == null)
                throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.NotFound, "No spawned colonist wall with that id is on the current map."));
            try
            {
                var rows = new List<Obs.WallUpgradeSite>();
                if (target != null)
                {
                    foreach (var normal in WallUpgradeSafety.Directions)
                        if (Replacement(map, target, normal, context) is Obs.WallUpgradeSite row) rows.Add(row);
                }
                else
                {
                    foreach (var wall in walls.Where(WallUpgradeSafety.Stone).OrderBy(b => b.GetUniqueLoadID(), StringComparer.Ordinal))
                        foreach (var normal in WallUpgradeSafety.Directions.Where(n => !WallUpgradeSafety.Corner(n)))
                            if (Cleanup(map, wall, normal, context) is Obs.WallUpgradeSite row) rows.Add(row);
                }
                return new Obs.WallUpgradeSnapshot { Context = context, Sites = [.. rows] };
            }
            catch (Exception readError)
            {
                ModLog.Error("observe", "Wall upgrade census read failed: " + readError);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed,
                    Detail = PlacementPreviewOperation.Diagnostic("Wall, room or roof facts could not be read completely: " + readError.GetType().Name + ": " + readError.Message) });
            }
        }

        internal static bool Validate(Obs.WallUpgradeSitesRequest request, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity, optional target id are required.");
            return request?.Scope?.ExpectedIdentity != null
                && (request.TargetId is null || ProtoBoundary.IsIdentifier(request.TargetId));
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

        internal static Obs.WallUpgradeSite? Replacement(Map map, Building wall, IntVec3 normal, Common.ObservationContext context)
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
            // committed to it.
            else blocker = RoofSupportSafety.Blocker(wall, backups == null ? cells : null, out _);
            return Site(map, wall, wall, normal, cells, context, backups, null, blocker);
        }

        private static bool Open(Map map, IntVec3 cell) => cell.Standable(map) && map.zoneManager.ZoneAt(cell) == null
            && !cell.GetThingList(map).Any(t => t is Building || t is Blueprint || t is Frame || t is Plant || t.def.category == ThingCategory.Item);

        /// <summary>Every backup cell's same-stuff colonist stone wall, or null when any cell lacks one.</summary>
        internal static List<Building>? CompletedBackups(Map map, List<IntVec3> cells)
        {
            if (cells.Count == 0) return null;
            var backups = cells.Select(c => ColonistWall(map, c)).ToList();
            if (backups.Any(b => b == null || !WallUpgradeSafety.Stone(b) || b.Stuff != (backups[0] ?? throw new System.InvalidOperationException("Required native value is unavailable: backups[0].")).Stuff)) return null;
            return backups.Select(b => (b ?? throw new System.InvalidOperationException("Required native value is unavailable: b."))).ToList();
        }

        internal static Obs.WallUpgradeSite? Cleanup(Map map, Building permanent, IntVec3 normal, Common.ObservationContext context)
        {
            var origin = permanent.Position;
            if (!Interior(map, origin - normal)) return null;
            if (ColonistWall(map, WallUpgradeSafety.LeftCell(origin, normal)) == null || ColonistWall(map, WallUpgradeSafety.RightCell(origin, normal)) == null) return null;
            var cells = WallUpgradeSafety.BackupCells(origin, normal).ToList();
            var backups = cells.Select(c => ColonistWall(map, c)).OfType<Building>().Where(b => WallUpgradeSafety.Stone(b) && b.Stuff == permanent.Stuff).ToList();
            if (backups.Count == 0) return null;
            return Site(map, backups[0], permanent, normal, cells, context, backups, permanent, RoofSupportSafety.Blocker(backups[0], out _));
        }

        private static Obs.WallUpgradeSite Site(Map map, Building target, Building original, IntVec3 normal, List<IntVec3> cells, Common.ObservationContext context, List<Building>? backups, Building? replacement, string? blocker) =>
            new Obs.WallUpgradeSite {
                Target = NativeBuildingObservationTools.Ref(target),
                Origin = new DomainCell(original.Position.x, original.Position.z),
                Normal = new DomainCell(normal.x, normal.z),
                BackupCells = [.. cells.Select(c => new DomainCell(c.x, c.z))],
                Blocker = blocker,
                Designated = map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) != null,
                RemovalId = WallUpgradeSafety.Pending(target)?.Id,
                Original = NativeBuildingObservationTools.Ref(original),
                LeftSupport = NativeBuildingObservationTools.Ref((ColonistWall(map, WallUpgradeSafety.LeftCell(original.Position, normal)) ?? throw new System.InvalidOperationException("Required native value is unavailable: ColonistWall(map, WallUpgradeSafety.LeftCell(original.Position, normal))."))),
                RightSupport = NativeBuildingObservationTools.Ref((ColonistWall(map, WallUpgradeSafety.RightCell(original.Position, normal)) ?? throw new System.InvalidOperationException("Required native value is unavailable: ColonistWall(map, WallUpgradeSafety.RightCell(original.Position, normal))."))),
                CompletedBackups = [.. (backups ?? new List<Building>()).Select(NativeBuildingObservationTools.Ref)],
                Replacement = replacement == null ? null : NativeBuildingObservationTools.Ref(replacement),
                TargetSnapshot = NativeBuildingObservationTools.Token(target, context),
            };

        // SiteToken is the wall removal receipt's site version: the target's
        // own token plus the site's geometry, designation and backups.
        internal static string SiteToken(Obs.WallUpgradeSite row, Common.ObservationContext context) =>
            NativeObservationSnapshot.Snapshot("wall-site", context, row.Target.Id ?? "", w => {
                w.Write(row.Original.Id ?? ""); w.Write(row.Normal.X); w.Write(row.Normal.Z); w.Write(row.TargetSnapshot.Token ?? "");
                w.Write(row.Designated); w.Write(row.RemovalId ?? "");
                w.Write(row.LeftSupport.Id ?? ""); w.Write(row.RightSupport.Id ?? "");
                foreach (var backup in row.CompletedBackups) w.Write(backup.Id ?? "");
            }).Token ?? "";

        // The stony stuffs a wall may be rebuilt from, by name: the order a corner replacement tries them in.
        internal static List<ThingDef> StonyStuffs() =>
            GenStuff.AllowedStuffsFor(ThingDefOf.Wall).Where(s => s.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) == true)
                .OrderBy(s => s.defName, StringComparer.Ordinal).ToList();
    }
}
