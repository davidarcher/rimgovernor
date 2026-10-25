namespace RimGovernor.Brain.Runtime;

/// <summary>
/// One pass's view of the colony's packed stock: each packed definition is read at most once and shared by
/// every owner of the pass. Stock comes first: an owner needing a piece asks <see cref="InstallAsync"/> and builds
/// only when it answers none. Not safe for concurrent use.
/// </summary>
internal sealed class PackedStock(object native, RimGovernor.Brain.GameFacts.Common.Identity identity)
{
    private readonly GameClient? _source = native as GameClient;
    private readonly Dictionary<string, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.StockItem>> _read = new(StringComparer.Ordinal);
    // Packed things Install handed out this pass.
    private readonly Dictionary<string, bool> _claimed = new(StringComparer.Ordinal);

    /// <summary>The packed items of one packed definition; null when the source cannot read stock.</summary>
    public async Task<IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.StockItem>?> ItemsAsync(string packedDef, CancellationToken ct)
    {
        if (_source is null)
        {
            return null;
        }
        if (_read.TryGetValue(packedDef, out var done))
        {
            return done;
        }
        var items = await _source.ReadPackedItemsAsync(identity, packedDef, ct).ConfigureAwait(false);
        _read[packedDef] = items;
        return items;
    }

    /// <summary>
    /// The move that installs a stored packed piece of <paramref name="def"/> (packed as <paramref name="packedDef"/>)
    /// at anchor/rot; null when none is stored.
    /// </summary>
    public async Task<MoveBuilding?> InstallAsync(string packedDef, string def, Cell anchor, Rotation rot, CancellationToken ct)
    {
        var items = await ItemsAsync(packedDef, ct).ConfigureAwait(false) ?? [];
        foreach (var item in items)
        {
            var packed = item.Packed ?? throw new ObservationContractException("packed stock lacks its building");
            if (packed.DefName != def || (_claimed.TryGetValue(packed.Id, out var taken) && taken))
            {
                continue;
            }
            try
            {
                var move = new MoveBuilding(packed.Id, def, anchor, rot);
                _claimed[packed.Id] = true;
                return move;
            }
            catch (DomainException)
            {
                _claimed[packed.Id] = false;
                throw;
            }
        }
        return null;
    }
}
