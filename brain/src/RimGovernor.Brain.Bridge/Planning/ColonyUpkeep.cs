using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using CatalogStats = RimGovernor.Brain.Bridge.CatalogStats;
using Cell = RimGovernor.Brain.Domain.Cell;
namespace RimGovernor.Brain.Bridge;

public static class ColonyUpkeepReads
{
    /// <summary>
    /// Decodes the upkeep censuses, each read whole. One that needs a def the catalog cannot answer for throws naming
    /// the census and the def, never an unknown.
    /// </summary>
    internal static UpkeepObservation ColonyUpkeep(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot v, Tables tables, DefinitionCatalog? catalog)
    {
        var u = v.Upkeep;
        var r = new UpkeepObservation
        {
            Structures = RepairStructures(u, tables.Buildings, ColonyPlanningReads.RequireCatalog(catalog)),
            Lighting = ColonyLighting(u.Lighting, tables.Buildings, catalog),
            Routes = u.Routes,
        };
        try
        {
            // The flooring census joins the routes census's traffic evidence.
            return r with { Flooring = ColonyFlooring(u.Flooring, u.Routes, catalog) };
        }
        catch (DomainException e)
        {
            throw new DomainException($"flooring census: {e.Message}");
        }
    }

    /// <summary>
    /// The structure census with each row's catalog repair priority (a medical bed is the bed census's) and
    /// flammability; a def the catalog cannot answer for throws naming the structure.
    /// </summary>
    private static EquatableArray<RepairStructure> RepairStructures(RimGovernor.Brain.GameFacts.Observations.UpkeepFacts u, Buildings buildings, DefinitionCatalog catalog)
    {
        var medical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bed in u.Beds)
        {
            if (bed.Medical)
            {
                medical.Add(bed.Id);
            }
        }
        var rows = new List<RepairStructure>(u.Structures.Count);
        foreach (var s in u.Structures)
        {
            if (!buildings.TryGetValue(s.Id, out var building) || building.Building.Place is not RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap
                || building.HitPoints is not RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Tracked)
            {
                throw new ObservationContractException($"repair census building {s.Id} lacks a placed damageable building row");
            }
            try
            {
                // The flammability of the building as made: its def with its stuff.
                var definition = building.Building.DefName;
                rows.Add(new RepairStructure(s, building, catalog.RepairPriority(definition, medical.Contains(s.Id)), catalog.PlannerStatValue(definition, building.Stuff ?? "", CatalogStats.Flammability)));
            }
            catch (DomainException e)
            {
                throw new DomainException($"upkeep structure {s.Id}: {e.Message}");
            }
        }
        return EquatableArray.From(rows);
    }

    /// <summary>
    /// Decodes the flooring census; a terrain the catalog has no stat or def row for throws.
    /// </summary>
    private static FlooringObservation ColonyFlooring(RimGovernor.Brain.GameFacts.Observations.FlooringFacts f, RimGovernor.Brain.GameFacts.Observations.RoutesFacts routes, DefinitionCatalog? catalog)
    {
        var terrains = ColonyPlanningReads.RequireCatalog(catalog).FloorTerrains();
        var r = new FlooringObservation { Terrains = new Dictionary<string, FloorTerrain>(terrains, StringComparer.Ordinal) };
        // The traffic tier reads the routes census's observed travel. Traffic cells whose terrain the flooring table does
        // not name are unmeasured and left out.
        r = r with { TrafficSamples = routes.TrafficSamples, Traffic = [.. routes.Traffic.Where(t => r.Terrains.ContainsKey(t.Terrain))] };
        var rooms = new List<FloorRoom>();
        foreach (var room in f.Rooms)
        {
            foreach (var cell in room.Cells)
            {
                if (!r.Terrains.ContainsKey(cell.Terrain))
                {
                    throw new ObservationContractException($"flooring cell stands on terrain {cell.Terrain}, which the catalog has no row for");
                }
            }
            rooms.Add(new FloorRoom { Observed = room });
        }
        return r with { Rooms = rooms };
    }

/// <summary>
    /// Joins the native lighting census to the building rows and catalog rows the lighting rules plan from: each lamp's
    /// def, cell, glow radius and service state, and the census plants that die to light. A lamp the building table
    /// does not hold, or a def the catalog cannot answer for, throws.
    /// </summary>
    private static LightingObservation ColonyLighting(RimGovernor.Brain.GameFacts.Observations.LightingFacts census, Buildings buildings, DefinitionCatalog? catalog)
    {
        var required = ColonyPlanningReads.RequireCatalog(catalog);
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
            if (reference.Place is not RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap)
            {
                throw new ObservationContractException("required map census row is unplaced");
            }
            lamps.Add(new Lamp
            {
                Observed = row,
                Building = b,
                Radius = radius,
            });
        }
        var plants = census.WorkCells.SelectMany(c => c.PlantDefs).Distinct(StringComparer.Ordinal).Where(required.PlantDiesToLight);
        return new LightingObservation { Census = census, DiesToLight = EquatableArray.From(plants), Lamps = lamps };
    }
}
