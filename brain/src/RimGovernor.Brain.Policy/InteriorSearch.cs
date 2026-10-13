namespace RimGovernor.Brain.Policy;

// The interior search improves the greedy placer's layout against the interior objective (#2869). It starts
// from PlaceInRegion's layout and applies a fixed number of seeded moves (shift, rotate, mirror, swap,
// re-place one piece), keeping a move only when the layout is feasible and strictly better: no gated term
// worse (InteriorScore.WorseTerms) and at least one better. The objective has no scalar total, so a move that
// trades one term for another is never kept, and the result is no worse than the greedy seed on any gated term
// by construction. Every choice comes from a splitmix64 stream the caller seeds (room id and plan generation);
// the iteration count is the whole budget and wall time never enters.
//
// Built furniture moves for free when BuiltPiece.Movable: uninstall and reinstall labor is negligible (user
// decision, #2869), and the strictly-better acceptance rule already prevents churn, so no labor-to-score rate
// exists. Every other built piece is Pinned. Doors are not searched (they stay as given); moving one needs a
// rate between rebuild labor and the objective's terms, which is a later priced variable.
public static partial class PolicyConstants
{
    /// <summary>
    /// The move budget when InteriorSearch names none: a compute bound, not a game value. Every fixture of the
    /// score table reaches its result well inside it.
    /// </summary>
    public const int DefaultInteriorSearchIterations = 600;
}

/// <summary>The search's input beside the RegionPlacement it improves.</summary>
public sealed record InteriorSearch
{
    /// <summary>The wall cells a door may stand on; the objective's hard constraint on RegionPlacement.Doors. Null accepts any cell.</summary>
    public IReadOnlyList<Cell>? Candidates { get; init; }

    public PieceShapes Shapes { get; init; } = new();

    public DiningFurniture Dining { get; init; } = new();

    /// <summary>Weights the walks between stations; null leaves the walk terms zero, so only free floor and facility links can improve.</summary>
    public IInteriorFlows? Flows { get; init; }

    public IReadOnlyDictionary<string, double> Beauty { get; init; } = new Dictionary<string, double>();

    /// <summary>Pieces the search keeps where they stand. They block their cells for the greedy seed and count toward required counts.</summary>
    public IReadOnlyList<InteriorPiece> Pinned { get; init; } = [];

    /// <summary>The room's built pieces. The movable ones (BuiltPiece.Movable) join the search at their current cells; the rest are pinned.</summary>
    public IReadOnlyList<BuiltPiece> Built { get; init; } = [];

    /// <summary>Seeds the move stream.</summary>
    public ulong Seed { get; init; }

    /// <summary>The move budget; 0 is DefaultInteriorSearchIterations.</summary>
    public int Iterations { get; init; }
}

/// <summary>A piece already standing in the room, with the two facts the move rule needs.</summary>
/// <param name="Piece">The piece.</param>
/// <param name="Minifiable">The def's Station.Minifiable (ThingDef.Minifiable).</param>
/// <param name="HoldsState">
/// Set when the building carries state a move cannot keep: a bed with owners or a set medical/prisoner/slave use
/// (DeSpawn clears them), a workbench with bills (#2881), a storage building with contents.
/// </param>
public sealed record BuiltPiece(InteriorPiece Piece, bool Minifiable, bool HoldsState)
{
    /// <summary>The move rule: a built piece is searched when the game can pack it (minifiable) and it holds no state the move would lose. Anything else is pinned.</summary>
    public bool Movable() => Minifiable && !HoldsState;
}

/// <summary>One kept move, with the gated terms it improved: the reason the caller journals (telemetry.Decide "interior_search_move").</summary>
public sealed record SearchMove(string Kind, string Slot, IReadOnlyList<string> Improves)
{
    /// <summary>The stable reason string of a kept move.</summary>
    public string Reason() => Kind + ":" + string.Join('+', Improves);
}

/// <summary>A searched layout.</summary>
public sealed record InteriorSearchResult
{
    /// <summary>
    /// The pinned pieces first (Pinned, then the unmovable Built), then the ones that stand where the search put
    /// them: movable built pieces, then the newly placed ones.
    /// </summary>
    public IReadOnlyList<InteriorPiece> Pieces { get; init; } = [];

    internal int Fixed { get; init; }

    /// <summary>The greedy layout's measure.</summary>
    public InteriorScore Seed { get; init; } = new();

    /// <summary>The searched layout's measure.</summary>
    public InteriorScore Score { get; init; } = new();

    public IReadOnlyList<SearchMove> Moves { get; init; } = [];

