using System.Text;

namespace RimGovernor.Brain.Domain;

/// <summary>Native EditZoneCells' direction. Go <c>CellEditMode</c>.</summary>
public enum CellEditMode
{
    Add,
    Remove,
}

/// <summary>
/// Grows or shrinks one exact zone (native EditZoneCells). The zone keeps its native id, so a zone_create claim
/// on it survives the edit. Cells are canonical (sorted, deduplicated, nonempty); the result must stay contiguous
/// (native refuses a split). Go <c>NewZoneCellEdit</c> is the constructor.
/// </summary>
public sealed record ZoneCellEdit : IActionPayload
{
    public ZoneCellEdit(string zone, CellEditMode mode, IEnumerable<Cell> cells)
    {
        var rows = cells.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
        DomainException.Require(Identity.IsValidText(zone) && Wire.IsDefined(mode) && rows.Count != 0, "invalid zone cell edit");
        for (var i = 0; i < rows.Count; i++)
        {
            DomainException.Require(rows[i].X >= 0 && rows[i].Z >= 0 && !(i > 0 && rows[i - 1] == rows[i]), "invalid zone cell edit cell");
        }
        Zone = zone;
        Mode = mode;
        Cells = EquatableArray.From(rows);
    }

    public string Zone { get; }

    public CellEditMode Mode { get; }

    public EquatableArray<Cell> Cells { get; }
}

/// <summary>Says what a <see cref="StockpilePatch"/>'s target id names. Go <c>StorageTargetKind</c>.</summary>
public enum StorageTargetKind
{
    Zone,
    Building,
}

/// <summary>
/// Replaces one storage target's priority and filter (native PatchStockpile): a stockpile zone or a player
/// storage building such as a shelf. The filter's base preset resets the target's filter, so the patch is a full
/// replacement. Role is the planner role key the patch claims the target for; empty is untagged. Go
/// <c>NewStockpilePatch</c> is the constructor.
/// </summary>
public sealed record StockpilePatch : IActionPayload
{
    public StockpilePatch(StorageTargetKind kind, string target, StockpileFilter filter, StockpilePriority priority, string role)
    {
        DomainException.Require(
            Wire.IsDefined(kind)
                && Identity.IsValidText(target)
                && ZoneCreate.ValidStockpilePriority(priority)
                && (role.Length == 0 || (Identity.IsValidText(role) && Encoding.UTF8.GetByteCount(role) <= 128)),
            "invalid stockpile patch"
        );
        TargetKind = kind;
        Target = target;
        Filter = filter;
        Priority = priority;
        Role = role;
    }

    public string Target { get; }

    public StorageTargetKind TargetKind { get; }

    public StockpileFilter Filter { get; }

    public StockpilePriority Priority { get; }

    public string Role { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewZoneCellEditAction(ActionId id, ZoneCellEdit e) => Create(id, ActionKind.ZoneCellEdit, e);

    /// <summary>Go <c>Action.ZoneCellEdit() (ZoneCellEdit, bool)</c>: the payload when this is a zone_cell_edit action, otherwise null.</summary>
    public ZoneCellEdit? ZoneCellEdit => PayloadOf<ZoneCellEdit>(ActionKind.ZoneCellEdit);

    public static DomainAction NewStockpilePatchAction(ActionId id, StockpilePatch p) => Create(id, ActionKind.StockpilePatch, p);

    /// <summary>Go <c>Action.StockpilePatch() (StockpilePatch, bool)</c>: the payload when this is a stockpile_patch action, otherwise null.</summary>
    public StockpilePatch? StockpilePatch => PayloadOf<StockpilePatch>(ActionKind.StockpilePatch);
}
