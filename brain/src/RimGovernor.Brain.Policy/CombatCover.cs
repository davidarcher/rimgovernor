namespace RimGovernor.Brain.Policy;

// Go: combat_cover.go

/// <summary>
/// Combat.geometry's cover for one (cell, hostile) pair: the game's CoverUtility block chance at the cell
/// against the hostile's shot, and the block chance the hostile keeps against a shot from the cell. Straight
/// past one sandbag is 0.40, diagonal past two 0.48, sandbag and wall at a right angle 0.83-0.9675; the policy
/// only compares them.
/// </summary>
public sealed record CoverLine
{
    public PawnId Hostile { get; init; }

    public double Cover { get; init; }

    public double HostileCover { get; init; }

    public bool LineOfFire { get; init; }
}

/// <summary>One candidate cell with its cover against each hostile.</summary>
public sealed record ScoredCell
{
    public Cell Cell { get; init; }

    public EquatableArray<CoverLine> Lines { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// A firing cell's worth against the approach: the mean cover the cell gets against the hostiles it can
    /// shoot, less the mean cover those hostiles keep against it (the enemy's diagonal cover near the
    /// approach). A cell with no line of fire scores on every hostile.
    /// </summary>
    public static double CoverScore(ScoredCell s)
    {
        var lines = s.Lines.Items.Where(l => l.LineOfFire).ToList();
        if (lines.Count == 0)
        {
            lines = [.. s.Lines.Items];
        }

        if (lines.Count == 0)
        {
            return 0;
        }

        double own = 0, theirs = 0;
        foreach (var l in lines)
        {
            own += l.Cover;
            theirs += l.HostileCover;
        }

        return (own - theirs) / lines.Count;
    }

    /// <summary>
    /// The in-bounds cells within one step (8-way) of the line, not on it: the cells cover_behind_line
    /// considers, named instead when the stop's proposal goes to the choke.
    /// </summary>
    private static List<Cell> AroundLine(IReadOnlyList<Cell> line)
    {
        var output = new List<Cell>();
        foreach (var l in line)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    var c = new Cell(l.X + dx, l.Z + dz);
                    if (c.X >= 0 && c.Z >= 0 && !line.Contains(c) && !output.Contains(c))
                    {
                        output.Add(c);
                    }
                }
            }
        }

        return output;
    }

    /// <summary>
    /// The cells around the line that the game reported standable with some cover against a hostile: the
    /// cover_behind_line rule applied to the game's own scores.
    /// </summary>
    private static List<Cell> CoveredAround(IReadOnlyList<Cell> line, GeometryReply geometry)
    {
        var covered = new HashSet<Cell>();
        foreach (var s in geometry.Scored.Items)
        {
            foreach (var l in s.Lines.Items)
            {
                if (l.Cover > 0)
                {
                    covered.Add(s.Cell);
                }
            }
        }

        return [.. AroundLine(line).Where(c => covered.Contains(c) && geometry.Stands(c))];
    }

    /// <summary>
    /// Orders cells best cover score first. Cells the geometry did not score keep their order after the
    /// scored ones; ties keep input order, so without a geometry reply the order is unchanged.
    /// </summary>
    public static List<Cell> RankByCover(IReadOnlyList<Cell> cells, IReadOnlyList<ScoredCell> scored)
    {
        if (scored.Count == 0)
        {
            return [.. cells];
        }

        var score = new Dictionary<Cell, double>(scored.Count);
        foreach (var s in scored)
        {
            score[s.Cell] = CoverScore(s);
        }

        return PolicySort.ByLess(
            cells,
            (a, b) =>
            {
                var oka = score.TryGetValue(a, out var sa);
                var okb = score.TryGetValue(b, out var sb);
                if (oka != okb)
                {
                    return oka;
                }

                return sa > sb;
            }
        );
    }
}
