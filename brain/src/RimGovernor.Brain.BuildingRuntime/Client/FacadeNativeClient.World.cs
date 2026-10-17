using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.BuildingRuntime;

// ReadWorldProgression and its request. The progression census decoder is WorldProgressionRead.DecodeWorldProgressionSnapshot.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// The settlements within <paramref name="settlementRadius"/> tiles (0: exact tile) of
    /// <paramref name="tile"/>, with the requested tile's longitude.
    /// </summary>
    internal async Task<WorldRead> ReadWorldAsync(PbCommon.Identity identity, int tile, double settlementRadius, CancellationToken cancellationToken)
    {
        if (tile < 0 || settlementRadius < 0)
        {
            throw BridgeContractException.Contract("invalid world read request");
        }

        var request = new PbObservations.WorldRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Tile = tile, SettlementRadius = settlementRadius };
        var reply = await Facade.ReadWorldAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        return WorldSelected(outcome.Observed!);
    }

    private static WorldRead WorldSelected(PbObservations.WorldSnapshot v)
    {
        var rows = new List<SettlementFact>(v.Settlements.Count);
        foreach (var row in v.Settlements)
        {
            var fact = new SettlementFact
            {
                CanTrade = row.CanTrade is { } canTrade ? Known(canTrade) : Unknown<bool>(),
                TraderKind = row.TraderKind ?? "",
                Id = row.Id ?? "",
                Label = row.Label ?? "",
                Tile = row.Tile ?? 0,
                Player = row.Player ?? false,
                SnapshotToken = row.Snapshot?.Token ?? "",
            };
            if (row.Faction is not null)
            {
                fact = fact with
                {
                    FactionId = row.Faction.Id ?? "",
                    FactionDefName = row.FactionDefName ?? "",
                    Relation = row.Relation ?? "",
                    FactionSnapshotToken = row.FactionSnapshot?.Token ?? "",
                };
                if (row.Goodwill is { } goodwill)
                {
                    fact = fact with { Goodwill = goodwill, GoodwillKnown = true };
                }
            }

            rows.Add(fact);
        }

        var longitude = v.Tile is { Longitude: { } lon } ? Known(lon) : Unknown<double>();
        return new WorldRead { Context = v.Context, Settlements = rows, Longitude = longitude };
    }

    /// <summary>
    /// The world progression census (maps, caravans, quests, factions, sites).
    /// </summary>
    internal async Task<WorldProgressionRead> ReadWorldProgressionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.WorldProgressionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var reply = await Facade.ReadWorldProgressionAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Outcome;
        if (outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        return WorldProgressionRead.DecodeWorldProgressionSnapshot(outcome.Observed!);
    }
}
