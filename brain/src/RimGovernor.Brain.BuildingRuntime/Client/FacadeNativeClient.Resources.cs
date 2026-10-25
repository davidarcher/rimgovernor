using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using Domain = RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

// ReadResourceSources, decodeResourceStorage and ResourceSourceMethodOf.
public sealed partial class FacadeNativeClient : IRoundsStorageSource
{
    /// <summary>
    /// Native's saved realized-consumption ring, every completed hour after
    /// <paramref name="sinceHour"/> (negative: the whole 60-day window), sparse. The ring is game-scoped, not
    /// map-scoped, so the request carries no identity.
    /// </summary>
    internal Task<PbObservations.ConsumptionSnapshot> ReadConsumptionAsync(int sinceHour, CancellationToken cancellationToken) =>
        Facade.ReadConsumptionAsync(new PbObservations.ConsumptionRequest { SinceHour = sinceHour }, cancellationToken);

    /// <summary>
    /// Native's saved storage occupancy ring: each named storage group's hourly stack samples after
    /// <paramref name="sinceHour"/>, ascending. A group with no samples is absent. Game-scoped like consumption: the
    /// group load ids name the map.
    /// </summary>
    public Task<PbObservations.StorageOccupancySnapshot> ReadStorageOccupancyAsync(IReadOnlyCollection<string> groups, int sinceHour, CancellationToken cancellationToken) =>
        Facade.ReadStorageOccupancyAsync(
            new PbObservations.StorageOccupancyRequest { Groups = Domain.EquatableArray.From(groups.Order(StringComparer.Ordinal)), SinceHour = sinceHour },
            cancellationToken);

    /// <summary>
    /// A fresh read of one resource definition's reachable mine/harvest sources and
    /// the always-populated storage capacity payload, in one complete page. The rows come back nearest first (ties by
    /// thing id).
    /// </summary>
    internal async Task<(IReadOnlyList<Policy.ResourceSource> Rows, Policy.ResourceStorage Storage)> ReadResourceSourcesAsync(PbCommon.Identity identity, string resource, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ResourceSourcesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Resource = resource };
        var snapshot = await Facade.ListResourceSourcesAsync(request, cancellationToken).ConfigureAwait(false);
        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var storage = ResourceDecodeStorage(snapshot.Storage, resource, catalog);
        var rows = new List<Policy.ResourceSource>(snapshot.Sources.Count);
        foreach (var row in snapshot.Sources)
        {
            var method = ResourceSourceMethodOf(catalog, row.DefName);
            rows.Add(new Policy.ResourceSource
            {
                ThingId = row.Id,
                Yield = (long)row.Yield,
                Distance = row.Distance,
                Method = method,
                Designated = row.Designated,
                Taken = row.Taken,
                Safety = row.Safety,
                Cell = method == Policy.ResourceSourceMethod.Mine ? row.Cell : default,
                Buried = row.Buried,
            });
        }

        IReadOnlyList<Policy.ResourceSource> ordered = [.. rows.OrderBy(r => r.Distance).ThenBy(r => r.ThingId, StringComparer.Ordinal)];
        return (ordered, storage);
    }

    /// <summary>
    /// The always-populated StorageCapacity payload. Haulers are counted, not carried in
    /// full; candidate cells are native's own hauler-reachable, roofed, unreserved scan and are trusted as exact placement
    /// sites.
    /// </summary>
    private static Policy.ResourceStorage ResourceDecodeStorage(PbObservations.StorageCapacity storage, string resource, BridgeCatalog catalog)
    {
        var stackLimit = catalog.StackLimit(resource);
        if (stackLimit <= 0)
        {
            throw BridgeContractException.Contract("invalid resource storage stack limit");
        }

        return new Policy.ResourceStorage
        {
            Capacity = storage.Capacity,
            Stored = storage.Stored,
            StackLimit = stackLimit,
            Haulers = storage.Haulers.Count,
            Candidates = storage.Candidates,
        };
    }

    /// <summary>
    /// How a source thing of <paramref name="def"/> is acquired. The
    /// policy knows only mine versus every other method (cut and harvest are both <see cref="Policy.ResourceSourceMethod.Other"/>).
    /// </summary>
    /// <remarks>
    /// APPROXIMATION: the def's thing class would be tested against the game's Mineable (ClassIsA). The Host references no
    /// game assembly, so the class is not reachable here; a def is taken as mined when its building yields a mineable
    /// thing (<c>building.mineableThing</c>); unverified against every def, a catalog method that tests the class replaces it. A def with no row at
    /// all is a contract error.
    /// </remarks>
    private static Policy.ResourceSourceMethod ResourceSourceMethodOf(BridgeCatalog catalog, string def)
    {
        _ = catalog.StackLimit(def);
        return catalog.MineableYield(def).Length != 0 ? Policy.ResourceSourceMethod.Mine : Policy.ResourceSourceMethod.Other;
    }
}
