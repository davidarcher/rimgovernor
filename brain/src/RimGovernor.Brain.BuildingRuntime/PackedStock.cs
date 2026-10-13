namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Lists stored packed items; a source without it builds new pieces instead (Go <c>packedSource</c>).</summary>
public interface IPackedSource
{
    Task<IReadOnlyList<PackedItem>> ReadPackedItemsAsync(PbCommon.Identity identity, string packedDef, CancellationToken ct);
}

/// <summary>
/// One pass's view of the colony's packed stock: each packed definition is read at most once and shared by
/// every owner of the pass. Stock comes first: an owner needing a piece asks <see cref="InstallAsync"/> and builds
/// only when it answers none. Not safe for concurrent use.
/// </summary>
internal sealed class PackedStock(object native, PbCommon.Identity identity)
{
    private readonly IPackedSource? _source = native as IPackedSource;
    private readonly Dictionary<string, IReadOnlyList<PackedItem>> _read = new(StringComparer.Ordinal);
    // Packed things Install handed out this pass.
    private readonly Dictionary<string, bool> _claimed = new(StringComparer.Ordinal);

    /// <summary>The packed items of one packed definition; null when the source cannot read stock.</summary>
    public async Task<IReadOnlyList<PackedItem>?> ItemsAsync(string packedDef, CancellationToken ct)
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
            if (item.InnerDef != def || (_claimed.TryGetValue(item.Inner, out var taken) && taken))
            {
                continue;
            }
            try
            {
                var move = new MoveBuilding(item.Inner, def, anchor, rot);
                _claimed[item.Inner] = true;
                return move;
            }
            catch (DomainException)
            {
                _claimed[item.Inner] = false;
                throw;
            }
        }
        return null;
    }
}
