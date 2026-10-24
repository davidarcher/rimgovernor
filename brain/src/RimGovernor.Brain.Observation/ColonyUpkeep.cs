namespace RimGovernor.Brain.Observation;

public static class ColonyUpkeepReads
{
    /// <summary>
    /// Decodes the upkeep censuses. A census whose frame lacks a field is unknown; one that needs a def the
    /// catalog cannot answer for throws naming the census and the def, never an unknown.
    /// </summary>
    internal static UpkeepObservation ColonyUpkeep(O.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog)
    {
        var buildings = tables.Buildings;
        var r = new UpkeepObservation();
        var u = v.Upkeep?.Outcome;
        if (u is null)
        {
            return r;
        }
        r = r with { Structures = Known(RepairStructures(u, ColonyDecodeReads.RequireCatalog(catalog))) };
        r = r with { Lighting = Known(ColonyLighting(u.Lighting, buildings, catalog)) };
        if (!ColonyDecodeReads.HasIssue(u.Issues, "routes"))
        {
            r = r with { Routes = ColonyRoutes(u.Routes) };
        }
        if (!ColonyDecodeReads.HasIssue(u.Issues, "flooring"))
        {
            // The flooring census joins the routes census's traffic evidence, so a routes issue leaves flooring
            // unknown too rather than judging the traffic tier from nothing.
            var routes = ColonyDecodeReads.HasIssue(u.Issues, "routes") ? new O.RoutesSection() : u.Routes;
            try
            {
                r = r with { Flooring = ColonyFlooring(u.Flooring, routes, catalog) };
            }
            catch (DomainException e)
            {
                throw new DomainException($"flooring census: {e.Message}");
            }
        }
        return r;
    }

    /// <summary>
    /// The structure census with each row's catalog repair priority (a medical bed is the bed census's) and
    /// flammability; a def the catalog cannot answer for throws naming the structure.
    /// </summary>
    private static EquatableArray<RepairStructure> RepairStructures(O.UpkeepFacts u, DefinitionCatalog catalog)
    {
        var medical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bed in u.Beds)
        {
            if (bed.Medical)
            {
                medical.Add(bed.Bed.Id ?? "");
            }
        }
        var rows = new List<RepairStructure>(u.Structures.Count);
        foreach (var s in u.Structures)
        {
            try
            {
                // The flammability of the building as made: its def with its stuff.
                rows.Add(new RepairStructure(s, catalog.RepairPriority(s.Definition, medical.Contains(s.Id)), catalog.PlannerStatValue(s.Definition, s.Stuff ?? "", CatalogStats.Flammability)));
            }
            catch (DomainException e)
            {
                throw new DomainException($"upkeep structure {s.Id}: {e.Message}");
            }
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Decodes the flooring section; a cell missing its terrain leaves the whole census unknown so the
    /// rooms/flooring Need reads unknown, and a terrain the catalog has no stat or def row for throws.
    /// </summary>
    private static Fact<FlooringObservation> ColonyFlooring(O.FlooringSection? section, O.RoutesSection? routes, DefinitionCatalog? catalog)
    {
        var f = section?.Outcome;
        if (f is null)
        {
            return default;
        }
        var terrains = ColonyDecodeReads.RequireCatalog(catalog).FloorTerrains();
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
            r = r with { Traffic = [.. kept] };
        }
        var rooms = new List<FloorRoom>();
        foreach (var room in f.Rooms)
        {
            var output = new FloorRoom { Id = room.Room?.Id ?? "" };
            if (room.Role is not null)
            {
                output = output with { Role = Known(ColonyDecodeReads.WireParseOpen(room.Role, RoomRole.Unrecognized)) };
            }
            var cells = new List<FloorCell>();
            foreach (var cell in room.Cells)
            {
                if (cell.Terrain is null)
                {
                    return default;
                }
                if (!r.Terrains.ContainsKey(cell.Terrain))
                {
                    throw new ObservationContractException($"flooring cell stands on terrain {cell.Terrain}, which the catalog has no row for");
                }
                cells.Add(new FloorCell { Cell = ColonyDecodeReads.CellOf(cell.Cell), Terrain = cell.Terrain, Pending = cell.Pending ?? "" });
            }
            rooms.Add(output with { Cells = [.. cells] });
        }
        return Known(r with { Rooms = rooms });
    }

