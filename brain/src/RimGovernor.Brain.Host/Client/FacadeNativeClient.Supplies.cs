using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.Host;

// Go packed_install.go and neuroformer_items.go: the supply-census reads of one definition's loose items.
internal sealed partial class FacadeNativeClient
{
    /// <summary>Go's <c>ReadPackedItems</c>: lists the colony's spawned, unheld packed items of one packed definition.</summary>
    internal async Task<(IReadOnlyList<PackedItem> Items, BridgeResult Receipt)> ReadPackedItemsAsync(PbCommon.Identity identity, string packedDef, CancellationToken cancellationToken)
    {
        if (!PlacementReads.Passes(() => BridgeFunctions.ValidateIdentity(identity)) || !BridgeFunctions.IsValidId(packedDef))
        {
            throw BridgeFunctions.Contract("invalid packed items read");
        }
        var reply = await Facade.ListSuppliesAsync(PlacementReads.OursUnheldSuppliesRequest(identity, packedDef), cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            throw BridgeFunctions.Contract("unknown packed items fields");
        }
        if (reply.Outcome?.Unavailable is { } unavailable)
        {
            throw Unavailable(unavailable);
        }
        if (reply.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        if (reply.Outcome?.Observed is not { } snapshot)
        {
            throw BridgeFunctions.Contract("missing packed items outcome");
        }
        if (!PlacementReads.Passes(() => BridgeFunctions.ValidateContext(snapshot.Context)) || !BridgeFunctions.SameIdentity(snapshot.Context!.Identity, identity))
        {
            throw BridgeFunctions.Contract("invalid packed items context");
        }
        var output = new List<PackedItem>();
        foreach (var row in snapshot.Stocks)
        {
            if (row is null || (row.Definition?.DefName ?? "") != packedDef)
            {
                throw BridgeFunctions.Contract("invalid packed items row");
            }
            foreach (var item in row.Items)
            {
                if (!BridgeFunctions.ValidRef(item.Item)
                    || !BridgeFunctions.ValidRef(item.Inner)
                    || !BridgeFunctions.IsValidId(item.InnerDefName ?? "")
                    || (item.Quality is { } quality && !BridgeFunctions.ValidQuality(quality))
                    || !double.IsFinite(item.MarketValue.GetValueOrDefault())
                    || item.MarketValue.GetValueOrDefault() < 0)
                {
                    throw BridgeFunctions.Contract("invalid packed item");
                }
                output.Add(new PackedItem
                {
                    Id = item.Item.Id ?? "",
                    Inner = item.Inner.Id ?? "",
                    InnerDef = item.InnerDefName ?? "",
                    Quality = item.Quality.GetValueOrDefault(),
                    QualityKnown = item.Quality is not null,
                    MarketValue = item.MarketValue.GetValueOrDefault(),
                });
            }
        }
        return (output, NoReceipt);
    }

    /// <summary>
    /// Go's <c>ReadNeuroformerItems</c>: lists the exact ids of the player's unforbidden stacks of
    /// <paramref name="def"/> lying on the map, sorted: the item a colonist uses on itself (UseItem). The royalty read
    /// carries only the count and lags this read. A stack with any forbidden unit is left out, since the supplies row
    /// counts forbidden units without naming them.
    /// </summary>
    internal async Task<(IReadOnlyList<string> Items, BridgeResult Receipt)> ReadNeuroformerItemsAsync(PbCommon.Identity identity, string def, CancellationToken cancellationToken)
    {
        if (!PlacementReads.Passes(() => BridgeFunctions.ValidateIdentity(identity)) || !BridgeFunctions.IsValidId(def))
        {
            throw BridgeFunctions.Contract("invalid neuroformer scope");
        }
        var reply = await Facade.ListSuppliesAsync(PlacementReads.OursUnheldSuppliesRequest(identity, def), cancellationToken).ConfigureAwait(false);
        if (reply?.Outcome?.Failure is { } failure)
        {
            throw Failure(failure);
        }
        return (PlacementReads.DecodeNeuroformerItems(reply, identity, def), NoReceipt);
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

        /// <summary>Go's <c>decodeNeuroformerItems</c>.</summary>
        internal static List<string> DecodeNeuroformerItems(PbObservations.ListSuppliesReply? reply, PbCommon.Identity identity, string def)
        {
            if (reply is null)
            {
                throw BridgeFunctions.Contract("invalid neuroformer reply");
            }
            var v = reply.Outcome?.Observed;
            if (v is null || !Passes(() => BuildingContext(v.Context, identity, 0, false)))
            {
                throw BridgeFunctions.Contract("neuroformer census unavailable");
            }
            if (v.Completeness is null || v.Completeness.Filtered.GetValueOrDefault() != 0)
            {
                throw BridgeFunctions.Contract("incomplete neuroformer census");
            }
            var output = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stock in v.Stocks)
            {
                if (stock is null
                    || stock.Units is null
                    || stock.Forbidden is null
                    || stock.Units.GetValueOrDefault() < 0
                    || stock.Forbidden.GetValueOrDefault() < 0
                    || stock.Forbidden.GetValueOrDefault() > stock.Units.GetValueOrDefault()
                    || (stock.Definition?.DefName ?? "") != def)
                {
                    throw BridgeFunctions.Contract("invalid neuroformer stock");
                }
                foreach (var item in stock.Items)
                {
                    var id = item.Item?.Id ?? "";
                    if (!BridgeFunctions.ValidRef(item.Item) || !seen.Add(id))
                    {
                        throw BridgeFunctions.Contract("neuroformer entity mismatch");
                    }

                    // Native issues distinguish ineligible items from an incomplete census.
                    if (item.Snapshot is null || stock.Forbidden.GetValueOrDefault() != 0)
                    {
                        continue;
                    }
                    if (!RefSnapshot(item.Snapshot, item.Item, v.Context))
                    {
                        throw BridgeFunctions.Contract("neuroformer CAS scope mismatch");
                    }
                    output.Add(id);
                }
            }
            output.Sort(StringComparer.Ordinal);
            return output;
        }
    }
}
