namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The round's planned-site facts (#3275): the plan's owed operations as sites, one per op x ring stuff, and the
    /// unplaced shelf candidates as one-piece sites, each joined to the native planned-site read. The read is asked
    /// PlannedSitesPerRead sites at a time through the construction activity read; without that read every site stays
    /// unknown. Unknown while the plan or the construction census is.
    /// </summary>
    internal async Task<Fact<PlannedSiteFacts>> PlannedSitesAsync(GenerationSnapshot snapshot, ColonyProjection colony, FlooringPolicy flooring,
        IReadOnlyList<ShelfCandidate> shelves, CancellationToken ct)
    {
        if (PlannedOwedOf(colony, flooring) is not { } owed)
        {
            return Unknown<PlannedSiteFacts>();
        }
        var sites = new List<PlannedBuildSite>(PolicyApi.PlannedSitesOf(owed.Rooms, room => PlannedRingStuffsOf(colony, room),
            (def, stuff) => PlannedRingPrice(colony, def, stuff), def => PlannedPiecePrice(colony, def), owed.FloorPrice,
            ClimateDefs().VentDef, owed.Placed));
        if (shelves.Count > 0)
        {
            var stuff = colony.BuildStuff(PolicyApi.ShelfDefinition);
            sites.AddRange(PolicyApi.ShelfPlannedSites(shelves, PlannedRingPrice(colony, PolicyApi.ShelfDefinition, stuff)));
        }
        var (batches, findings) = PolicyApi.PlannedSiteBatches(sites);
        var reads = new List<(EquatableArray<O.PlannedConstructionSite>, O.ConstructionActivityRead?)>(batches.Count);
        foreach (var batch in batches)
        {
            O.ConstructionActivityRead? read = null;
            if (Native is IActivityTaskSource source)
            {
                read = await source.ReadConstructionActivityAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), []) { Planned = batch }, ct)
                    .ConfigureAwait(false);
            }
            reads.Add((batch, read));
        }
        return Known(PolicyApi.JoinPlannedSites(sites, findings, reads));
    }

    /// <summary>
    /// The stuffs a planned room's ring is read in (PlannedRingStuffs): the wall def's ordinary stuffs the door can be made
    /// from too, priced by the wall's cost list, stocked when the colony holds any of the stuff.
    /// </summary>
    private static IReadOnlyList<string> PlannedRingStuffsOf(ColonyProjection colony, PlannedRoom room)
    {
        var (wallDef, doorDef) = room.RingDefs();
        if (AnimalContainmentDefinition(colony.Definitions, wallDef) is not { } wall)
        {
            return [];
        }
        var door = AnimalContainmentDefinition(colony.Definitions, doorDef);
        var common = wall.StuffOptions.Any(o => o.Common);
        colony.TryStock(out var stock);
        var options = wall.StuffOptions
            .Where(o => (o.Common || !common) && (door is null || door.MakeableFrom(o.Stuff)))
            .Select(o => new PlannedRingStuff(o.Stuff, o.Value, stock is not null && stock.TryGetValue(new Resource(o.Stuff), out var held) && held > 0))
            .ToList();
        if (wall.StuffOptions.Count > 0 && options.Count == 0)
        {
            return [];
        }
        return PolicyApi.PlannedRingStuffs(options);
    }

    /// <summary>The stuff def is built from for a wanted stuff ("" for a def made from no stuff) and its cost list; null where the def is unavailable or cannot be made from it.</summary>
    private static (string Stuff, IReadOnlyList<Amount> Costs)? PlannedRingPrice(ColonyProjection colony, string name, string stuff)
    {
        if (AnimalContainmentDefinition(colony.Definitions, name) is not { } def)
        {
            return null;
        }
        if (def.StuffOptions.Count == 0)
        {
            return def.Costs.TryGetValue(out var costs) ? ("", costs) : null;
        }
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is { } option ? (stuff, option.Costs) : null;
    }
}
