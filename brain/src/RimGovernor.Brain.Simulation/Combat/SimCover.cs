using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>One CoverInfo of a ShotReport: the cell of a thing next to the target and the adjusted chance it blocks the shot.</summary>
internal readonly record struct CoverGiver(Cell Cell, double Chance);

public sealed partial class Sim
{
    /// <summary>
    /// Ports ShootLeanUtility.CellCanSeeCell: whether a pawn standing on <paramref name="source"/> can shoot
    /// at one standing on <paramref name="dest"/>, each side leaning around corners. It is the game's
    /// line-of-fire test between cells; it is symmetric except where the game's own asymmetries are (the port
    /// keeps them).
    /// </summary>
    public bool CellCanSeeCell(Cell source, Cell dest)
    {
        var g = G;
        if (!g.InBounds(source) || !g.InBounds(dest))
        {
            return false;
        }
        if (!g.CanBeSeenOver(source) || !g.CanBeSeenOver(dest))
        {
            return false;
        }
        g.LeanSources(dest, source, _leanDest);
        // The game re-runs the same unleaned test once per dest lean cell.
        if (_leanDest.Count > 0 && g.LineOfSight(source, dest, true, 0, 0))
        {
            return true;
        }
        g.LeanSources(source, dest, _leanSource);
        foreach (var a in _leanSource)
        {
            foreach (var b in _leanDest)
            {
                if (g.LineOfSight(a, b, true, 0, 0))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Ports CoverUtility.CalculateOverallBlockChance for a pawn standing on <paramref name="target"/> shot
    /// from <paramref name="shooter"/>: the chance the eight neighbours' cover, each weighed by its angle off
    /// the firing line and its distance from the shooter, blocks the shot. <paramref name="canBenefitFromCover"/>
    /// is the target race's CanBenefitFromCover flag.
    /// </summary>
    public double CoverBlockChance(Cell target, Cell shooter, bool canBenefitFromCover)
    {
        if (!canBenefitFromCover)
        {
            return 0;
        }
        var g = G;
        float total = 0;
        foreach (var off in Grid.Adjacent)
        {
            var adj = Grid.Add(target, off);
            if (!g.InBounds(adj) || adj == shooter)
            {
                continue;
            }
            total += (1 - total) * (float)AdjustedCover(shooter, target, adj);
        }
        return total;
    }

    /// <summary>
    /// Ports CoverUtility.CalculateCoverGiverSet for a pawn on <paramref name="target"/> shot from
    /// <paramref name="shooter"/>: the eight neighbours whose cover has an adjusted block chance above 0. Unlike
    /// the overall chance, a neighbour on the shooter's own cell is not skipped.
    /// </summary>
    internal void CoverGivers(Cell target, Cell shooter, List<CoverGiver> output)
    {
        foreach (var off in Grid.Adjacent)
        {
            var adj = Grid.Add(target, off);
            if (!G.InBounds(adj))
            {
                continue;
            }
            var c = AdjustedCover(shooter, target, adj);
            if (c > 0)
            {
                output.Add(new CoverGiver(adj, c));
            }
        }
    }

    private static double AngleDifference(double a, double b) =>
        Math.Min(Math.Min(Math.Abs(a - b), Math.Abs(a + 360 - b)), Math.Abs(a - (b + 360)));

    private static bool CardinalAdjacent(Cell a, Cell b) =>
        (a.Z == b.Z && Grid.Abs32(a.X - b.X) == 1) || (a.X == b.X && Grid.Abs32(a.Z - b.Z) == 1);

    /// <summary>
    /// Ports CoverUtility.TryFindAdjustedCoverInCell: the block chance the thing on <paramref name="adj"/>
    /// gives a target on <paramref name="cell"/> against a shooter on <paramref name="shooter"/>, or 0 when adj
    /// has no cover or sits too far off the firing line.
    /// </summary>
    private double AdjustedCover(Cell shooter, Cell cell, Cell adj)
    {
        if (!G.HasCover(adj) || shooter == cell)
        {
            return 0;
        }
        var diff = AngleDifference(
            Grid.AngleFlat(new Cell(adj.X - cell.X, adj.Z - cell.Z)),
            Grid.AngleFlat(new Cell(shooter.X - cell.X, shooter.Z - cell.Z)));
        if (!CardinalAdjacent(cell, adj))
        {
            diff *= 1.75;
        }
        // The game computes the chance in float and the native reply carries the float widened to double: the
        // policy ties cells on summed cover scores, and float values sum exactly in a double where doubles do
        // not (A40).
        var chance = (float)G.BaseBlockChance(adj);
        if (diff >= 65)
        {
            return 0;
        }
        if (diff >= 52)
        {
            chance *= 0.2f;
        }
        else if (diff >= 40)
        {
            chance *= 0.4f;
        }
        else if (diff >= 27)
        {
            chance *= 0.6f;
        }
        else if (diff >= 15)
        {
            chance *= 0.8f;
        }
        double dx = shooter.X - adj.X, dz = shooter.Z - adj.Z;
        var dist = Math.Sqrt((dx * dx) + (dz * dz));
        if (dist < 1.9)
        {
            chance *= 0.3333f;
        }
        else if (dist < 2.9)
        {
            chance *= 0.66666f;
        }
        return chance;
    }
}
