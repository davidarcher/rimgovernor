using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

// ReadWorldProgression returns the native census directly.
public sealed partial class GameClient
{
    /// <summary>
    /// The settlements within <paramref name="settlementRadius"/> tiles (0: exact tile) of
    /// <paramref name="tile"/>, and the scope map's tile.
    /// </summary>
    internal Task<RimGovernor.Brain.GameFacts.Observations.WorldSnapshot> ReadWorldAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, int tile, double settlementRadius, CancellationToken cancellationToken)
    {
        if (tile < 0 || settlementRadius < 0)
        {
            throw BridgeContractException.Contract("invalid world read request");
        }

        return Facade.ReadWorldAsync(new RimGovernor.Brain.GameOrders.Observations.WorldRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }, Settlements = new RimGovernor.Brain.GameFacts.Observations.SettlementSearch { Tile = tile, Radius = settlementRadius } }, cancellationToken);
    }

    /// <summary>
    /// The world progression census (maps, caravans, quests, factions, sites).
    /// </summary>
    internal async Task<RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot> ReadWorldProgressionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.WorldProgressionRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity } };
        var snapshot = await Facade.ReadWorldProgressionAsync(request, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }
}
