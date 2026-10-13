using RimGovernor.Brain.Bridge.Contracts;
using static RimGovernor.Brain.Bridge.Contracts.BridgeFunctions;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Host;

// Go world.go (Client.ReadWorld, worldSelected) and world_progression.go (Client.ReadWorldProgression,
// worldProgressionRequest). The progression census decoder is BridgeFunctions.DecodeWorldProgressionSnapshot.
internal sealed partial class FacadeNativeClient
{
    /// <summary>
    /// Go <c>Client.ReadWorld</c>: the settlements within <paramref name="settlementRadius"/> tiles (0: exact tile) of
    /// <paramref name="tile"/>, with the requested tile's longitude. It requires the same single complete page every
    /// observation reader enforces.
    /// </summary>
    internal async Task<(WorldRead Value, BridgeResult Receipt)> ReadWorldAsync(PbCommon.Identity identity, int tile, double settlementRadius, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        if (tile < 0 || settlementRadius < 0)
        {
            throw Contract("invalid world read request");
        }

        var request = new PbObservations.WorldRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Tile = tile, SettlementRadius = settlementRadius };
        var reply = await Facade.ReadWorldAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Observed is { } observed)
        {
            return (WorldSelected(observed, identity), NoReceipt);
        }

        throw Contract("world outcome missing");
    }

    private static WorldRead WorldSelected(PbObservations.WorldSnapshot? v, PbCommon.Identity identity)
    {
        if (v is null)
        {
            throw Contract("world snapshot missing");
        }

        ValidateContext(v.Context);
        if (!SameIdentity(v.Context!.Identity, identity))
        {
            throw Contract("world identity mismatch");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<SettlementFact>(v.Settlements.Count);
        foreach (var row in v.Settlements)
        {
            if (row is null || !IsValidId(row.Id ?? "") || !seen.Add(row.Id!))
            {
                throw Contract("invalid or duplicate world settlement");
            }

            if (row.Snapshot is null || (row.Snapshot.EntityId ?? "") != row.Id || !IsValidId(row.Snapshot.Token ?? ""))
            {
                throw Contract("world settlement CAS token unavailable");
            }

            var fact = new SettlementFact
            {
                CanTrade = row.CanTrade is { } canTrade ? Known(canTrade) : Unknown<bool>(),
                TraderKind = row.TraderKind ?? "",
                Id = row.Id!,
                Label = row.Label ?? "",
                Tile = row.Tile ?? 0,
                Player = row.Player ?? false,
                SnapshotToken = row.Snapshot.Token ?? "",
            };
            if (row.Faction is not null)
            {
                var factionId = row.Faction.Id ?? "";
                if (row.FactionSnapshot is null || (row.FactionSnapshot.EntityId ?? "") != factionId || !IsValidId(row.FactionSnapshot.Token ?? ""))
                {
                    throw Contract("world settlement faction CAS token unavailable");
                }

                fact = fact with
                {
                    FactionId = factionId,
                    FactionDefName = row.FactionDefName ?? "",
                    Relation = row.Relation ?? "",
                    FactionSnapshotToken = row.FactionSnapshot.Token ?? "",
                };
                if (row.Goodwill is { } goodwill)
                {
                    fact = fact with { Goodwill = goodwill, GoodwillKnown = true };
                }
            }

            rows.Add(fact);
        }

        var longitude = Unknown<double>();
        if (v.Tile is { Longitude: { } lon })
        {
            if (double.IsNaN(lon) || double.IsInfinity(lon) || lon < -180 || lon > 180)
            {
                throw Contract("invalid world tile longitude");
            }

            longitude = Known(lon);
        }

        return new WorldRead { Context = v.Context, Settlements = rows, Longitude = longitude };
    }

    /// <summary>
    /// Go <c>Client.ReadWorldProgression</c>: the world progression census (maps, caravans, quests, factions, sites),
    /// validated against the identity. <paramref name="includeStorage"/> asks for the storage census too.
    /// </summary>
    internal async Task<(WorldProgressionRead Value, BridgeResult Receipt)> ReadWorldProgressionAsync(PbCommon.Identity identity, bool includeStorage, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        var request = new PbObservations.WorldProgressionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, IncludeStorage = includeStorage };
        var reply = await Facade.ReadWorldProgressionAsync(request, cancellationToken).ConfigureAwait(false);
        BuildingUnknown(reply);
        var outcome = reply.Outcome;
        if (outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }

        if (outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }

        if (outcome?.Observed is { } observed)
        {
            return (DecodeWorldProgressionSnapshot(observed, identity), NoReceipt);
        }

        throw Contract("world progression outcome missing");
    }
}
