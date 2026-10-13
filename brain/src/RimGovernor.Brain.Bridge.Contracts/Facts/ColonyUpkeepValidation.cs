using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go colony_upkeep.go and colony_comfort.go: the validators of the upkeep census (items, structures, fires, filth,
// people, beds, animals, home coverage, lighting, flooring and routes) and of its comfort section.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>validRefs</c>: whether every reference is valid and distinct.</summary>
    private static bool ValidRefs(IEnumerable<Ref> references)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (!ValidRef(reference) || !seen.Add((reference.Id ?? "")))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Go's <c>validateDirectUpkeep</c>: the rows of the upkeep census, each identified once, and the sections it nests.</summary>
    private static void ValidateDirectUpkeep(UpkeepFacts v, MapSize? size)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["items"] = v.Items.Count,
            ["structures"] = v.Structures.Count,
            ["fires"] = v.Fires.Count,
            ["filth"] = v.Filth.Count,
            ["animals"] = v.Animals.Count,
            ["people"] = v.People.Count + v.Slaves.Count + v.Guests.Count,
            ["beds"] = v.Beds.Count,
        };
        if (v.HomeCoverage is not null)
        {
            counts["home_coverage"] = 1;
        }
        if (v.Lighting is not null)
        {
            counts["lighting"] = 1;
        }
        if (v.Flooring is not null)
        {
            counts["flooring"] = 1;
        }
        if (v.Routes is not null)
        {
            counts["routes"] = 1;
        }
        if (v.Issues.Any(issue => counts.TryGetValue((issue.Field ?? ""), out var count) && count != 0))
        {
            throw Contract("unavailable upkeep section contains rows");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Items)
        {
            if (row is null
                || !UniqueRef(row.Item, seen)
                || (row.Count is not null && row.Count.GetValueOrDefault() < 0)
                || (row.RotTicks is not null && row.RotTicks.GetValueOrDefault() < 0)
                || !OnlyFields(row, "item", "count", "roofed", "in_storage", "rot_ticks", "forbidden"))
            {
                throw Contract("invalid upkeep item");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Structures)
        {
            if (row is null || row.Building is null)
            {
                throw Contract("missing upkeep structure");
            }
            if (!UniqueRef(row.Building, seen) || !OnlyFields(row, "building", "home"))
            {
                throw Contract("invalid upkeep structure");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Fires)
        {
            if (row is null || !UniqueRef(row.Fire, seen) || !NonNegativeNumber(row.Size is not null, row.Size.GetValueOrDefault()) || !OnlyFields(row, "fire", "size", "home"))
            {
                throw Contract("invalid upkeep fire");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Filth)
        {
            if (row is null
                || !UniqueRef(row.Filth, seen)
                || (row.RoomRole is not null && !IsValidId((row.RoomRole ?? "")))
                || !OptionalRef(row.Room)
                || !OnlyFields(row, "filth", "home", "thickness", "room_role", "room"))
            {
                throw Contract("invalid upkeep filth");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.People.Concat(v.Slaves).Concat(v.Guests))
        {
            if (row is null
                || !UniqueRef(row.Pawn, seen)
                || !OptionalRef(row.OwnedBed)
                || !FiniteNumber(row.ComfortableMinC is not null, row.ComfortableMinC.GetValueOrDefault())
                || !FiniteNumber(row.ComfortableMaxC is not null, row.ComfortableMaxC.GetValueOrDefault())
                || !FiniteNumber(row.TemperatureC is not null, row.TemperatureC.GetValueOrDefault())
                || (row.ComfortableMinC is not null && row.ComfortableMaxC is not null && row.ComfortableMinC.GetValueOrDefault() > row.ComfortableMaxC.GetValueOrDefault())
                || !ValidRefs(row.Partners)
                || !ValidTitle(row)
                || !OnlyFields(
                    row,
                    "pawn",
                    "owned_bed",
                    "comfortable_min_c",
                    "comfortable_max_c",
                    "temperature_c",
                    "partners",
                    "bed_sharing_allowed",
                    "royal_title",
                    "ascetic",
                    "precepts"))
            {
                throw Contract("invalid sleeping person");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Beds)
        {
            if (row is null
                || !UniqueRef(row.Bed, seen)
                || !FiniteNumber(row.TemperatureC is not null, row.TemperatureC.GetValueOrDefault())
                || !ValidRefs(row.Owners)
                || !ValidRefs(row.Users)
                || !ValidRefs(row.AccessibleTo)
                || !OptionalRef(row.Room)
                || (row.Quality is not null && !IsValidId((row.Quality ?? "")))
                || (row.Stuff is not null && !IsValidId((row.Stuff ?? "")))
                || !OnlyFields(
                    row,
                    "bed",
                    "medical",
                    "prisoners",
                    "roofed",
                    "temperature_c",
                    "owners",
                    "users",
                    "accessible_to",
                    "room",
                    "quality",
                    "stuff",
                    "for_slaves"))
            {
                throw Contract("invalid upkeep bed");
            }
        }
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.Animals)
        {
            if (row is null || !UniqueRef(row.Pawn, seen) || !OptionalRef(row.SuitablePen))
            {
                throw Contract("invalid upkeep animal");
            }
            if ((row.RequiresPen is not null && !row.RequiresPen.GetValueOrDefault() && row.SuitablePen is not null) || !OnlyFields(row, "pawn", "requires_pen", "suitable_pen"))
            {
                throw Contract("conflicting upkeep animal fields");
            }
        }

        // A wild row is the factionless tame census: its diet only, never feed, pen or ownership facts; the tame facts
        // ride the table row.
        seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in v.WildAnimals)
        {
            if (row is null || !UniqueRef(row.Pawn, seen) || (row.RequiresPen is not null && row.RequiresPen.GetValueOrDefault()) || !OnlyFields(row, "pawn", "requires_pen"))
            {
                throw Contract("invalid wild animal");
            }
        }
        if (v.HomeCoverage is not null)
        {
            ValidateHomeCoverage(v.HomeCoverage, size);
        }
        if (v.Lighting is not null)
        {
            ValidateLighting(v.Lighting, size);
        }
        if (v.Flooring is not null)
        {
            ValidateFlooring(v.Flooring, size);
        }
        if (v.Routes is not null)
        {
            ValidateRoutes(v.Routes, size);
        }
    }

    private static void ValidateHomeCoverage(HomeCoverageSection section, MapSize? size)
    {
        var h = section.Outcome?.Observed;
        if (h is null)
        {
            ValidateUnavailable(section.Outcome?.Unavailable);
            return;
        }
        if (h.Revision is null || h.Revision.GetValueOrDefault() < 0)
        {
            throw Contract("invalid Home coverage census");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in h.Targets)
        {
            if (row is null
                || !IsValidId((row.Id ?? ""))
                || seen.Contains((row.Id ?? ""))
                || row.Snapshot is not null
                || (row.ShapeToken is not null && !IsValidId((row.ShapeToken ?? "")))
                || !Diagnostic(row.Blocker is not null, (row.Blocker ?? ""))
                || (row.ExcludedCells is not null && row.MissingCells is not null && row.ExcludedCells.GetValueOrDefault() > row.MissingCells.GetValueOrDefault()))
            {
                throw Contract("invalid Home coverage target");
            }
            seen.Add((row.Id ?? ""));
            if (row.ExtentGeometry is { } g)
            {
                if ((row.Blocker ?? "").Length != 0)
                {
                    throw Contract("invalid complete Home extent geometry");
                }
                var geometryCells = new HashSet<(int, int)>();
                foreach (var cell in g.EnclosedInterior.Concat(g.Corridor).Concat(g.Zone))
                {
                    if (!ColonyCell(cell, size) || !geometryCells.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
                    {
                        throw Contract("invalid Home extent cell");
                    }
                }
            }
            var cells = new HashSet<(int, int)>();
            foreach (var cell in row.Cells)
            {
                if (!ColonyCell(cell, size))
                {
                    throw Contract("invalid Home coverage cell");
                }
                if (!cells.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
                {
                    throw Contract("duplicate Home cell");
                }
            }
            if (row.Cells.Count > 0 && row.MissingCells is not null && row.MissingCells.GetValueOrDefault() > (uint)row.Cells.Count)
            {
                throw Contract("Home deficit exceeds footprint");
            }
        }
    }

    /// <summary>Go's <c>validTitle</c>: a person's royal title and each precept are definition names.</summary>
    private static bool ValidTitle(UpkeepPerson p) =>
        (p.RoyalTitle is null || IsValidId((p.RoyalTitle ?? ""))) && p.Precepts.All(IsValidId);

    /// <summary>
    /// Go's <c>validateRoutes</c>: unique facility refs at in-map cells, each travel row naming a listed pawn once
    /// with non-negative path numbers only when reachable (path_skipped only on a reachable row with none), breach
    /// cells unique and in the map, traffic cells unique and in the map.
    /// </summary>
    private static void ValidateRoutes(RoutesSection section, MapSize? size)
    {
        var f = section.Outcome?.Observed;
        if (f is null)
        {
            ValidateUnavailable(section.Outcome?.Unavailable);
            return;
        }
        if (!OnlyFields(f, "facilities", "pawn_ids", "traffic", "traffic_samples", "traffic_since_tick") || (f.TrafficSinceTick is not null && f.TrafficSinceTick.GetValueOrDefault() < 0))
        {
            throw Contract("invalid routes facts");
        }
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in f.PawnIds)
        {
            if (!IsValidId(id) || !pawns.Add(id))
            {
                throw Contract("invalid routes pawn");
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in f.Facilities)
        {
            if (row is null
                || !UniqueRef(row.Facility, seen)
                || RouteKindName(row.Kind.GetValueOrDefault()).Length == 0
                || !ColonyCell(row.Cell, size)
                || !OptionalRef(row.Room)
                || !OnlyFields(row, "facility", "kind", "cell", "room", "travel", "breaches"))
            {
                throw Contract("invalid routes facility");
            }
            var travelled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in row.Travel)
            {
                if (t is null
                    || !pawns.Contains((t.PawnId ?? ""))
                    || travelled.Contains((t.PawnId ?? ""))
                    || (t.PathCost is not null && (t.PathCost.GetValueOrDefault() < 0 || !t.Reachable.GetValueOrDefault()))
                    || (t.PathCells is not null && (t.PathCells.GetValueOrDefault() < 0 || !t.Reachable.GetValueOrDefault()))
                    || (t.PathSkipped is not null && (!t.Reachable.GetValueOrDefault() || t.PathCost is not null || t.PathCells is not null))
                    || !OnlyFields(t, "pawn_id", "reachable", "path_cost", "path_cells", "path_skipped"))
                {
                    throw Contract("invalid routes travel");
                }
                travelled.Add((t.PawnId ?? ""));
            }
            var cells = new HashSet<(int, int)>();
            foreach (var b in row.Breaches)
            {
                if (b is null
                    || !ColonyCell(b.Cell, size)
                    || b.Edifice is null
                    || !IsValidId((b.Edifice ?? ""))
                    || (b.Pending is not null && !IsValidId((b.Pending ?? "")))
                    || (b.Distance is not null && b.Distance.GetValueOrDefault() < 0)
                    || !OnlyFields(b, "cell", "edifice", "pending", "distance"))
                {
                    throw Contract("invalid routes breach");
                }
                if (!cells.Add((b.Cell.X.GetValueOrDefault(), b.Cell.Z.GetValueOrDefault())))
                {
                    throw Contract("routes breaches overlap");
                }
            }
        }

        // A cell appears once per traffic layer.
        var layered = new HashSet<(int, int, int)>();
        foreach (var t in f.Traffic)
        {
            if (t is null
                || !ColonyCell(t.Cell, size)
                || t.Layer < TrafficLayer.Colonist
                || t.Layer > TrafficLayer.Hostile
                || (t.Terrain is not null && !IsValidId((t.Terrain ?? "")))
                || (t.Pending is not null && !IsValidId((t.Pending ?? "")))
                || !OnlyFields(t, "cell", "samples", "terrain", "home", "pending", "layer"))
            {
                throw Contract("invalid traffic cell");
            }
            if (!layered.Add(((int)t.Layer, t.Cell.X.GetValueOrDefault(), t.Cell.Z.GetValueOrDefault())))
            {
                throw Contract("traffic cells overlap");
            }
        }
    }

    /// <summary>Go's <c>validateFlooring</c>: unique rooms of unique in-map cells, each naming a terrain (the catalog prices it).</summary>
    private static void ValidateFlooring(FlooringSection section, MapSize? size)
    {
        var f = section.Outcome?.Observed;
        if (f is null)
        {
            ValidateUnavailable(section.Outcome?.Unavailable);
            return;
        }
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<(int, int)>();
        foreach (var room in f.Rooms)
        {
            if (room is null
                || !IsValidId(room.Room?.Id ?? "")
                || rooms.Contains((room.Room!.Id ?? ""))
                || (room.Role is not null && !IsValidId((room.Role ?? "")))
                || !OnlyFields(room, "room", "role", "cells"))
            {
                throw Contract("invalid flooring room");
            }
            rooms.Add((room.Room.Id ?? ""));
            foreach (var cell in room.Cells)
            {
                if (cell is null
                    || !ColonyCell(cell.Cell, size)
                    || cell.Terrain is null
                    || !IsValidId((cell.Terrain ?? ""))
                    || (cell.Pending is not null && !IsValidId((cell.Pending ?? "")))
                    || !OnlyFields(cell, "cell", "terrain", "pending"))
                {
                    throw Contract("invalid flooring cell");
                }
                if (!cells.Add((cell.Cell.X.GetValueOrDefault(), cell.Cell.Z.GetValueOrDefault())))
                {
                    throw Contract("flooring cells overlap");
                }
            }
        }
    }

    /// <summary>
    /// Go's <c>validateLighting</c>: work cells are unique bench refs at in-map cells with a unit glow, lamps are
    /// unique building states carrying only the service detail the power census also allows.
    /// </summary>
    private static void ValidateLighting(LightingSection section, MapSize? size)
    {
        var l = section.Outcome?.Observed;
        if (l is null)
        {
            ValidateUnavailable(section.Outcome?.Unavailable);
            return;
        }
        var benches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in l.WorkCells)
        {
            if (row is null
                || !UniqueRef(row.Bench, benches)
                || !ColonyCell(row.Cell, size)
                || !OptionalRef(row.Room)
                || !OnlyFields(row, "bench", "cell", "glow", "roofed", "room", "plant_defs"))
            {
                throw Contract("invalid lighting work cell");
            }
            if (row.Glow is not null && (double.IsNaN(row.Glow.GetValueOrDefault()) || row.Glow.GetValueOrDefault() < 0 || row.Glow.GetValueOrDefault() > 1))
            {
                throw Contract("invalid lighting glow");
            }
        }
        var lamps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in l.Lamps)
        {
            if (row is null || !UniqueRef(row.Building, lamps) || !OptionalRef(row.Room) || !OnlyFields(row, "building", "lit", "room"))
            {
                throw Contract("invalid lighting lamp");
            }
        }
    }

    /// <summary>
    /// Go's <c>validateColonyUpkeep</c>: the upkeep projection carries only the fields this port reads, with
    /// in-map unique home cells and a valid comfort section.
    /// </summary>
    private static void ValidateColonyUpkeep(UpkeepFacts? v, MapSize? size)
    {
        if (v is null
            || !OnlyFields(
                v,
                "comfort",
                "issues",
                "items",
                "structures",
                "fires",
                "filth",
                "animals",
                "people",
                "beds",
                "home_coverage",
                "lighting",
                "wild_animals",
                "flooring",
                "routes",
                "auto_home_area",
                "home_cells"))
        {
            throw Contract("unsupported upkeep projection");
        }
        var home = new HashSet<(int, int)>();
        foreach (var cell in v.HomeCells)
        {
            if (!ColonyCell(cell, size) || !home.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
            {
                throw Contract("invalid home cell");
            }
        }
        PawnsIssues(v.Issues, v);
        var comfort = v.Comfort?.Outcome?.Observed;
        if (comfort is null)
        {
            ValidateUnavailable(v.Comfort?.Outcome?.Unavailable);
            return;
        }
        var people = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in comfort.People)
        {
            if (!IsValidId(id) || !people.Add(id))
            {
                throw Contract("invalid comfort person");
            }
        }
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var surface in comfort.Surfaces)
        {
            var surfaceId = surface?.Id ?? "";
            if (surface is null || !IsValidId(surfaceId) || surfaces.Contains(surfaceId) || !OptionalRef(surface.Room))
            {
                throw Contract("invalid dining surface");
            }
            surfaces.Add(surfaceId);
            var cells = new HashSet<(int, int)>();
            foreach (var cell in surface.Adjacent)
            {
                if (!ColonyCell(cell, size) || !cells.Add((cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault())))
                {
                    throw Contract("invalid dining adjacency");
                }
            }
        }
        var facilityLists = new[] { comfort.Dining, comfort.Recreation };
        for (var kind = 0; kind < facilityLists.Length; kind++)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in facilityLists[kind])
            {
                var facilityId = f?.Id ?? "";
                if (f is null
                    || !IsValidId(facilityId)
                    || seen.Contains(facilityId)
                    || (kind == 0 && f.Kind is not null)
                    || (kind == 1 && !IsValidId(f.Kind ?? ""))
                    || !OptionalRef(f.Room))
                {
                    throw Contract("invalid comfort facility");
                }
                seen.Add(facilityId);
                foreach (var ids in new[] { RefIds(f.AccessibleTo), RefIds(f.Users) })
                {
                    var found = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var id in ids)
                    {
                        if (!people.Contains(id) || !found.Add(id))
                        {
                            throw Contract("invalid comfort access or use");
                        }
                    }
                }
            }
        }
        ValidateRecreationCensus(comfort, people);
    }

    /// <summary>Go's <c>validateRecreationCensus</c>: the joy kinds, the facilities that serve them and each pawn's tolerance per kind.</summary>
    private static void ValidateRecreationCensus(ComfortFacts v, HashSet<string> people)
    {
        var j = v.Joy;
        if (j is null)
        {
            return;
        }
        static BridgeContractException Bad() => Contract("invalid recreation kind census");
        if (j.Kinds.Count > 16)
        {
            throw Bad();
        }
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in j.Kinds)
        {
            if (!IsValidId(k) || !kinds.Add(k))
            {
                throw Bad();
            }
        }
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in v.Recreation)
        {
            if (!kinds.Contains((f.Kind ?? "")))
            {
                throw Bad();
            }
            available.Add((f.Kind ?? ""));
        }
        if (available.Count != kinds.Count)
        {
            throw Bad();
        }
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in j.Pawns)
        {
            if (p is null || !people.Contains(p.Pawn) || pawns.Contains(p.Pawn) || p.Tolerance.Count != j.Kinds.Count || p.Bored.Count != j.Kinds.Count)
            {
                throw Bad();
            }
            pawns.Add(p.Pawn);
            if (p.Tolerance.Any(t => !double.IsFinite(t) || t < 0 || t > 1))
            {
                throw Bad();
            }
        }
    }
}
