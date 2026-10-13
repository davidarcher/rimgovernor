namespace RimGovernor.Brain.Policy;

/// <summary>Says what a planned structure does with a cell, which decides what natural rock on it means.</summary>
public enum RockRole
{
    /// <summary>A wall or edge cell: natural rock there already blocks, so it is left as rock and nothing waits on it.</summary>
    Blocks = 0,

    /// <summary>A floor, door, interior, corridor or turret/turbine footprint cell: rock there is dug, then built on.</summary>
    NeedsFloor,

    /// <summary>A generator footprint or catch cell: dug like a floor, and its roof comes off as well.</summary>
    NeedsSky,
}

/// <summary>One cell of a planned structure with its role.</summary>
public sealed record RoleCell
{
    public Cell Cell { get; init; }

    public RockRole Role { get; init; }
}

/// <summary>The shared admission rock step's answer for one plan.</summary>
public sealed record RockStepResult
{
    /// <summary>The rock to excavate before building, in input order, each cell once.</summary>
    public EquatableArray<Cell> Dig { get; init; }

    /// <summary>
    /// The wall-role cells on rock, kept as natural rock; they count as built. An unlisted (fogged) wall-role
    /// cell is unseen mountain and is left too.
    /// </summary>
    public EquatableArray<Cell> Left { get; init; }

    /// <summary>The needs-sky cells carrying a removable roof, dug or already open.</summary>
    public EquatableArray<Cell> Unroof { get; init; }

    /// <summary>The needs-sky cells that cannot be opened: an unseen cell, an unknown roof or a thick mountain roof.</summary>
    public EquatableArray<Cell> Unfit { get; init; }
}

/// <summary>
/// What the game's RoofDef row says about a roof. The game refuses to designate a no-roof area under a roof
/// whose isThickRoof is set (Designator_AreaNoRoof.CanDesignateCell) and checks nothing else when a roof is
/// removed: canCollapse does not gate it, so it is not carried. Natural is the RoofDef isNatural flag: a
/// mountain roof.
/// </summary>
public readonly record struct RoofRule(bool Thick, bool Natural)
{
    /// <summary>Whether the roof can be taken off.</summary>
    public bool Removable() => !Thick;
}

/// <summary>
/// Go's <c>ErrUnknownRoof</c>: a needs-sky cell whose roof def is not in the rules. <see cref="Result"/> is
/// still complete for the other cells.
/// </summary>
public sealed class UnknownRoofException(RockStepResult result, string roofs) : DomainException("unknown roof def: " + roofs)
{
    public RockStepResult Result { get; } = result;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Classifies a planned structure's cells against the terrain facts in the frame. A needs-floor cell on
    /// rock is dug; a blocks cell on rock is left as rock. A cell the frame does not list is fogged mountain:
    /// when it needs a floor it is dug, because roof support is read from the true map when the dig is read,
    /// and when it blocks it is left as rock. A listed cell that is not rock is open and needs nothing. A
    /// needs-sky cell is dug when rock and unroofed when its roof is removable (Unroof), and is Unfit when
    /// unseen or under a thick roof. Duplicate cells collapse, and needs-floor wins over blocks for the same cell.
    /// </summary>
    public static RockStepResult RockStep(IReadOnlyList<RoleCell> planned, IReadOnlyList<SiteCell> cells) =>
        RockStepCore(planned, cells, null, out _);

    /// <summary>
    /// RockStep with the roof rules that decide a needs-sky cell: a roof is removable when its RoofDef is not
    /// thick. A roofed cell whose def is not in roofs is Unfit and <see cref="UnknownRoofException"/> is
    /// thrown naming the def; its Result is still complete for the other cells. RockStep passes no rules, so
    /// it is for plans with no needs-sky cell.
    /// </summary>
    public static RockStepResult RockStepRoofs(IReadOnlyList<RoleCell> planned, IReadOnlyList<SiteCell> cells, IReadOnlyDictionary<string, RoofRule>? roofs)
    {
        var result = RockStepCore(planned, cells, roofs, out var unknown);
        if (unknown is not null)
        {
            throw new UnknownRoofException(result, unknown);
        }
        return result;
    }

