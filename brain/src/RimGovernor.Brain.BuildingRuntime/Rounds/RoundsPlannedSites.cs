namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The round's planned-site facts (#3275): the plan's owed operations as sites, one per op x ring stuff, and the
    /// unplaced shelf candidates as one-piece sites, each joined to the native planned-site read. The read is asked
    /// PlannedSitesPerRead sites at a time through the construction activity read, then the routes between each room's
    /// answered sites (PlannedRouteBatches, #3313); without that read every site and route stays unknown. Unknown while
    /// the plan or the construction census is.
    /// </summary>
    internal async Task<Fact<PlannedSiteFacts>> PlannedSitesAsync(GenerationSnapshot snapshot, ColonyProjection colony, FlooringPolicy flooring,
        IReadOnlyList<ShelfCandidate> shelves, CancellationToken ct)
    {
        if (PlannedRoomSitesOf(colony, flooring) is not { } roomSites)
        {
            return Unknown<PlannedSiteFacts>();
        }
        var sites = new List<PlannedBuildSite>(roomSites);
        if (shelves.Count > 0)
        {
            var stuff = colony.BuildStuff(PolicyApi.ShelfDefinition);
            sites.AddRange(PolicyApi.ShelfPlannedSites(shelves, PlannedRingPrice(colony, PolicyApi.ShelfDefinition, stuff)));
        }
        var (batches, findings) = PolicyApi.PlannedSiteBatches(sites);
        var reads = new List<(EquatableArray<O.PlannedConstructionSite>, O.ConstructionActivityRead?)>(batches.Count);
        foreach (var batch in batches)
        {
            reads.Add((batch, await ReadPlannedAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), []) { Planned = batch }, ct)
                .ConfigureAwait(false)));
        }
        var joined = PolicyApi.JoinPlannedSites(sites, findings, reads);
        // Routes between the answered sites (#3313): their sources and clearing are known only once the sites are.
        var (probes, routeFindings) = PolicyApi.PlannedRouteBatches(joined.Sites);
        var routes = new List<(EquatableArray<O.PlannedRouteProbe>, O.ConstructionActivityRead?)>(probes.Count);
        foreach (var batch in probes)
        {
            routes.Add((batch, await ReadPlannedAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), []) { Routes = batch }, ct)
                .ConfigureAwait(false)));
        }
        return Known(PolicyApi.JoinPlannedRoutes(joined, routeFindings, routes));
    }

    /// <summary>One construction activity read of planned sites or routes; null without the read.</summary>
    private async Task<O.ConstructionActivityRead?> ReadPlannedAsync(O.ConstructionActivityRequest request, CancellationToken ct)
    {
        return Native is IActivityTaskSource source ? await source.ReadConstructionActivityAsync(request, ct).ConfigureAwait(false) : null;
    }

    /// <summary>The plan's owed operations as planned sites (PlannedSitesOf), unread; null while the plan or the construction census is unknown.</summary>
    internal static IReadOnlyList<PlannedBuildSite>? PlannedRoomSitesOf(ColonyProjection colony, FlooringPolicy flooring) =>
        PlannedOwedOf(colony, flooring) is not { } owed ? null
            : PolicyApi.PlannedSitesOf(owed.Sited, room => PlannedRingStuffsOf(colony, room),
                (def, stuff) => PlannedRingPrice(colony, def, stuff), def => PlannedPiecePrice(colony, def), owed.FloorPrice,
                ClimateDefs().VentDef, owed.Placed);

    /// <summary>
    /// A planned site's catalog work per piece (WorkToBuild): its def's for its stuff, a floor's own; unknown where the
    /// catalog does not show it. A roof has no catalog work (PlannedRoomWork reports it apart).
    /// </summary>
    internal static Fact<double> PlannedSiteWork(ColonyProjection colony, FlooringFacts floors, PlannedBuildSite site)
    {
        if (site.Op == OpKind.FloorIn)
        {
            return floors.Definitions.TryGetValue(site.Definition, out var floor) ? floor.WorkToBuild : Unknown<double>();
        }
        if (AnimalContainmentDefinition(colony.Definitions, site.Definition) is not { } def)
        {
            return Unknown<double>();
        }
        if (site.Stuff.Length == 0)
        {
            return def.StuffOptions.Count == 0 ? def.WorkToBuild : Unknown<double>();
        }
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == site.Stuff) is { } option
            && option.Stats.TryGetValue(BridgeConstants.StatWorkToBuild, out var work) ? Known(work) : Unknown<double>();
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
