using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using static RimGovernor.Brain.Domain.Fact;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using Domain = RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Host;

// Go consumption_read.go (Client.ReadConsumption) and resource_sources_read.go (Client.ReadResourceSources,
// decodeResourceStorage, DefinitionCatalog.ResourceSourceMethodOf).
internal sealed partial class FacadeNativeClient
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
        var outcome = reply?.Outcome;
        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        var snapshot = outcome?.Observed ?? throw Contract("missing consumption outcome");
        if (snapshot.CurrentHour is null || snapshot.FirstHour is null || snapshot.CurrentHour < 0 || snapshot.FirstHour < 0)
        {
            throw Contract("invalid consumption hours");
        }

        var currentHour = snapshot.CurrentHour.Value;
        var hours = new List<Policy.ConsumptionHour>();
        var previous = -1;
        foreach (var hour in snapshot.Hours)
        {
            if (hour is null || hour.Hour is null || hour.Hour <= previous || hour.Hour >= currentHour || hour.Hour <= sinceHour)
            {
                throw Contract("invalid consumption hour");
            }

            previous = hour.Hour.Value;
            var rows = new List<Policy.ConsumptionRow>();
            foreach (var row in hour.Rows)
            {
                if (row is null || !IsValidId(row.Definition ?? "") || row.Count is null || !Policy.PolicyFunctions.KnownConsumptionReason(row.Reason ?? ""))
                {
                    throw Contract("invalid consumption row");
                }

                rows.Add(new Policy.ConsumptionRow { Resource = new Policy.Resource(row.Definition!), Reason = row.Reason ?? "", Count = row.Count.Value });
            }

            hours.Add(new Policy.ConsumptionHour { Hour = previous, Rows = Domain.EquatableArray.From(rows) });
        }

        return new Policy.ConsumptionPage { CurrentHour = currentHour, FirstHour = snapshot.FirstHour.Value, Hours = Domain.EquatableArray.From(hours) };
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
        var outcome = reply?.Outcome;
        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        var snapshot = outcome?.Observed ?? throw Contract("missing storage occupancy outcome");
        if (snapshot.CurrentHour is null || snapshot.CurrentHour < 0)
        {
            throw Contract("invalid storage occupancy hour");
        }

        var output = new Dictionary<string, Domain.EquatableArray<Policy.OccupancySample>>(StringComparer.Ordinal);
        foreach (var group in snapshot.Groups)
        {
            var id = group?.Group?.Id ?? "";
            if (!groups.Contains(id) || output.ContainsKey(id))
            {
                throw Contract("invalid storage occupancy group");
            }

            var samples = new List<Policy.OccupancySample>();
            var previous = sinceHour;
            foreach (var sample in group!.Samples)
            {
                if (sample is null || sample.Hour is null || sample.Stacks is null || sample.Hour <= previous || sample.Hour > snapshot.CurrentHour || sample.Stacks < 0)
                {
                    throw Contract("invalid storage occupancy sample");
                }

                previous = sample.Hour.Value;
                samples.Add(new Policy.OccupancySample { Hour = previous, Stacks = sample.Stacks.Value });
            }

            output[id] = Domain.EquatableArray.From(samples);
        }

        return new Policy.StoreOccupancy { CurrentHour = snapshot.CurrentHour.Value, Groups = output };
    }

    /// <summary>
    /// Go <c>Client.ReadResourceSources</c>: a fresh read of one resource definition's reachable mine/harvest sources and
    /// the always-populated storage capacity payload, in one complete page. The rows come back nearest first (ties by
    /// thing id).
    /// </summary>
    internal async Task<(IReadOnlyList<Policy.ResourceSource> Rows, Policy.ResourceStorage Storage)> ReadResourceSourcesAsync(PbCommon.Identity identity, string resource, CancellationToken cancellationToken)
    {
        if (!IsValidId(resource))
        {
            throw Contract("invalid resource source definition");
        }

        var request = new PbObservations.ResourceSourcesRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Resource = resource };
        var reply = await Facade.ListResourceSourcesAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        var snapshot = outcome?.Observed ?? throw Contract("missing resource sources outcome");
        if (!G3ContextValid(snapshot.Context) || !SameIdentity(snapshot.Context!.Identity, identity))
        {
            throw Contract("invalid resource sources context");
        }

        if ((snapshot.Resource ?? "") != resource)
        {
            throw Contract("resource sources definition mismatch");
        }

        var catalog = await DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);
        var storage = ResourceDecodeStorage(snapshot.Storage, resource, catalog);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Policy.ResourceSource>(snapshot.Sources.Count);
        foreach (var row in snapshot.Sources)
        {
            if (row is null || row.Source is null || !IsValidId(row.Source.Id ?? "") || seen.Contains(row.Source.Id!))
            {
                throw Contract("invalid resource source identity");
            }

            var method = ResourceSourceMethodOf(catalog, row.DefName ?? "");
            if (row.Yield is null || row.Yield < 0)
            {
                throw Contract("invalid resource source yield");
            }

            var units = (long)row.Yield.Value;
            if ((double)units != row.Yield.Value)
            {
                throw Contract("fractional resource source yield");
            }

            if (row.Distance is null || row.Distance < 0)
            {
                throw Contract("invalid resource source distance");
            }

            if (row.Designated is null)
            {
                throw Contract("resource source designation unavailable");
            }

            var cell = default(Domain.Cell);
            var token = "";
            if (method == Policy.ResourceSourceMethod.Mine)
            {
                if (!IsValidId(row.Safety ?? ""))
                {
                    throw Contract("mine source safety unavailable");
                }

                var position = row.Cell;
                var snapshotToken = row.SourceSnapshot?.Token ?? "";
                if (position is null || position.X is null || position.Z is null || position.X < 0 || position.Z < 0 || !IsValidId(snapshotToken))
                {
                    throw Contract("mine source snapshot unavailable");
                }

                cell = new Domain.Cell(position.X.Value, position.Z.Value);
                token = snapshotToken;
                if (row.Taken is null || row.Designated != (row.DesignatedTick is not null)
                    || (row.DesignatedTick is { } designatedTick && (designatedTick < 0 || designatedTick > snapshot.Context.Tick)))
                {
                    throw Contract("invalid mine source designation age or taken");
                }
            }

            seen.Add(row.Source.Id!);
            rows.Add(new Policy.ResourceSource
            {
                ThingId = row.Source.Id!,
                Yield = units,
                Distance = row.Distance.Value,
                Method = method,
                Designated = row.Designated.Value,
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
    private static Policy.ResourceStorage ResourceDecodeStorage(PbObservations.StorageCapacity? storage, string resource, BridgeCatalog catalog)
    {
        if (storage is null || storage.Resource != resource)
        {
            throw Contract("resource storage identity mismatch");
        }

        var stackLimit = catalog.StackLimit(resource);
        if (stackLimit <= 0)
        {
            throw Contract("invalid resource storage stack limit");
        }

        var cells = new List<Domain.Cell>(storage.Candidates.Count);
        foreach (var cell in storage.Candidates)
        {
            cells.Add(new Domain.Cell(cell.X.GetValueOrDefault(), cell.Z.GetValueOrDefault()));
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
