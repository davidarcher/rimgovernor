using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// Go packed_install.go and neuroformer_items.go: the supply-census reads of one definition's loose items.
public sealed partial class FacadeNativeClient
{
    /// <summary>Go's <c>ReadPackedItems</c>: lists the colony's spawned, unheld packed items of one packed definition.</summary>
    internal async Task<IReadOnlyList<PackedItem>> ReadPackedItemsAsync(PbCommon.Identity identity, string packedDef, CancellationToken cancellationToken)
    {
        if (!PlacementReads.Passes(() => WireValidation.ValidateIdentity(identity)) || !WireValidation.IsValidId(packedDef))
        {
            throw BridgeContractException.Contract("invalid packed items read");
        }
        var reply = await Facade.ListSuppliesAsync(PlacementReads.OursUnheldSuppliesRequest(identity, packedDef), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var snapshot = reply.Outcome.Observed!;
        var output = new List<PackedItem>();
        foreach (var row in snapshot.Stocks)
        {
            foreach (var item in row.Items)
            {
                output.Add(new PackedItem
                {
                    Id = item.Item.Id ?? "",
                    Inner = item.Inner?.Id ?? "",
                    InnerDef = item.InnerDefName ?? "",
                    Quality = (int)item.Quality.GetValueOrDefault(),
                    QualityKnown = item.Quality is not null,
                    MarketValue = item.MarketValue.GetValueOrDefault(),
                });
            }
        }
        return output;
    }

    /// <summary>
    /// Go's <c>ReadNeuroformerItems</c>: lists the exact ids of the player's unforbidden stacks of
    /// <paramref name="def"/> lying on the map, sorted: the item a colonist uses on itself (UseItem). The royalty read
    /// carries only the count and lags this read. A stack with any forbidden unit is left out, since the supplies row
    /// counts forbidden units without naming them.
    /// </summary>
    internal async Task<IReadOnlyList<string>> ReadNeuroformerItemsAsync(PbCommon.Identity identity, string def, CancellationToken cancellationToken)
    {
        if (!PlacementReads.Passes(() => WireValidation.ValidateIdentity(identity)) || !WireValidation.IsValidId(def))
        {
            throw BridgeContractException.Contract("invalid neuroformer scope");
        }
        var reply = await Facade.ListSuppliesAsync(PlacementReads.OursUnheldSuppliesRequest(identity, def), cancellationToken).ConfigureAwait(false);
        if (reply.Outcome.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome.Failure is { } failure)
        {
            throw Failure(failure);
        }
        var output = new List<string>();
        foreach (var stock in reply.Outcome.Observed!.Stocks)
        {
            foreach (var item in stock.Items)
            {
                // Native issues distinguish ineligible items from an incomplete census.
                if (item.Snapshot is null || stock.Forbidden != 0)
                {
                    continue;
                }
                output.Add(item.Item.Id ?? "");
            }
        }
        output.Sort(StringComparer.Ordinal);
        return output;
    }

    internal static partial class PlacementReads
    {
        /// <summary>The supplies read of one definition's player-owned, unheld stacks (packed items and neuroformers).</summary>
        internal static PbObservations.ListSuppliesRequest OursUnheldSuppliesRequest(PbCommon.Identity identity, string def) =>
            new()
            {
                Scope = new PbObservations.ReadScope { ExpectedIdentity = identity },
                Filter = new PbObservations.StockFilter { DefNames = [def], Ownership = PbObservations.StockOwnership.Ours, IncludeHeld = false },
            };
    }
}
