namespace RimGovernor.Brain.Policy;

// A blocked tier (#2973). A tier the plan holds in place can find a tree, a rock chunk or a new building on a
// cell it needs. The two ways out are priced in the layout's units and the cheaper is taken; neither is a veto.
//
// Clear: the work of removing what blocks it (the plant's harvestWork, the building's deconstruct work, read
// from the game's defs) at a colonist's work speed, charged at the #2945 work-day price, net of what the
// removal yields (a felled tree's wood, a deconstruction's refund). A chunk is hauled, which has no work
// amount in the defs: it costs the walk of one cell (the lower bound of a haul). Rock needs mining, whose work
// the defs do not state, so it is unpriced.
//
// Move: the build cost of the tier at its new site plus the work of removing the standing buildings of the old
// one. The downtime, the days the old tier no longer covers the lane, is not priced: a move's price is a stated
// lower bound, and a move taken carries that in its reason.

/// <summary>What stands on a cell the census reports as raider cover and the game's designators could remove.</summary>
public enum CoverKind
{
    None = 0,
    Plant,
    Chunk,
    Mineable,
    Building,
}

/// <summary>The census's identification of the thing on a cell. Stuff is the building's material, empty for a stuffless thing.</summary>
public sealed record CoverThing
{
    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public CoverKind Kind { get; init; }
}

/// <summary>
/// What removing one thing takes and gives back, from the game's defs: Work is the work amount in ticks at work
/// speed 1 and Yield the resources the removal leaves.
/// </summary>
public sealed record ClearRow
{
    public double Work { get; init; }

    public EquatableArray<Amount> Yield { get; init; }
}

/// <summary>
/// What the blocked-tier price reads: a colonist's time (the #2945 work-day price, and the work ticks a
/// work-day holds), the market values the yield and the build cost are counted in, the cost of each placed
/// definition and the clearance rows of the things on the tier's cells.
/// </summary>
public sealed record BlockPricing
{
    public WorkPrice Work { get; init; } = new();

    public double TicksPerWorkDay { get; init; }

    public double SteelValue { get; init; }

    public IReadOnlyDictionary<Resource, double> Market { get; init; } = new Dictionary<Resource, double>();

    public IReadOnlyDictionary<string, EquatableArray<Amount>> UnitCosts { get; init; } = new Dictionary<string, EquatableArray<Amount>>();

    public IReadOnlyDictionary<string, ClearRow> Rows { get; init; } = new Dictionary<string, ClearRow>();

    /// <summary>Reports the pricing with the work-day, the work ticks of one and the steel the build cost is counted against known.</summary>
    public bool Valid() => Work.Valid() && FinitePositive(TicksPerWorkDay) && FinitePositive(SteelValue);

    /// <summary>Amounts at their market value as watts, the build price's unit.</summary>
    internal bool ValueW(IEnumerable<Amount> amounts, out double result)
    {
        var value = 0.0;
        foreach (var a in amounts)
        {
            if (!Market.TryGetValue(a.Resource, out var m) || m < 0 || double.IsNaN(m) || double.IsInfinity(m))
            {
                result = 0;
                return false;
            }
            value += m * a.Count;
        }
        result = BuildW(DefaultSiteTypeWeights(), value, SteelValue);
        return true;
    }

    /// <summary>Ticks of work at a colonist's work-day price.</summary>
    internal double WorkW(double ticks) => ticks / TicksPerWorkDay * Work.WorkDayW;