    /// <summary>
    /// Decodes the routes section; a travel row missing its reachability leaves the whole census unknown so
    /// the layout/routes Need reads unknown instead of declaring a facility reachable by omission.
    /// </summary>
    private static Fact<RoutesObservation> ColonyRoutes(O.RoutesSection? section)
    {
        var f = section?.Outcome;
        if (f is null)
        {
            return default;
        }
        var facilities = new List<RouteFacility>();
        var traffic = new List<TrafficCell>();
        var r = new RoutesObservation { Pawns = [.. f.PawnIds], TrafficSamples = f.TrafficSamples.GetValueOrDefault() };
        foreach (var row in f.Facilities)
        {
            var output = new RouteFacility { Id = row.Facility?.Id ?? "", Kind = WireEnums.RouteKindName(row.Kind.GetValueOrDefault()) };
            if (row.Room is not null)
            {
                output = output with { Room = Known(row.Room.Id ?? "") };
            }
            var travels = new List<RouteTravel>();
            foreach (var t in row.Travel)
            {
                if (t.Reachable is null)
                {
                    return default;
                }
                var travel = new RouteTravel { Pawn = t.PawnId ?? "", Reachable = t.Reachable.GetValueOrDefault(), Skipped = t.PathSkipped.GetValueOrDefault() };
                if (t.PathCost is not null)
                {
                    travel = travel with { Cost = Known(t.PathCost.GetValueOrDefault()) };
                }
                if (t.PathCells is not null)
                {
                    travel = travel with { Cells = Known(t.PathCells.GetValueOrDefault()) };
                }
                travels.Add(travel);
            }
            var breaches = new List<RouteBreach>();
            foreach (var b in row.Breaches)
            {
                breaches.Add(new RouteBreach { Cell = ColonyDecodeReads.CellOf(b.Cell), Edifice = b.Edifice ?? "", Pending = b.Pending ?? "", Distance = b.Distance.GetValueOrDefault() });
            }
            facilities.Add(output with { Travel = [.. travels], Breaches = [.. breaches] });
        }
        foreach (var t in f.Traffic)
        {
            if (t.Samples is null || t.Terrain is null || t.Home is null || !TrafficLayerOf(t.Layer, out var layer))
            {
                return default;
            }
            traffic.Add(new TrafficCell { Cell = ColonyDecodeReads.CellOf(t.Cell), Layer = layer, Samples = t.Samples.GetValueOrDefault(), Terrain = t.Terrain ?? "", Home = t.Home.GetValueOrDefault(), Pending = t.Pending ?? "" });
        }
        return Known(r with { Facilities = [.. facilities], Traffic = [.. traffic] });
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
    /// Joins the native lighting census to the building rows and catalog rows the lighting rules plan from: each lamp's
    /// def, cell, glow radius and service state, and the census plants that die to light. A lamp the building table
    /// does not hold, or a def the catalog cannot answer for, throws.
    /// </summary>
    private static LightingObservation ColonyLighting(O.LightingFacts census, Buildings buildings, DefinitionCatalog? catalog)
    {
        var required = ColonyDecodeReads.RequireCatalog(catalog);
        var lamps = new List<Lamp>();
        foreach (var row in census.Lamps)
        {
            if (!buildings.TryGetValue(row.Building, out var b))
            {
                throw new ObservationContractException($"lighting census lamp {row.Building} has no building row");
            }
            var reference = b.Building;
            var radius = required.GlowRadius(reference.DefName)
                ?? throw new DomainException($"lamp {reference.Id}: def {reference.DefName} has no glower comp");
            var s = b.Service;
            lamps.Add(new Lamp
            {
                Id = reference.Id,
                Definition = reference.DefName,
                Cell = ColonyDecodeReads.MapCellOf(reference),
                Radius = radius,
                Lit = row.Lit,
                SwitchedOn = s.SwitchedOn,
                BrokenDown = s.BrokenDown,
                Fuel = s.OutOfFuel switch { null => LampFuel.Unfueled, true => LampFuel.Empty, false => LampFuel.Fueled },
            });
        }
        var plants = census.WorkCells.SelectMany(c => c.PlantDefs).Distinct(StringComparer.Ordinal).Where(required.PlantDiesToLight);
        return new LightingObservation { Census = census, DiesToLight = EquatableArray.From(plants), Lamps = lamps };
    }
}
