using System.Collections.Immutable;

namespace RimGovernor.Brain.Domain;

/// <summary>The edit an <see cref="Area"/> applies. Go <c>AreaOperation</c>.</summary>
public enum AreaOperation
{
    Create,
    SetCells,
    ClearCells,
    Delete,
}

/// <summary>One inclusive rectangle of cells. Go <c>AreaRect</c>.</summary>
public readonly record struct AreaRect(int MinX, int MinZ, int MaxX, int MaxZ);

/// <summary>Canonicalisation shared by the cell-set payloads (Go repeated it per file).</summary>
internal static class CanonicalCells
{
    /// <summary>Cells sorted by X then Z; throws <paramref name="invalidMessage"/> on a negative or duplicate cell.</summary>
    public static ImmutableArray<Cell> Sort(IEnumerable<Cell> cells, string invalidMessage)
    {
        var rows = cells.OrderBy(c => c.X).ThenBy(c => c.Z).ToImmutableArray();
        for (var i = 0; i < rows.Length; i++)
        {
            var c = rows[i];
            DomainException.Require(c.X >= 0 && c.Z >= 0 && !(i > 0 && rows[i - 1] == c), invalidMessage);
        }
        return rows;
    }
}

/// <summary>
/// Creates, edits or deletes one bot-owned allowed area, or edits the home area (an AreaIntent on
/// Actions/Apply). A bot-owned area is the Area_Allowed labelled with its key; native never touches an area
/// the player made. Holds the operation, the bot area key (empty for the home area) and canonical cells
/// (sorted, deduplicated, as a rectangle cover). set_cells and clear_cells need cells; delete takes none;
/// create may seed cells. The home area is never created or deleted.
/// </summary>
public sealed record Area : IActionPayload
{
    /// <summary>Go <c>NewArea</c>; throws <see cref="DomainException"/> when invalid.</summary>
    public Area(AreaOperation operation, string key, IReadOnlyCollection<Cell> cells)
        : this(operation, key, false, cells) { }

    private Area(AreaOperation operation, string key, bool pollutionClear, IReadOnlyCollection<Cell> cells)
    {
        switch (operation)
        {
            case AreaOperation.Create:
            case AreaOperation.Delete:
                DomainException.Require(Identity.IsValidText(key), "area create and delete need a bot area key");
                break;
            case AreaOperation.SetCells:
            case AreaOperation.ClearCells:
                DomainException.Require(!(key.Length != 0 && !Identity.IsValidText(key)) && cells.Count != 0, "area cell edits need cells");
                break;
            default:
                throw new DomainException("invalid area operation");
        }
        DomainException.Require(!(operation == AreaOperation.Delete && cells.Count > 0), "area delete takes no cells");
        var rows = CanonicalCells.Sort(cells, "invalid area cell");
        Operation = operation;
        Key = key;
        PollutionClear = pollutionClear;
        Rects = AreaRects(rows);
    }

    /// <summary>
    /// Go <c>NewPollutionClearArea</c>: edits the game's pollution-clear area, the cells the cleanup crew
    /// cleans: set_cells and clear_cells only, like home.
    /// </summary>
    public static Area NewPollutionClearArea(AreaOperation operation, IReadOnlyCollection<Cell> cells)
    {
        DomainException.Require(
            operation is AreaOperation.SetCells or AreaOperation.ClearCells,
            "the pollution-clear area takes set_cells and clear_cells only"
        );
        return new Area(operation, "", true, cells);
    }

    public AreaOperation Operation { get; }

    /// <summary>The bot area key; empty names the home or pollution-clear area.</summary>
    public string Key { get; }

    public bool Home => Key.Length == 0 && !PollutionClear;

    /// <summary>Names the game's pollution-clear area.</summary>
    public bool PollutionClear { get; }

    /// <summary>The canonical cover of the area edit; <see cref="Cells"/> expands it.</summary>
    public EquatableArray<AreaRect> Rects { get; }

    public IReadOnlyList<Cell> Cells => ExpandAreaRects(Rects);

    /// <summary>
    /// Go <c>AreaRects</c>: covers cells (no duplicates) with the fewest row-run rectangles a greedy vertical
    /// merge finds.
    /// </summary>
    public static EquatableArray<AreaRect> AreaRects(IEnumerable<Cell> cells)
    {
        var sorted = cells.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
        var output = new List<AreaRect>();
        var open = new List<int>(); // output indexes of rects ending on the previous row
        var prevZ = -2;
        var i = 0;
        while (i < sorted.Count)
        {
            var z = sorted[i].Z;
            var row = new List<AreaRect>();
            while (i < sorted.Count && sorted[i].Z == z)
            {
                var j = i;
                while (j + 1 < sorted.Count && sorted[j + 1].Z == z && sorted[j + 1].X == sorted[j].X + 1)
                {
                    j++;
                }
                row.Add(new AreaRect(sorted[i].X, z, sorted[j].X, z));
                i = j + 1;
            }
            var next = new List<int>();
            foreach (var r in row)
            {
                var merged = false;
                if (z == prevZ + 1)
                {
                    foreach (var k in open)
                    {
                        if (output[k].MinX == r.MinX && output[k].MaxX == r.MaxX)
                        {
                            output[k] = output[k] with { MaxZ = z };
                            next.Add(k);
                            merged = true;
                            break;
                        }
                    }
                }
                if (!merged)
                {
                    output.Add(r);
                    next.Add(output.Count - 1);
                }
            }
            open = next;
            prevZ = z;
        }
        return new EquatableArray<AreaRect>([.. output]);
    }

    /// <summary>Go <c>ExpandAreaRects</c>: lists the cells of rects row by row.</summary>
    public static List<Cell> ExpandAreaRects(IEnumerable<AreaRect> rects)
    {
        var cells = new List<Cell>();
        foreach (var r in rects)
        {
            for (var z = r.MinZ; z <= r.MaxZ; z++)
            {
                for (var x = r.MinX; x <= r.MaxX; x++)
                {
                    cells.Add(new Cell(x, z));
                }
            }
        }
        return cells;
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewAreaAction(ActionId id, Area area) => Create(id, ActionKind.Area, area);

    /// <summary>Go <c>Action.Area() (Area, bool)</c>: the payload when this is an area action, otherwise null.</summary>
    public Area? Area => PayloadOf<Area>(ActionKind.Area);
}
