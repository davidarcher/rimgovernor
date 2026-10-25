namespace RimGovernor.Brain.Policy;

/// <summary>Classifies what occupies a band cell.</summary>
public enum FirebreakGround
{
    /// <summary>Ground the ring keeps cut or paves.</summary>
    Open,

    /// <summary>A player-owned wall or other edifice.</summary>
    PlayerEdifice,

    Water,

    NaturalRock,

    /// <summary>A non-player, non-flammable edifice.</summary>
    StoneRuin,

    /// <summary>A non-player, flammable edifice: deconstructed, then kept as a normal ring cell.</summary>
    WoodenRuin,
}

public static class FirebreakGroundExtensions
{
    public static bool Skipped(this FirebreakGround g) =>
        g is FirebreakGround.PlayerEdifice or FirebreakGround.Water or FirebreakGround.NaturalRock or FirebreakGround.StoneRuin;
}

/// <summary>What a ring cell is kept as.</summary>
public enum FirebreakTreatment
{
    Cut,
    Pave,
}

/// <summary>
/// The base the firebreak band surrounds: the colony extent's evidence and the growing zones. Every member is read;
/// an evidence the colony has not read raises <see cref="RequiredFactException"/> where the footprint is built.
/// </summary>
public sealed record FirebreakFootprint
{
    public required Bounds Bounds { get; init; }

    public required CurrentConstruction Construction { get; init; }

    public required EquatableArray<ConstructionClaim> Claims { get; init; }

    public required RimGovernor.Brain.GameFacts.Observations.HomeCoverageFacts Home { get; init; }

    /// <summary>The cells of every growing zone; they join the footprint so a field is enclosed rather than cut.</summary>
    public required EquatableArray<Cell> GrowingZones { get; init; }
}

/// <summary>
/// Everything <c>PlanFirebreak</c> reads. Ground must classify every band cell; a band cell it lacks raises
/// <see cref="RequiredFactException"/>.
/// </summary>
public sealed record FirebreakRequest
{
    public required FirebreakFootprint Footprint { get; init; }

    /// <summary>The cells of the accepted layout plan's footprints.</summary>
    public required EquatableArray<Cell> Planned { get; init; }

    public required IReadOnlyDictionary<Cell, FirebreakGround> Ground { get; init; }

    public required Tick Now { get; init; }

    /// <summary>The floor census rows.</summary>
    public required IReadOnlyDictionary<string, FloorDefinition> Floors { get; init; }

    /// <summary>The accessible colony stock.</summary>
    public required IReadOnlyDictionary<Resource, long> Stock { get; init; }

    /// <summary>The material queued construction already claims.</summary>
    public required IReadOnlyDictionary<Resource, long> Claimed { get; init; }

    /// <summary>
    /// The tier style's aisle floor, null when the style names none (Camp); the ring is paved with it or not at
    /// all, never with a scored substitute.
    /// </summary>
    public required string? Floor { get; init; }
}

/// <summary>One ring cell's treatment.</summary>
public sealed record FirebreakCell(Cell Cell, FirebreakTreatment Treatment);

/// <summary>
/// The ring work: every non-skipped band cell with its treatment and the wooden ruins to deconstruct.
/// The rooms/flooring Need picks the floor a paved cell takes.
/// </summary>
public sealed record FirebreakPlan
{
    public IReadOnlyList<FirebreakCell> Cells { get; init; } = [];

    public IReadOnlyList<Cell> Deconstruct { get; init; } = [];
}

public static class FirebreakRules
{
    /// <summary>The Chebyshev width of the firebreak ring around the base footprint.</summary>
    public const int FirebreakWidth = 2;

    /// <summary>How long a cell stays in the ring before it may be paved: five in-game days.</summary>
    public static readonly Tick FirebreakSettleTicks = new(5L * GameTime.TicksPerDay);

    /// <summary>
    /// Derives the firebreak ring: <see cref="FirebreakRingRules.FirebreakRing"/>'s band less skipped,
    /// planned and claimed cells. A cell is paved when the colony is in Development, the cell has been in the
    /// ring FirebreakSettleTicks, and the stock left after claimed material covers the cheapest non-flammable
    /// floor for every such cell; otherwise it is cut. dwell maps each ring cell to its first tick in the ring;
    /// the returned map drops cells that left, so a returning cell restarts. A band cell the ground census does
    /// not classify, or a tier floor the census cannot judge, raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static (FirebreakPlan Plan, IReadOnlyDictionary<Cell, Tick> Dwell) PlanFirebreak(FirebreakRequest r, IReadOnlyDictionary<Cell, Tick> dwell)
    {
        var band = FirebreakRingRules.FirebreakRing(r.Footprint);
        var skip = new HashSet<Cell>(r.Planned);
        foreach (var claim in r.Footprint.Claims)
        {
            skip.UnionWith(claim.Cells);
        }
        var cells = new List<FirebreakCell>();
        var deconstruct = new List<Cell>();
        var next = new Dictionary<Cell, Tick>();
        foreach (var c in band)
        {
            if (skip.Contains(c))
            {
                continue;
            }
            if (!r.Ground.TryGetValue(c, out var ground))
            {
                throw new RequiredFactException("firebreak ground");
            }
            if (ground.Skipped())
            {
                continue;
            }
            if (ground == FirebreakGround.WoodenRuin)
            {
                deconstruct.Add(c);
            }
            next[c] = dwell.TryGetValue(c, out var first) ? first : r.Now;
            cells.Add(new FirebreakCell(c, FirebreakTreatment.Cut));
        }
        var settled = new List<int>();
        for (var i = 0; i < cells.Count; i++)
        {
            if (r.Now.Value - next[cells[i].Cell].Value >= FirebreakSettleTicks.Value)
            {
                settled.Add(i);
            }
        }
        if (settled.Count > 0 && FirebreakFloor(r.Floors, r.Floor) is { } floor)
        {
            var left = new Dictionary<Resource, long>();
            foreach (var (k, v) in r.Stock)
            {
                left[k] = v - r.Claimed.GetValueOrDefault(k);
            }
            if (FlooringRules.AffordableCells(r.Floors[floor], left, settled.Count) >= settled.Count)
            {
                foreach (var i in settled)
                {
                    cells[i] = cells[i] with { Treatment = FirebreakTreatment.Pave };
                }
            }
        }
        return (new FirebreakPlan { Cells = cells, Deconstruct = deconstruct }, next);
    }

    /// <summary>
    /// Judges the tier style's floor: it must be available, terrain, priced and non-flammable. Null when none can
    /// be laid (no style, or the style's floor is unavailable or flammable); a floor the census cannot judge
    /// raises <see cref="RequiredFactException"/>.
    /// </summary>
    private static string? FirebreakFloor(IReadOnlyDictionary<string, FloorDefinition> floors, string? style)
    {
        if (style is null)
        {
            return null;
        }
        if (!floors.TryGetValue(style, out var def)
            || !FlooringRules.FloorPriced(def))
        {
            throw new RequiredFactException("firebreak floor " + style);
        }
        return !def.Available || !def.Terrain || def.Flammability > 0 ? null : style;
    }
}