    /// <summary>The pieces the caller must stand: everything but the pinned ones.</summary>
    public IReadOnlyList<InteriorPiece> Stood() => [.. Pieces.Skip(Fixed)];
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Places req's wants with the greedy placer and improves the layout by seeded local search. It throws when
    /// the greedy seed does, and returns the seed unchanged when the request has no door to measure from.
    /// </summary>
    public static InteriorSearchResult SearchInterior(RegionPlacement req, InteriorSearch s)
    {
        var (floor, seats) = SplitSeats(req.Wants);
        req = req with { Wants = [.. floor] };
        var pinned = new List<InteriorPiece>(s.Pinned);
        var movable = new List<InteriorPiece>();
        foreach (var b in s.Built)
        {
            if (b.Movable())
            {
                movable.Add(b.Piece);
            }
            else
            {
                pinned.Add(b.Piece);
            }
        }
        var reserved = new List<Cell>(req.Reserved);
        foreach (var p in pinned.Concat(movable))
        {
            reserved.AddRange(RectCells(p.Rect));
            reserved.AddRange(p.Reserves());
        }
        var greedy = req with { Search = null, Reserved = EquatableArray.From(reserved) };
        var placed = PlaceInRegion(greedy);
        IReadOnlyList<InteriorPiece> pieces = [.. pinned, .. movable, .. placed];
        var required = new Dictionary<string, int>();
        foreach (var p in pinned.Concat(movable))
        {
            required[p.Def] = required.GetValueOrDefault(p.Def) + 1;
        }
        foreach (var w in req.Wants)
        {
            required[w.Def] = required.GetValueOrDefault(w.Def) + w.Min;
        }
        var obj = new InteriorObjective
        {
            Region = req.Region,
            Candidates = s.Candidates,
            Doors = req.Doors,
            Required = required,
            Shapes = s.Shapes,
            Flows = s.Flows,
            Beauty = s.Beauty,
            Dining = s.Dining,
        };
        InteriorScore Score(IReadOnlyList<InteriorPiece> ps) => ScoreInterior(obj with { Pieces = ps });
        var cur = Score(pieces);
        var seedScore = cur;
        var free = pinned.Count;
        var moves = new List<SearchMove>();
        InteriorSearchResult Seated(InteriorSearchResult res) => res with { Pieces = [.. SeatAll(req.Region, [.. res.Pieces], seats)] };
        if (req.Doors.Count == 0 || pieces.Count == free)
        {
            return Seated(new InteriorSearchResult { Pieces = pieces, Seed = cur, Score = cur, Fixed = free });
        }
        var iters = s.Iterations;
        if (iters <= 0)
        {
            iters = DefaultInteriorSearchIterations;
        }
        var cells = req.Region.Cells();
        var bounds = req.Region.Bounds();
        var rng = SearchRand.FromState(s.Seed);
        for (var n = 0; n < iters; n++)
        {
            var (cand, kind, at, ok) = PerturbPieces(pieces, free, cells, bounds, rng);
            if (!ok)
            {
                continue;
            }
            var cs = Score(cand);
            if (!cs.Feasible())
            {
                continue;
            }
            IReadOnlyList<string> gained = [];
            if (cur.Feasible())
            {
                if (cs.WorseTerms(cur).Count > 0)
                {
                    continue;
                }
                gained = cur.WorseTerms(cs);
                if (gained.Count == 0)
                {
                    continue;
                }
            }
            pieces = cand;
            cur = cs;
            moves.Add(new SearchMove(kind, pieces[at].Slot, gained));
        }
        return Seated(new InteriorSearchResult { Pieces = pieces, Seed = seedScore, Score = cur, Fixed = free, Moves = moves });
    }

    /// <summary>
    /// Applies one random move to a copy of pieces, moving only the pieces from index free on. It returns the
    /// new layout, the move's kind and the index of the piece moved (the first, for a swap).
    /// </summary>
    private static (IReadOnlyList<InteriorPiece> Pieces, string Kind, int At, bool Ok) PerturbPieces(IReadOnlyList<InteriorPiece> pieces, int free, IReadOnlyList<Cell> cells, Rectangle bounds, SearchRand rng)
    {
        var i = free + rng.Intn(pieces.Count - free);
        var p = pieces[i];
        var output = pieces.ToArray();
        var corner = new Cell(p.Rect.X, p.Rect.Z);
        string kind;
        switch (rng.Intn(5))
        {
            case 0:
                kind = "shift";
                var d = new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) }[rng.Intn(4)];
                output[i] = Replaced(p, p.Rot, new Cell(corner.X + d.X, corner.Z + d.Z));
                break;
            case 1:
                kind = "rotate";
                output[i] = Replaced(p, RotateCW(p.Rot, 1 + rng.Intn(3)), corner);
                break;
            case 2:
                kind = "mirror";
                var rot = p.Rot switch
                {
                    Rotation.East => Rotation.West,
                    Rotation.West => Rotation.East,
                    _ => p.Rot,
                };
                output[i] = Replaced(p, rot, new Cell((2 * bounds.X) + bounds.Width - p.Rect.X - p.Rect.Width, corner.Z));
                break;
            case 3:
                kind = "swap";
                if (pieces.Count - free < 2)
                {
                    return ([], string.Empty, 0, false);
                }
                var j = free + rng.Intn(pieces.Count - free);
                var q = pieces[j];
                if (i == j || p.Def == q.Def)
                {
                    return ([], string.Empty, 0, false);
                }
                output[i] = Replaced(p, p.Rot, new Cell(q.Rect.X, q.Rect.Z));
                output[j] = Replaced(q, q.Rot, corner);
                break;
            default:
                kind = "replace";
                output[i] = Replaced(p, RotationOrder[rng.Intn(4)], cells[rng.Intn(cells.Count)]);
                break;
        }
        return (output, kind, i, true);
    }

    /// <summary>The piece stood at the footprint corner in rotation rot.</summary>
    private static InteriorPiece Replaced(InteriorPiece p, Rotation rot, Cell corner) =>
        NewInteriorPiece(p.Slot, p.Def, p.Size, rot, corner) with
        {
            InteractionOffset = p.InteractionOffset,
            Row = p.Row,
            Beside = p.Beside,
            Clear = p.Clear,
        };
}
