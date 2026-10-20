using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>
    /// The round's planned-site facts (#3275): the plan's owed operations as sites, one per op x ring stuff, and the
    /// unplaced shelf candidates as one-piece sites, each joined to the native planned-site read. Every site is asked in
    /// one construction activity read, then every route between each room's answered sites and between them and the
    /// nearest need targets in a second (PlannedRouteProbes, #3313, #3676); each read covers every free colonist and caps nothing (#3341). Without a read its sites or routes stay
    /// unknown. Unknown while the plan or the construction census is.
    /// </summary>
    internal async Task<Fact<PlannedSiteFacts>> PlannedSitesAsync(GenerationSnapshot snapshot, ColonyProjection colony, PlannedOwed? owed,
        IReadOnlyList<ShelfCandidate> shelves, CancellationToken ct)
    {
        if (PlannedRoomSitesOf(colony, owed) is not { } roomSites)
        {
            return Unknown<PlannedSiteFacts>();
        }
        var sites = new List<PlannedBuildSite>(roomSites);
        if (shelves.Count > 0)
        {
            var stuff = colony.BuildStuff(StorageRules.ShelfDefinition);
            sites.AddRange(PlannedRules.ShelfPlannedSites(shelves, PlannedRingPrice(colony, StorageRules.ShelfDefinition, stuff)));
        }
        var asked = PlannedRules.PlannedSiteRequest(sites);
        var read = asked.Count == 0 ? null
            : await ReadPlannedAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), []) { Planned = asked }, ct).ConfigureAwait(false);
        var joined = PlannedRules.JoinPlannedSites(sites, read);
        // Routes between the answered sites (#3313): their sources and clearing are known only once the sites are;
        // and between them and the need targets a pawn eats or sleeps at in between (#3676).
        var needTargets = (colony.Facts.ForecastObserved.Needs?.Pawns ?? []).SelectMany(p => p.Targets)
            .Where(t => t.Location is not null).Select(t => t.Location!.Value).ToHashSet();
        var probes = PlannedRules.PlannedRouteProbes(joined.Sites, needTargets);
        var routes = probes.Count == 0 ? null
            : await ReadPlannedAsync(new O.ConstructionActivityRequest(Boundary.Identity(snapshot), []) { Routes = probes }, ct).ConfigureAwait(false);
        return Known(PlannedRules.JoinPlannedRoutes(joined, probes, routes) with
        {
            Roles = EquatableArray.From(owed!.Sited.Select(r => (Room: r.Room.Id, r.Room.Role)).Distinct().OrderBy(r => r.Room)),
        });
    }

    /// <summary>One construction activity read of planned sites or routes; null without the read.</summary>
    private async Task<O.ConstructionActivityRead?> ReadPlannedAsync(O.ConstructionActivityRequest request, CancellationToken ct)
    {
        return Native is IActivityTaskSource source ? await source.ReadConstructionActivityAsync(request, ct).ConfigureAwait(false) : null;
    }

    /// <summary>The plan's owed operations as planned sites (PlannedSitesOf), unread; null while the plan or the construction census is unknown.</summary>
    internal static IReadOnlyList<PlannedBuildSite>? PlannedRoomSitesOf(ColonyProjection colony, FlooringPolicy flooring) =>
        PlannedRoomSitesOf(colony, PlannedOwedOf(colony, flooring));

    /// <summary>PlannedRoomSitesOf over an owed diff already taken (PlannedOwedOf), so one review's readers share it; null without one.</summary>
    internal static IReadOnlyList<PlannedBuildSite>? PlannedRoomSitesOf(ColonyProjection colony, PlannedOwed? owed) =>
        owed is null ? null
            : PlannedRules.PlannedSitesOf(owed.Sited, room => PlannedRingStuffsOf(colony, room),
                (def, stuff) => PlannedRingPrice(colony, def, stuff), def => PlannedPiecePrice(colony, def), owed.FloorPrice,
                ClimateRules.ClimateDefs().VentDef, owed.Placed, owed.WallStuff);

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
        if (RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, site.Definition) is not { } def)
        {
            return Unknown<double>();
        }
        if (site.Stuff.Length == 0)
        {
            return def.StuffOptions.Count == 0 ? def.WorkToBuild : Unknown<double>();
        }
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == site.Stuff) is { } option
            && option.Stats.TryGetValue(Bridge.Contracts.StatDefNames.StatWorkToBuild, out var work) ? Known(work) : Unknown<double>();
    }

    /// <summary>
    /// The stuffs a planned room's ring is read in (PlannedRingStuffs): the wall def's ordinary stuffs the door can be made
    /// from too, priced by the wall's cost list, stocked when the colony holds any of the stuff.
    /// </summary>
    private static IReadOnlyList<string> PlannedRingStuffsOf(ColonyProjection colony, PlannedRoom room)
    {
        var (wallDef, doorDef) = room.RingDefs();
        return PlannedRingStuffsOf(colony, wallDef, doorDef);
    }

    /// <summary>The stuffs a ring of <paramref name="wallDef"/> and <paramref name="doorDef"/> is read in (PlannedRingStuffs); the first is its base sites' stuff.</summary>
    internal static IReadOnlyList<string> PlannedRingStuffsOf(ColonyProjection colony, string wallDef, string doorDef)
    {
        if (RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, wallDef) is not { } wall)
        {
            return [];
        }
        var door = RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, doorDef);
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
        return PlannedRules.PlannedRingStuffs(options);
    }

    /// <summary>The stuff def is built from for a wanted stuff ("" for a def made from no stuff) and its cost list; null where the def is unavailable or cannot be made from it.</summary>
    private static (string Stuff, IReadOnlyList<Amount> Costs)? PlannedRingPrice(ColonyProjection colony, string name, string stuff)
    {
        if (RoundsAnimalContainment.AnimalContainmentDefinition(colony.Definitions, name) is not { } def)
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
