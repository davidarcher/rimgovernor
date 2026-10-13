namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    /// <summary>
    /// Decodes the upkeep censuses. A census whose frame lacks a field is unknown; one that needs a def the
    /// catalog cannot answer for throws naming the census and the def, never an unknown.
    /// </summary>
    internal static UpkeepObservation ColonyUpkeep(O.ColonyFactsSnapshot v, Tables tables)
    {
        var buildings = tables.Buildings;
        var r = new UpkeepObservation();
        var u = v.Upkeep?.Observed;
        if (u is null)
        {
            return r;
        }
        if (!HasIssue(u.Issues, "structures"))
        {
            var rows = new List<UpkeepStructure>();
            var known = true;
            var medical = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var bed in u.Beds)
            {
                medical[bed.Bed?.Id ?? ""] = bed.Medical;
            }
            foreach (var item in u.Structures)
            {
                if (!buildings.TryGetRow(item.Building, out var b) || !item.HasHome || b is null || !b.HasHitPoints || !b.HasMaxHitPoints)
                {
                    known = false;
                    break;
                }
                if (tables.Catalog is null)
                {
                    throw new DomainException("upkeep structures: no definition catalog was loaded");
                }
                RepairPriority priority;
                try
                {
                    priority = tables.Catalog.RepairPriority(b.Building?.DefName ?? "", medical.GetValueOrDefault(item.Building?.Id ?? ""));
                }
                catch (DomainException e)
                {
                    throw new DomainException($"upkeep structure {item.Building?.Id}: {e.Message}");
                }
                rows.Add(new UpkeepStructure
                {
                    Id = item.Building?.Id ?? "",
                    Definition = b.Building?.DefName ?? "",
                    Cell = CellOf(b.Building?.Position),
                    Home = item.Home,
                    HitPoints = b.HitPoints,
                    MaxHitPoints = b.MaxHitPoints,
                    Priority = priority,
                });
            }
            if (known)
            {
                r = r with { Structures = Known<IReadOnlyList<UpkeepStructure>>(rows) };
            }
        }
        if (!HasIssue(u.Issues, "fires"))
        {
            var rows = new List<UpkeepFire>();
            var known = true;
            foreach (var item in u.Fires)
            {
                if (!item.HasHome)
                {
                    known = false;
                    break;
                }
                rows.Add(new UpkeepFire { Id = item.Fire?.Id ?? "", Home = item.Home, Size = Optional(item.HasSize, item.Size) });
            }
            if (known)
            {
                r = r with { Fires = Known<IReadOnlyList<UpkeepFire>>(rows) };
            }
        }
        if (!HasIssue(u.Issues, "filth") && Headed(tables, u.Filth, f => f.Filth))
        {
            var rows = new List<UpkeepFilth>();
            var known = true;
            foreach (var item in u.Filth)
            {
                if (!item.HasHome || !item.HasThickness)
                {
                    known = false;
                    break;
                }
                var head = tables.Entity(item.Filth);
                rows.Add(new UpkeepFilth
                {
                    Id = item.Filth?.Id ?? "",
                    Definition = head?.DefName ?? "",
                    Cell = CellOf(head?.Position),
                    Home = item.Home,
                    Room = item.RoomRole,
                    RoomId = OptionalRef(item.Room),
                    Thickness = item.Thickness,
                });
            }
            if (known)
            {
                r = r with { Filth = Known<IReadOnlyList<UpkeepFilth>>(rows) };
            }
        }
        if (!HasIssue(u.Issues, "lighting"))
        {
            r = r with { Lighting = ColonyLighting(u.Lighting, buildings, tables.Catalog) };
        }
        if (!HasIssue(u.Issues, "routes"))
        {
            r = r with { Routes = ColonyRoutes(u.Routes) };
        }
        if (!HasIssue(u.Issues, "flooring"))
        {
            // The flooring census joins the routes census's traffic evidence, so a routes issue leaves flooring
            // unknown too rather than judging the traffic tier from nothing.
            var routes = HasIssue(u.Issues, "routes") ? new O.RoutesSection() : u.Routes;
            try
            {
                r = r with { Flooring = ColonyFlooring(u.Flooring, routes, tables.Catalog) };
            }
            catch (DomainException e)
            {
                throw new DomainException($"flooring census: {e.Message}");
            }
        }
        return r;
    }

    /// <summary>
    /// Decodes the flooring section; a cell missing its terrain leaves the whole census unknown so
    /// MaintainFlooring keeps its previous latch, and a terrain the catalog has no stat or def row for throws.
    /// </summary>
    private static Fact<FlooringObservation> ColonyFlooring(O.FlooringSection? section, O.RoutesSection? routes, DefinitionCatalog? catalog)
    {
        var f = section?.Observed;
        if (f is null)
        {
            return default;
        }
        var terrains = RequireCatalog(catalog).FloorTerrains();
        var r = new FlooringObservation { Terrains = new Dictionary<string, FloorTerrain>(terrains, StringComparer.Ordinal) };
        // The traffic tier reads the routes census's observed travel; an unknown routes census leaves the whole
        // flooring census unknown rather than silently dropping the tier. Traffic cells whose terrain the
        // flooring table does not name are unmeasured and left out.
        if (routes is not null)
        {
            if (!ColonyRoutes(routes).TryGetValue(out var t))
            {
                return default;
            }
            r = r with { TrafficSamples = t.TrafficSamples, Traffic = t.Traffic };
        }
        if (r.Traffic.Count > 0)
        {
            var kept = new List<TrafficCell>();
            foreach (var t in r.Traffic)
            {
                if (r.Terrains.ContainsKey(t.Terrain))
                {
                    kept.Add(t);
                }
            }
            r = r with { Traffic = kept };
        }
        var rooms = new List<FloorRoom>();
        foreach (var room in f.Rooms)
        {
            var output = new FloorRoom { Id = room.Room?.Id ?? "" };
            if (room.HasRole)
            {
                output = output with { Role = Known(WireParse<RoomRole>(room.Role)) };
            }
            var cells = new List<FloorCell>();
            foreach (var cell in room.Cells)
            {
                if (!cell.HasTerrain)
                {
                    return default;
                }
                if (!r.Terrains.ContainsKey(cell.Terrain))
                {
                    throw new ObservationContractException($"flooring cell stands on terrain {cell.Terrain}, which the catalog has no row for");
                }
                cells.Add(new FloorCell { Cell = CellOf(cell.Cell), Terrain = cell.Terrain, Pending = cell.Pending });
            }
            rooms.Add(output with { Cells = cells });
        }
        return Known(r with { Rooms = rooms });
    }

    /// <summary>
    /// Decodes the routes section; a travel row missing its reachability leaves the whole census unknown so
    /// MaintainRoutes keeps its previous latch instead of declaring a facility reachable by omission.
    /// </summary>
    private static Fact<RoutesObservation> ColonyRoutes(O.RoutesSection? section)
    {
        var f = section?.Observed;
        if (f is null)
        {
            return default;
        }
        var facilities = new List<RouteFacility>();
        var traffic = new List<TrafficCell>();
        var r = new RoutesObservation { Pawns = [.. f.PawnIds], TrafficSamples = f.TrafficSamples };
        if (f.HasTrafficSinceTick)
        {
            r = r with { TrafficSince = Known(new Tick((long)f.TrafficSinceTick)) };
        }
        foreach (var row in f.Facilities)
        {
            var output = new RouteFacility { Id = row.Facility?.Id ?? "", Kind = RouteKindName(row.Kind), Cell = CellOf(row.Cell) };
            if (row.Room is not null)
            {
                output = output with { Room = Known(row.Room.Id) };
            }
            var travels = new List<RouteTravel>();
            foreach (var t in row.Travel)
            {
                if (!t.HasReachable)
                {
                    return default;
                }
                var travel = new RouteTravel { Pawn = t.PawnId, Reachable = t.Reachable, Skipped = t.PathSkipped };
                if (t.HasPathCost)
                {
                    travel = travel with { Cost = Known(t.PathCost) };
                }
                if (t.HasPathCells)
                {
                    travel = travel with { Cells = Known(t.PathCells) };
                }
                travels.Add(travel);
            }
            var breaches = new List<RouteBreach>();
            foreach (var b in row.Breaches)
            {
                breaches.Add(new RouteBreach { Cell = CellOf(b.Cell), Edifice = b.Edifice, Pending = b.Pending, Distance = b.Distance });
            }
            facilities.Add(output with { Travel = travels, Breaches = breaches });
        }
        foreach (var t in f.Traffic)
        {
            if (!t.HasSamples || !t.HasTerrain || !t.HasHome || !TrafficLayerOf(t.Layer, out var layer))
            {
                return default;
            }
            traffic.Add(new TrafficCell { Cell = CellOf(t.Cell), Layer = layer, Samples = t.Samples, Terrain = t.Terrain, Home = t.Home, Pending = t.Pending });
        }
        return Known(r with { Facilities = facilities, Traffic = traffic });
    }

    private static bool TrafficLayerOf(O.TrafficLayer wire, out TrafficLayer layer)
    {
        switch (wire)
        {
            case O.TrafficLayer.Colonist:
                layer = TrafficLayer.Colonist;
                return true;
            case O.TrafficLayer.Crossing:
                layer = TrafficLayer.Crossing;
                return true;
            case O.TrafficLayer.Animal:
                layer = TrafficLayer.Animal;
                return true;
            case O.TrafficLayer.Visitor:
                layer = TrafficLayer.Visitor;
                return true;
            case O.TrafficLayer.Hostile:
                layer = TrafficLayer.Hostile;
                return true;
            default:
                layer = default;
                return false;
        }
    }

    /// <summary>
    /// Decodes the lighting section; any row missing a measured glow, roof or lit flag leaves the whole census
    /// unknown so MaintainLighting keeps its previous latch instead of reasoning from half a map.
    /// </summary>
    private static Fact<LightingObservation> ColonyLighting(O.LightingSection? section, Buildings buildings, DefinitionCatalog? catalog)
    {
        var l = section?.Observed;
        if (l is null)
        {
            return default;
        }
        var cells = new List<WorkLightCell>();
        foreach (var row in l.WorkCells)
        {
            if (!row.HasGlow || !row.HasRoofed || buildings.Entity(row.Bench) is not { } head)
            {
                return default;
            }
            var sensitive = false;
            foreach (var plant in row.PlantDefs)
            {
                sensitive = sensitive || RequireCatalog(catalog).PlantDiesToLight(plant);
            }
            cells.Add(new WorkLightCell
            {
                Bench = row.Bench?.Id ?? "",
                Definition = head.DefName,
                Cell = CellOf(row.Cell),
                Glow = row.Glow,
                Roofed = row.Roofed,
                Room = OptionalRef(row.Room),
                LightSensitive = sensitive,
            });
        }
        var lamps = new List<Lamp>();
        foreach (var row in l.Lamps)
        {
            if (!buildings.TryGetRow(row.Building, out var b) || !row.HasLit || b is null)
            {
                return default;
            }
            var reference = b.Building;
            var (radius, glows) = RequireCatalog(catalog).GlowRadius(reference?.DefName ?? "");
            if (!glows)
            {
                throw new DomainException($"lamp {reference?.Id}: def {reference?.DefName} has no glower comp");
            }
            var s = b.Service;
            lamps.Add(new Lamp
            {
                Id = reference?.Id ?? "",
                Definition = reference?.DefName ?? "",
                Cell = CellOf(reference?.Position),
                Radius = radius,
                Lit = row.Lit,
                Room = OptionalRef(row.Room),
                Powered = Optional(s.HasPowerOn, s.PowerOn),
                Connected = Optional(s.HasConnected, s.Connected),
                SwitchedOn = Optional(s.HasSwitchedOn, s.SwitchedOn),
                OutOfFuel = Optional(s.HasOutOfFuel, s.OutOfFuel),
                BrokenDown = Optional(s.HasBrokenDown, s.BrokenDown),
                FuelDefinitions = RequireCatalog(catalog).RefuelFuels(reference?.DefName ?? ""),
            });
        }
        return Known(new LightingObservation { WorkCells = cells, Lamps = lamps });
    }
}
