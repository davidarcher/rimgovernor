namespace RimGovernor.Brain.Policy;

/// <summary>Classifies what occupies a band cell. (Go <c>FirebreakGround</c>.)</summary>
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
    /// <summary>Go <c>FirebreakGround.skipped</c>.</summary>
    public static bool Skipped(this FirebreakGround g) =>
        g is FirebreakGround.PlayerEdifice or FirebreakGround.Water or FirebreakGround.NaturalRock or FirebreakGround.StoneRuin;
}

/// <summary>What a ring cell is kept as. (Go <c>FirebreakTreatment</c>.)</summary>
public enum FirebreakTreatment
{
    Cut,
    Pave,
}

/// <summary>
/// Everything <c>PlanFirebreak</c> reads. Ground must classify every band cell; a band cell it lacks, or holds
/// unknown, leaves the plan unknown. (Go <c>FirebreakRequest</c>.)
/// </summary>
public sealed record FirebreakRequest
{
    public Fact<Bounds> Bounds { get; init; }

    public Fact<CurrentConstruction> Construction { get; init; }

    public Fact<IReadOnlyList<ConstructionClaim>> Claims { get; init; }

    public Fact<HomeCoverageObservation> Home { get; init; }

    /// <summary>The cells of every growing zone; they join the footprint so a field is enclosed rather than cut.</summary>
    public Fact<IReadOnlyList<Cell>> GrowingZones { get; init; }

    /// <summary>The cells of the accepted layout plan's footprints.</summary>
    public Fact<IReadOnlyList<Cell>> Planned { get; init; }

    public IReadOnlyDictionary<Cell, Fact<FirebreakGround>> Ground { get; init; } = new Dictionary<Cell, Fact<FirebreakGround>>();

    public Fact<ColonyStage> Stage { get; init; }

    public Tick Now { get; init; }

    /// <summary>The floor census rows.</summary>
    public IReadOnlyDictionary<string, FloorDefinition> Floors { get; init; } = new Dictionary<string, FloorDefinition>();

    /// <summary>The accessible colony stock.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Stock { get; init; }

    /// <summary>The material queued construction already claims.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> Claimed { get; init; }

    /// <summary>
    /// The tier style's aisle floor, empty when the style names none (Camp); the ring is paved with it or not at
    /// all, never with a scored substitute.
    /// </summary>
    public string Floor { get; init; } = "";
}

/// <summary>One ring cell's treatment. (Go <c>FirebreakCell</c>.)</summary>
public sealed record FirebreakCell(Cell Cell, FirebreakTreatment Treatment);

/// <summary>
/// The ring work: every non-skipped band cell with its treatment and the wooden ruins to deconstruct.
/// MaintainFlooring picks the floor a paved cell takes. (Go <c>FirebreakPlan</c>.)
/// </summary>
public sealed record FirebreakPlan
{
    public IReadOnlyList<FirebreakCell> Cells { get; init; } = [];

    public IReadOnlyList<Cell> Deconstruct { get; init; } = [];
}

public static partial class PolicyFunctions
{
    /// <summary>The Chebyshev width of the firebreak ring around the base footprint.</summary>
    public const int FirebreakWidth = 2;

    /// <summary>How long a cell stays in the ring before it may be paved: five in-game days.</summary>
    public static readonly Tick FirebreakSettleTicks = new(5L * GameTime.TicksPerDay);

    /// <summary>
    /// Go <c>PlanFirebreak</c>: derives the firebreak ring: <see cref="FirebreakRing"/>'s band less skipped,
    /// planned and claimed cells. A cell is paved when the colony is in Development, the cell has been in the
    /// ring FirebreakSettleTicks, and the stock left after claimed material covers the cheapest non-flammable
    /// floor for every such cell; otherwise it is cut. dwell maps each ring cell to its first tick in the ring;
    /// the returned map drops cells that left, so a returning cell restarts. An unknown input returns an unknown
    /// plan and dwell unchanged. Throws where Go returned an error.
    /// </summary>
    public static (Fact<FirebreakPlan> Plan, IReadOnlyDictionary<Cell, Tick> Dwell) PlanFirebreak(FirebreakRequest r, IReadOnlyDictionary<Cell, Tick> dwell)
    {
        var unknown = Unknown<FirebreakPlan>();
        var pk = r.Planned.TryGetValue(out var planned);
        var ck = r.Claims.TryGetValue(out var claims);
        var sk = r.Stage.TryGetValue(out var stage);
        if (!pk || !ck || !sk)
        {
            return (unknown, dwell);
        }
        var ring = FirebreakRing(r);
        if (!ring.TryGetValue(out var band))
        {
            return (unknown, dwell);
        }
        var skip = new HashSet<Cell>(planned!);
        foreach (var claim in claims!)
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
            if (!r.Ground.TryGetValue(c, out var fact) || !fact.TryGetValue(out var ground))
            {
                return (unknown, dwell);
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
        if (stage == ColonyStage.Development)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                if (r.Now.Value - next[cells[i].Cell].Value >= FirebreakSettleTicks.Value)
                {
                    settled.Add(i);
                }
            }
        }
        if (settled.Count > 0)
        {
            var (floor, known) = FirebreakFloor(r.Floors, r.Floor);
            if (!known)
            {
                return (unknown, dwell);
            }
            var stockKnown = r.Stock.TryGetValue(out var stock);
            var claimedKnown = r.Claimed.TryGetValue(out var claimed);
            if (!stockKnown || !claimedKnown)
            {
                return (unknown, dwell);
            }
            var left = new Dictionary<Resource, long>();
            foreach (var (k, v) in stock!)
            {
                left[k] = v - claimed!.GetValueOrDefault(k);
            }
            if (floor.Length != 0 && AffordableCells(r.Floors.GetValueOrDefault(floor)!, Known<IReadOnlyDictionary<Resource, long>>(left), settled.Count) >= settled.Count)
            {
                foreach (var i in settled)
                {
                    cells[i] = cells[i] with { Treatment = FirebreakTreatment.Pave };
                }
            }
        }
        return (Known(new FirebreakPlan { Cells = cells, Deconstruct = deconstruct }), next);
    }

    /// <summary>
    /// Judges the tier style's floor: it must be known available, terrain, priced and non-flammable. Known is
    /// false when the floor cannot be judged; an empty name with known true means none can be laid (no style, or
    /// the style's floor is unavailable or flammable).
    /// </summary>
    private static (string Floor, bool Known) FirebreakFloor(IReadOnlyDictionary<string, FloorDefinition> floors, string style)
    {
        if (style.Length == 0)
        {
            return ("", true);
        }
        var ok = floors.TryGetValue(style, out var def);
        def ??= new FloorDefinition();
        var ak = def.Available.TryGetValue(out var available);
        var tk = def.Terrain.TryGetValue(out var terrain);
        var fk = def.Flammability.TryGetValue(out var flammability);
        if (!ok || !ak || !tk || !fk || !FloorPriced(def))
        {
            return ("", false);
        }
        if (!available || !terrain || flammability > 0)
        {
            return ("", true);
        }
        return (style, true);
    }
}
