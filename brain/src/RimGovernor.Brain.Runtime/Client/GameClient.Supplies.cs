using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

// The supply-census reads of one definition's loose items.
public sealed partial class GameClient
{
    /// <summary>Lists the colony's spawned, unheld native packed objects, across container definitions.</summary>
    internal async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.StockItem>> ReadPackedItemsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken)
    {
        var request = new RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest
        {
            Scope = new() { ExpectedIdentity = identity },
            Filter = new() { Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Packed,
                Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours, IncludeHeld = false },
        };
        var snapshot = await Facade.ListSuppliesAsync(request, cancellationToken).ConfigureAwait(false);
        var output = new List<RimGovernor.Brain.GameFacts.Observations.StockItem>();
        foreach (var row in snapshot.Stocks)
        {
            foreach (var item in row.Items)
            {
                // The native packed-object category requires its inner building.
                _ = item.Packed ?? throw BridgeContractException.Contract("packed stock without its building");
                output.Add(item);
            }
        }
        return output;
    }

    /// <summary>
    /// Lists the exact ids of the player's unforbidden stacks of
    /// <paramref name="def"/> lying on the map, sorted: the item a colonist uses on itself (UseItem). The royalty read
    /// carries only the count and lags this read. A stack with any forbidden unit is left out, since the supplies row
    /// counts forbidden units without naming them.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ReadNeuroformerItemsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, string def, CancellationToken cancellationToken)
    {
        var snapshot = await Facade.ListSuppliesAsync(SupplyRequests.OursUnheldSuppliesRequest(identity, def), cancellationToken).ConfigureAwait(false);
        var output = new List<string>();
        foreach (var stock in snapshot.Stocks)
        {
            foreach (var item in stock.Items)
            {
                // An item with no Allow snapshot is an ineligible loose supply.
                if (item.Snapshot is null || stock.Forbidden != 0)
                {
                    continue;
                }
                output.Add(item.Id);
            }
        }
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    internal static class SupplyRequests
    {
        /// <summary>The supplies read of one definition's player-owned, unheld stacks.</summary>
        internal static RimGovernor.Brain.GameOrders.Observations.ListSuppliesRequest OursUnheldSuppliesRequest(RimGovernor.Brain.GameFacts.Common.Identity identity, string def) =>
            new()
            {
                Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity },
                Filter = new RimGovernor.Brain.GameFacts.Observations.StockFilter { DefNames = [def], Category = RimGovernor.Brain.GameFacts.Observations.StockCategory.Haulable, Ownership = RimGovernor.Brain.GameFacts.Observations.StockOwnership.Ours, IncludeHeld = false },
            };
    }
}
