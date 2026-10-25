using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// The supply-census reads of one definition's loose items.
public sealed partial class FacadeNativeClient
{
    /// <summary>Lists the colony's spawned, unheld packed items of one packed definition.</summary>
    internal async Task<IReadOnlyList<PbObservations.StockItem>> ReadPackedItemsAsync(PbCommon.Identity identity, string packedDef, CancellationToken cancellationToken)
    {
        var snapshot = await Facade.ListSuppliesAsync(SupplyRequests.OursUnheldSuppliesRequest(identity, packedDef), cancellationToken).ConfigureAwait(false);
        var output = new List<PbObservations.StockItem>();
        foreach (var row in snapshot.Stocks)
        {
            foreach (var item in row.Items)
            {
                // A packed def's stack is always a packed building.
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
    internal async Task<IReadOnlyList<string>> ReadNeuroformerItemsAsync(PbCommon.Identity identity, string def, CancellationToken cancellationToken)
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
        /// <summary>The supplies read of one definition's player-owned, unheld stacks (packed items and neuroformers).</summary>
        internal static PbObservations.ListSuppliesRequest OursUnheldSuppliesRequest(PbCommon.Identity identity, string def) =>
            new()
            {
                Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
                Filter = new PbObservations.StockFilter { DefNames = [def], Category = PbObservations.StockCategory.Haulable, Ownership = PbObservations.StockOwnership.Ours, IncludeHeld = false },
            };
    }
}