    /// <summary>What removing the thing costs net of its yield.</summary>
    internal bool ClearW(CoverThing t, out double result)
    {
        result = 0;
        switch (t.Kind)
        {
            case CoverKind.Chunk:
                result = Work.CellWalkDays * Work.WorkDayW;
                return true;
            case CoverKind.Plant:
            case CoverKind.Building:
                if (!Rows.TryGetValue(PolicyFunctions.ClearKey(t.Def, t.Stuff), out var row) || double.IsNaN(row.Work) || double.IsInfinity(row.Work) || row.Work < 0)
                {
                    return false;
                }
                if (!ValueW(row.Yield, out var yield))
                {
                    return false;
                }
                result = WorkW(row.Work) - yield;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The price of moving the tier to the place the census sites it: its build cost plus the work of removing
    /// the old tier's standing buildings. Not known when the census sites no such place (or only on the blocked
    /// cells), or a cost, value or removal work is unknown.
    /// </summary>
    internal bool MoveW(IReadOnlyList<Building> standing, IReadOnlyList<DefenseTier> sited, DefenseTierName name, IReadOnlyDictionary<Cell, CoverThing> blocked, out double result)
    {
        result = 0;
        var i = -1;
        for (var k = 0; k < sited.Count; k++)
        {
            if (sited[k].Name == name)
            {
                i = k;
                break;
            }
        }
        if (i < 0 || sited[i].Buildings.Count == 0 || !Valid())
        {
            return false;
        }
        var cost = new List<Amount>();
        foreach (var b in sited[i].Buildings)
        {
            if (blocked.ContainsKey(b.Cell) || !UnitCosts.TryGetValue(b.Definition, out var unit))
            {
                return false;
            }
            cost.AddRange(unit);
        }
        if (!ValueW(cost, out var w))
        {
            return false;
        }
        foreach (var b in standing)
        {
            if (!Rows.TryGetValue(PolicyFunctions.ClearKey(b.Definition, b.Stuff), out var row))
            {
                return false;
            }
            w += WorkW(row.Work);
        }
        result = w;
        return true;
    }
}

/// <summary>The way a blocked tier is resolved.</summary>
public enum TierBlockChoice
{
    [WireName("clear")]
    Clear,
    [WireName("move")]
    Move,

    /// <summary>Neither way could be priced; the tier stays and the blockers are reported.</summary>
    [WireName("unpriced")]
    Unpriced,
}

/// <summary>One blocked tier's price. ClearW and MoveW are valid where ClearKnown and MoveKnown say so.</summary>
public sealed record TierBlockDecision
{
    public DefenseTierName Tier { get; init; }

    public EquatableArray<Cell> Blocked { get; init; }

    public double ClearW { get; init; }

    public bool ClearKnown { get; init; }

    public double MoveW { get; init; }

    public bool MoveKnown { get; init; }

    public TierBlockChoice Choice { get; init; }

    public string Reason { get; init; } = "";
}

public static partial class PolicyFunctions
{
    // Why a blocked tier went the way it did; the caller journals them as layout_plan rows (target defense_tier_block).

    /// <summary>Clearing is the cheaper way, or the only priced one.</summary>
    public const string ReasonBlockClear = "tier_clear_cheaper";

    /// <summary>Moving is the cheaper way. The downtime of the move is unpriced, so its price is a lower bound.</summary>
    public const string ReasonBlockMove = "tier_move_cheaper_downtime_unpriced";

    /// <summary>No price for either way.</summary>
    public const string ReasonBlockUnpriced = "tier_block_unpriced";

    /// <summary>Indexes BlockPricing.Rows: a def made of a stuff takes the stuff's work.</summary>
    public static string ClearKey(string def, string stuff) => def + "|" + stuff;

    /// <summary>
    /// The work ticks one colonist's work-day holds: the mean over the available colonists of their timetable's
    /// work hours at their work speed. Unknown while a schedule or speed is.
    /// </summary>
    public static Fact<double> ColonistWorkTicksPerDay(IReadOnlyList<WorkPawn> pawns)
    {
        var workers = DispatchWorkers(pawns, []);
        if (workers.Count == 0)
        {
            return Unknown<double>();
        }
        var mean = AverageWorker(workers);
        if (!mean.WorkHoursPerDay.TryGetValue(out var hours) || !mean.WorkSpeed.TryGetValue(out var speed) || !FinitePositive(hours * speed))
        {
            return Unknown<double>();
        }
        return Known(hours * GameTime.TicksPerHour * speed);
    }

    private sealed record BlockedTier(DefenseTierName Name, Dictionary<Cell, CoverThing> Blocked, List<Building> Standing);

    /// <summary>Finds the pinned tiers a thing blocks, in name order. census is the raw census (the pinned buildings not read as ground).</summary>
    private static List<BlockedTier> BlockedTiers(IEnumerable<DefenseCell> census, DefenseTierPins pins)
    {
        var byCell = new Dictionary<Cell, DefenseCell>();
        foreach (var c in census)
        {
            byCell[c.Cell] = c;
        }
        var output = new List<BlockedTier>();
        foreach (var name in pins.Tiers.Keys.OrderBy(n => n.Value, StringComparer.Ordinal))
        {
            var t = new BlockedTier(name, [], []);
            foreach (var b in pins.Tiers[name].Buildings)
            {
                if (!byCell.TryGetValue(b.Cell, out var c))
                {
                    continue;
                }
                if (c.Edifice == b.Definition)
                {
                    t.Standing.Add(b);
                }
                else if (c.Cover.Kind != CoverKind.None)
                {
                    t.Blocked[b.Cell] = c.Cover;
                }
            }
            if (t.Blocked.Count > 0)
            {
                output.Add(t);
            }
        }
        return output;
    }

    /// <summary>
    /// The things whose clearance rows PriceTierBlocks reads: what blocks a pinned tier and the pinned tiers'
    /// standing buildings (a move removes them). Sorted, without repeats.
    /// </summary>
    public static IReadOnlyList<CoverThing> BlockedThings(IEnumerable<DefenseCell> census, DefenseTierPins pins)
    {
        var seen = new Dictionary<string, CoverThing>(StringComparer.Ordinal);
        foreach (var t in BlockedTiers(census, pins))
        {
            foreach (var thing in t.Blocked.Values)
            {
                seen[ClearKey(thing.Def, thing.Stuff)] = thing;
            }
            foreach (var b in t.Standing)
            {
                seen[ClearKey(b.Definition, b.Stuff)] = new CoverThing { Def = b.Definition, Stuff = b.Stuff, Kind = CoverKind.Building };
            }
        }
        return seen.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
    }

    /// <summary>
    /// Prices every pinned tier that has a building missing from a cell something else stands on. census is the
    /// raw census; sited are the tiers the census sites with nothing pinned, the places a blocked tier could move
    /// to. Tiers come in name order.
    /// </summary>
    public static IReadOnlyList<TierBlockDecision> PriceTierBlocks(IEnumerable<DefenseCell> census, DefenseTierPins pins, IReadOnlyList<DefenseTier> sited, BlockPricing p)
    {
        var output = new List<TierBlockDecision>();
        foreach (var t in BlockedTiers(census, pins))
        {
            var blocked = t.Blocked.Keys.OrderBy(c => c.X).ThenBy(c => c.Z).ToList();
            var clearKnown = p.Valid();
            var clearW = 0.0;
            foreach (var cell in blocked)
            {
                var ok = p.ClearW(t.Blocked[cell], out var w);
                clearW += w;
                clearKnown = clearKnown && ok;
            }
            var moveKnown = p.MoveW(t.Standing, sited, t.Name, t.Blocked, out var moveW);
            var d = new TierBlockDecision
            {
                Tier = t.Name,
                Blocked = EquatableArray.From(blocked),
                ClearW = clearW,
                ClearKnown = clearKnown,
                MoveW = moveW,
                MoveKnown = moveKnown,
            };
            var (choice, reason) = ChooseBlock(d);
            output.Add(d with { Choice = choice, Reason = reason });
        }
        return output;
    }

    /// <summary>
    /// Prices a colonist's time and the build cost from the station facts; unit costs and clearance rows are
    /// the caller's. Unknown facts leave the pricing invalid.
    /// </summary>
    public static BlockPricing NewBlockPricing(StationFacts f, IReadOnlyDictionary<string, EquatableArray<Amount>> unitCosts, IReadOnlyDictionary<string, ClearRow> rows)
    {
        f.Items.TryMarketValue(SiteSteelUnit, out var steel);
        var p = new BlockPricing { Work = StationWorkPrice(f), SteelValue = steel, Market = f.Items.Market, UnitCosts = unitCosts, Rows = rows };
        if (f.WorkPawns.TryGetValue(out var pawns) && ColonistWorkTicksPerDay(pawns).TryGetValue(out var ticks))
        {
            p = p with { TicksPerWorkDay = ticks };
        }
        return p;
    }

    /// <summary>Takes the cheaper way; a tie keeps the tier where it stands.</summary>
    internal static (TierBlockChoice Choice, string Reason) ChooseBlock(TierBlockDecision d)
    {
        if (d.ClearKnown && (!d.MoveKnown || d.ClearW <= d.MoveW))
        {
            return (TierBlockChoice.Clear, ReasonBlockClear);
        }
        if (d.MoveKnown)
        {
            return (TierBlockChoice.Move, ReasonBlockMove);
        }
        return (TierBlockChoice.Unpriced, ReasonBlockUnpriced);
    }
}
