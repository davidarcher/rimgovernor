using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// ReadWorldProgression and its request. The progression census decoder is WorldProgressionRead.DecodeWorldProgressionSnapshot.
public sealed partial class FacadeNativeClient
{
    /// <summary>
    /// The settlements within <paramref name="settlementRadius"/> tiles (0: exact tile) of
    /// <paramref name="tile"/>, and the scope map's tile.
    /// </summary>
    internal Task<PbObservations.WorldSnapshot> ReadWorldAsync(PbCommon.Identity identity, int tile, double settlementRadius, CancellationToken cancellationToken)
    {
        if (tile < 0 || settlementRadius < 0)
        {
            throw BridgeContractException.Contract("invalid world read request");
        }

        return Facade.ReadWorldAsync(new PbObservations.WorldRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, Settlements = new PbObservations.SettlementSearch { Tile = tile, Radius = settlementRadius } }, cancellationToken);
    }

    /// <summary>
    /// The world progression census (maps, caravans, quests, factions, sites).
    /// </summary>
    internal async Task<WorldProgressionRead> ReadWorldProgressionAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var request = new PbObservations.WorldProgressionRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity } };
        var snapshot = await Facade.ReadWorldProgressionAsync(request, cancellationToken).ConfigureAwait(false);
        return WorldProgressionRead.DecodeWorldProgressionSnapshot(snapshot);
    }
}