    private static RockStepResult RockStepCore(
        IReadOnlyList<RoleCell> planned, IReadOnlyList<SiteCell> cells, IReadOnlyDictionary<string, RoofRule>? roofs, out string? unknownRoofs)
    {
        var unknown = new List<string>();
        var site = new Dictionary<Cell, SiteCell>(cells.Count);
        foreach (var c in cells)
        {
            site[c.Cell] = c;
        }
        var role = new Dictionary<Cell, RockRole>(planned.Count);
        var order = new List<Cell>();
        foreach (var p in planned)
        {
            if (!role.TryGetValue(p.Cell, out var prev))
            {
                order.Add(p.Cell);
                role[p.Cell] = p.Role;
            }
            else if (p.Role > prev)
            {
                role[p.Cell] = p.Role;
            }
        }
        var dig = new List<Cell>();
        var left = new List<Cell>();
        var unroof = new List<Cell>();
        var unfit = new List<Cell>();
        foreach (var cell in order)
        {
            var listed = site.TryGetValue(cell, out var c);
            if (role[cell] == RockRole.NeedsSky)
            {
                // The native grid names a roof only where one stands: an unroofed cell reads Roofed=false
                // with the roof name unknown.
                var known = c is not null && c.Roof.TryGetValue(out var roof);
                roof = c is not null && c.Roof.TryGetValue(out var r0) ? r0 : "";
                if (c is not null && c.Roofed.TryGetValue(out var roofed) && !roofed)
                {
                    roof = "";
                    known = true;
                }
                if (!listed || !known)
                {
                    unfit.Add(cell);
                    continue;
                }
                if (roof.Length != 0)
                {
                    if (roofs is null || !roofs.TryGetValue(roof, out var rule))
                    {
                        if (!unknown.Contains(roof))
                        {
                            unknown.Add(roof);
                        }
                        unfit.Add(cell);
                        continue;
                    }
                    if (!rule.Removable())
                    {
                        unfit.Add(cell);
                        continue;
                    }
                    unroof.Add(cell);
                }
                if (RockCell(c!))
                {
                    dig.Add(cell);
                }
                continue;
            }
            if (listed && !RockCell(c!))
            {
                continue;
            }
            if (role[cell] == RockRole.Blocks)
            {
                left.Add(cell);
                continue;
            }
            dig.Add(cell);
        }
        var result = new RockStepResult
        {
            Dig = EquatableArray.From(dig),
            Left = EquatableArray.From(left),
            Unroof = EquatableArray.From(unroof),
            Unfit = EquatableArray.From(unfit),
        };
        if (unknown.Count > 0)
        {
            unknown.Sort(StringComparer.Ordinal);
            unknownRoofs = string.Join(", ", unknown);
        }
        else
        {
            unknownRoofs = null;
        }
        return result;
    }

    /// <summary>
    /// Unions the dig lists of several plans into one excavation list: a cell two plans both need is dug
    /// once. The result is sorted by Z then X so overlapping requests produce the same excavation whatever
    /// the plan order.
    /// </summary>
    public static IReadOnlyList<Cell> MergeRockDigs(params IReadOnlyList<Cell>[] digs)
    {
        var seen = new HashSet<Cell>();
        var output = new List<Cell>();
        foreach (var d in digs)
        {
            foreach (var c in d)
            {
                if (seen.Add(c))
                {
                    output.Add(c);
                }
            }
        }
        return output.OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
    }

    /// <summary>
    /// The walkable cell a miner reaches a footprint's rock from: the first listed cell outside footprint, in
    /// Z then X order, that is open ground (not occupied, walkable) and shares an edge with a footprint cell.
    /// False when the footprint is walled in by rock or unseen ground, so the caller says the rock cannot be
    /// reached instead of guessing a cell.
    /// </summary>
    public static (Cell Cell, bool Ok) RockAccess(IReadOnlyList<Cell> footprint, IReadOnlyList<SiteCell> cells)
    {
        var inside = new HashSet<Cell>(footprint);
        var open = new List<Cell>();
        Cell[] directions = [new(1, 0), new(-1, 0), new(0, 1), new(0, -1)];
        foreach (var c in cells)
        {
            var wk = c.Walkable.TryGetValue(out var walkable);
            if (inside.Contains(c.Cell) || c.Occupied() || !wk || !walkable)
            {
                continue;
            }
            foreach (var d in directions)
            {
                if (inside.Contains(new Cell(c.Cell.X + d.X, c.Cell.Z + d.Z)))
                {
                    open.Add(c.Cell);
                    break;
                }
            }
        }
        if (open.Count == 0)
        {
            return (default, false);
        }
        return (MergeRockDigs(open)[0], true);
    }
}
