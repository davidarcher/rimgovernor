using static RimGovernor.Brain.Domain.Fact;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using Domain = RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.BuildingRuntime;

// Go consumption_read.go (Client.ReadConsumption) and resource_sources_read.go (Client.ReadResourceSources,
// decodeResourceStorage, DefinitionCatalog.ResourceSourceMethodOf).
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadConsumption</c>: native's saved realized-consumption ring, every completed hour after
    /// <paramref name="sinceHour"/> (negative: the whole 60-day window), sparse. The ring is game-scoped, not
    /// map-scoped, so the request carries no identity.
    /// </summary>
    internal async Task<Policy.ConsumptionPage> ReadConsumptionAsync(int sinceHour, CancellationToken cancellationToken)
    {
        var request = new PbObservations.ConsumptionRequest { SinceHour = sinceHour >= 0 ? sinceHour : null };
        var reply = await Facade.ReadConsumptionAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var snapshot = outcome.Observed!;
        var hours = new List<Policy.ConsumptionHour>();
        foreach (var hour in snapshot.Hours)
        {
            var rows = new List<Policy.ConsumptionRow>();
            foreach (var row in hour.Rows)
            {
                rows.Add(new Policy.ConsumptionRow { Resource = new Policy.Resource(row.Definition ?? ""), Reason = row.Reason ?? "", Count = row.Count.GetValueOrDefault() });
            }

            hours.Add(new Policy.ConsumptionHour { Hour = hour.Hour.GetValueOrDefault(), Rows = Domain.EquatableArray.From(rows) });
        }

        return new Policy.ConsumptionPage { CurrentHour = snapshot.CurrentHour.GetValueOrDefault(), FirstHour = snapshot.FirstHour.GetValueOrDefault(), Hours = Domain.EquatableArray.From(hours) };
    }

    /// <summary>
    /// Native's saved storage occupancy ring: each named storage group's hourly stack samples after
    /// <paramref name="sinceHour"/>, ascending. A group with no samples is absent. Game-scoped like consumption: the
    /// group load ids name the map.
    /// </summary>
    internal async Task<Policy.StoreOccupancy> ReadStorageOccupancyAsync(IReadOnlyCollection<string> groups, int sinceHour, CancellationToken cancellationToken)
    {
        var request = new PbObservations.StorageOccupancyRequest
        {
            Groups = Domain.EquatableArray.From(groups.Order(StringComparer.Ordinal).Select(id => new PbCommon.Ref { Id = id })),
            SinceHour = sinceHour >= 0 ? sinceHour : null,
        };
        var reply = await Facade.ReadStorageOccupancyAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        var snapshot = outcome.Observed!;
        var output = new Dictionary<string, Domain.EquatableArray<Policy.OccupancySample>>(StringComparer.Ordinal);
        foreach (var group in snapshot.Groups)
        {
            var samples = new List<Policy.OccupancySample>();
            foreach (var sample in group.Samples)
            {
                samples.Add(new Policy.OccupancySample { Hour = sample.Hour.GetValueOrDefault(), Stacks = sample.Stacks.GetValueOrDefault() });
            }

            output[group.Group?.Id ?? ""] = Domain.EquatableArray.From(samples);
        }

        return new Policy.StoreOccupancy { CurrentHour = snapshot.CurrentHour.GetValueOrDefault(), Groups = output };
    }

    /// <summary>
    /// Go <c>Client.ReadResourceSources</c>: a fresh read of one resource definition's reachable mine/harvest sources and
    /// the always-populated storage capacity payload, in one complete page. The rows come back nearest first (ties by
    /// thing id).
    /// </summary>
    internal async Task<(IReadOnlyList<Policy.ResourceSource> Rows, Policy.ResourceStorage Storage)> ReadResourceSourcesAsync(PbCommon.Identity identity, string resource, CancellationToken cancellationToken)
    {
        if (!WireValidation.IsValidId(resource))
        {
            throw BridgeContractException.Contract("invalid resource source definition");
        }

        var request = new PbObservations.ResourceSourcesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Resource = resource };
        var reply = await Facade.ListResourceSourcesAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        var snapshot = outcome.Observed!;
        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var storage = ResourceDecodeStorage(snapshot.Storage!, resource, catalog);
        var rows = new List<Policy.ResourceSource>(snapshot.Sources.Count);
        foreach (var row in snapshot.Sources)
        {
            var method = ResourceSourceMethodOf(catalog, row.DefName ?? "");
            var cell = default(Domain.Cell);
            if (method == Policy.ResourceSourceMethod.Mine && row.Cell is { } position)
            {
                cell = new Domain.Cell(position.X, position.Z);
            }

            rows.Add(new Policy.ResourceSource
            {
                ThingId = row.Source?.Id ?? "",
                Yield = (long)row.Yield.GetValueOrDefault(),
                Distance = row.Distance.GetValueOrDefault(),
                Method = method,
                Designated = row.Designated ?? false,
                Taken = row.Taken ?? false,
                Safety = row.Safety ?? "",
                Cell = cell,
                Reachable = row.Reachable is { } reachable ? Known(reachable) : Unknown<bool>(),
                Buried = row.Buried ?? false,
            });
        }

        IReadOnlyList<Policy.ResourceSource> ordered = [.. rows.OrderBy(r => r.Distance).ThenBy(r => r.ThingId, StringComparer.Ordinal)];
        return (ordered, storage);
    }

    /// <summary>
    /// Go <c>decodeResourceStorage</c>: the always-populated StorageCapacity payload. Haulers are counted, not carried in
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

        var cells = new List<Domain.Cell>(storage.Candidates.Count);
        foreach (var cell in storage.Candidates)
        {
            cells.Add(new Domain.Cell(cell.X, cell.Z));
        }

        return new Policy.ResourceStorage
        {
            Capacity = storage.Capacity,
            Stored = storage.Stored,
            StackLimit = stackLimit,
            Haulers = storage.Haulers.Count,
            Candidates = Domain.EquatableArray.From(cells),
        };
    }

    /// <summary>
    /// Go <c>DefinitionCatalog.ResourceSourceMethodOf</c>: how a source thing of <paramref name="def"/> is acquired. The
    /// policy knows only mine versus every other method (cut and harvest are both <see cref="Policy.ResourceSourceMethod.Other"/>).
    /// </summary>
    /// <remarks>
    /// APPROXIMATION: Go tested the def's thing class against the game's Mineable (ClassIsA). The Host references no
    /// game assembly, so the class is not reachable here; a def is taken as mined when its building yields a mineable
    /// thing (<c>building.mineableThing</c>); unverified against every def, a catalog method that tests the class replaces it. A def with no row at
    /// all is the same contract error Go raised.
    /// </remarks>
    private static Policy.ResourceSourceMethod ResourceSourceMethodOf(BridgeCatalog catalog, string def)
    {
        _ = catalog.StackLimit(def);
        return catalog.MineableYield(def).Length != 0 ? Policy.ResourceSourceMethod.Mine : Policy.ResourceSourceMethod.Other;
    }
}
