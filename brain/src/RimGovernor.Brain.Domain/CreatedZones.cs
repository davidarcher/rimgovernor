namespace RimGovernor.Brain.Domain;

/// <summary>
/// The native identity and actual footprint of one component created by a stockpile rectangle. Existing player
/// zones are never claims. Go <c>CreatedZone</c>.
/// </summary>
public sealed record CreatedZone(string Id, EquatableArray<Cell> Cells)
{
    public CreatedZone(string id, IEnumerable<Cell> cells)
        : this(id, EquatableArray.From(cells)) { }
}

/// <summary>
/// The stockpile zones a rectangle placement created, canonical (sorted by id, each footprint connected and
/// inside the requested ground, no overlap). Go <c>CreatedZones</c> (<c>NewCreatedZones</c> is the constructor).
/// </summary>
public sealed record CreatedZones
{
    public CreatedZones(ZoneCreate z, IEnumerable<CreatedZone> rows)
    {
        var input = rows.ToList();
        DomainException.Require(z.Kind == ZoneKind.Stockpile && input.Count != 0, "missing stockpile placement");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cells = new HashSet<Cell>();
        var allowed = new HashSet<Cell>(z.Cells);
        var canonical = new List<CreatedZone>(input.Count);
        foreach (var row in input)
        {
            DomainException.Require(Identity.IsValidText(row.Id) && ids.Add(row.Id), "invalid created stockpile identity");
            var footprint = ZoneCreate.CanonicalConnectedCells(row.Cells);
            foreach (var c in footprint)
            {
                DomainException.Require(allowed.Contains(c) && cells.Add(c), "created stockpile footprint outside rectangle or overlapping");
            }
            canonical.Add(new CreatedZone(row.Id, footprint));
        }
        Rows = EquatableArray.From(canonical.OrderBy(r => r.Id, StringComparer.Ordinal));
    }

    public EquatableArray<CreatedZone> Rows { get; }
}

public sealed partial record Progress
{
    /// <summary>
    /// Records an applied stockpile rectangle's receipt together with the native identities and actual footprints
    /// of the zones it created.
    /// </summary>
    public Progress RecordStockpileReceipt(AttemptId attempt, Receipt receipt, IEnumerable<CreatedZone> rows)
    {
        var list = rows.ToList();
        var zone = Action.ZoneCreate;
        if (zone is not { Kind: ZoneKind.Stockpile } || receipt != Receipt.Accepted || list.Count == 0)
        {
            throw new DomainException("stockpile identities require an applied rectangle placement");
        }
        var placements = new CreatedZones(zone, list);
        return RecordReceiptCore(attempt, receipt).With(v => v with { Stockpiles = Known(placements) });
    }
}
