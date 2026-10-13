namespace RimGovernor.Brain.Policy;

/// <summary>
/// The per-cell answer the review walks over, projected from a native site read. Cleared is open walkable
/// ground; Blocked is a visible cell that is neither cleared nor natively eligible (a structure the fog hid,
/// deep water, faction-owned rock, a native refusal) and stays standing as part of the room's walls; Rock is the
/// visible mineable definition. (Go <c>ExcavationCellState</c>.)
/// </summary>
public sealed record ExcavationCellState
{
    public Cell Cell { get; init; }

    public bool Fogged { get; init; }

    public bool Cleared { get; init; }

    public bool Blocked { get; init; }

    public bool Eligible { get; init; }

    public string Rock { get; init; } = "";
}

/// <summary>
/// One bounded decision over a target's current cell states. Stage is the next cells to designate, in target
/// order and at most stageLimit long: uncleared, visible, natively eligible cells a miner can reach through the
/// access cell, a cleared cell or another cell of the same stage. Kept are the blocked cells the room is dug
/// around. Remaining counts the uncleared cells that are neither kept nor stageable yet. Unknown means part of
/// the remainder is still fogged, so the caller re-observes instead of deciding. Corridor reports whether every
/// corridor cell is cleared or still diggable. Complete is a target with nothing left to stage, nothing unknown
/// and an open corridor. (Go <c>ExcavationReview</c>.)
/// </summary>
public sealed record ExcavationReview
{
    public IReadOnlyList<Cell> Stage { get; init; } = [];

    public IReadOnlyList<Cell> Kept { get; init; } = [];

    public int Remaining { get; init; }

    public bool Unknown { get; init; }

    public bool Corridor { get; init; }

    public bool Complete { get; init; }
}

public static partial class PolicyFunctions
{
    internal static readonly Cell[] OrthogonalSteps = [new(0, 1), new(1, 0), new(0, -1), new(-1, 0)];

    /// <summary>Go <c>ReviewExcavation</c>: walks the target once and classifies every cell.</summary>
    public static ExcavationReview ReviewExcavation(ExcavationTarget target, IReadOnlyList<ExcavationCellState> states, int stageLimit)
    {
        var order = target.Cells();
        var byCell = new Dictionary<Cell, ExcavationCellState>(states.Count);
        foreach (var s in states)
        {
            byCell[s.Cell] = s;
        }
        var open = new HashSet<Cell> { target.Access };
        foreach (var c in order)
        {
            if (byCell.TryGetValue(c, out var s) && s.Cleared)
            {
                open.Add(c);
            }
        }
        bool AdjacentOpen(Cell c) => OrthogonalSteps.Any(d => open.Contains(new Cell(c.X + d.X, c.Z + d.Z)));
        var corridor = new HashSet<Cell>(target.Corridor);
        var stage = new List<Cell>();
        var kept = new List<Cell>();
        var remaining = 0;
        var unknown = false;
        var corridorClear = true;
        var corridorOpen = true;
        foreach (var c in order)
        {
            if (!byCell.TryGetValue(c, out var s))
            {
                unknown = true;
                remaining++;
                continue;
            }
            if (s.Cleared)
            {
                continue;
            }
            if (s.Fogged)
            {
                unknown = true;
                remaining++;
                continue;
            }
            if (s.Blocked || !s.Eligible)
            {
                kept.Add(c);
                if (corridor.Contains(c))
                {
                    corridorClear = false;
                }
                continue;
            }
            if (corridor.Contains(c))
            {
                corridorOpen = false;
            }
            if (stage.Count >= stageLimit || !AdjacentOpen(c))
            {
                remaining++;
                continue;
            }
            stage.Add(c);
            open.Add(c);
        }
        return new ExcavationReview
        {
            Stage = stage,
            Kept = kept,
            Remaining = remaining,
            Unknown = unknown,
            Corridor = corridorClear,
            Complete = corridorClear && corridorOpen && !unknown && stage.Count == 0,
        };
    }
}
