using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>The map plus the door state that moves.</summary>
internal sealed class Grid
{
    /// <summary>The PathGrid cost of a cell no pawn can enter.</summary>
    public const int ImpassableCost = 10000;

    /// <summary>GenAdj.AdjacentCells: N, E, S, W, SE, NE, NW, SW (x east, z north).</summary>
    public static readonly Cell[] Adjacent =
    [
        new(0, 1), new(1, 0), new(0, -1), new(-1, 0),
        new(1, -1), new(1, 1), new(-1, 1), new(-1, -1),
    ];

    private static readonly ThingDef NoThing = new();

    public int W { get; set; }

    public int H { get; set; }

    public List<TerrainDef> Terrain { get; } = [];

    /// <summary>Index 0 is "no thing".</summary>
    public List<ThingDef> Things { get; } = [];

    public byte[] TerrainI { get; set; } = [];

    public byte[] ThingI { get; set; } = [];

    public bool[] Roofed { get; set; } = [];

    public bool[] DoorOpen { get; set; } = [];

    public static Cell Add(Cell a, Cell b) => new(a.X + b.X, a.Z + b.Z);

    public static int Abs32(int v) => v < 0 ? -v : v;

    public static int Manhattan(Cell a, Cell b) => Abs32(a.X - b.X) + Abs32(a.Z - b.Z);

    public static int DistSq(Cell a, Cell b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    /// <summary>Ports IntVec3.AngleFlat for the offset d: the compass angle in degrees in [0, 360) with north (+z) at 0 and east (+x) at 90; zero for no offset.</summary>
    public static double AngleFlat(Cell d)
    {
        if (d.X == 0 && d.Z == 0)
        {
            return 0;
        }
        var a = Math.Atan2(d.X, d.Z) * 180 / Math.PI;
        if (a < 0)
        {
            a += 360;
        }
        return a;
    }

    public bool InBounds(Cell c) => c.X >= 0 && c.Z >= 0 && c.X < W && c.Z < H;

    public int Idx(Cell c) => (c.Z * W) + c.X;

    public Cell CellAt(int i) => new(i % W, i / W);

    public ThingDef Thing(Cell c) => Things[ThingI[Idx(c)]];

    /// <summary>Ports GenGrid.CanBeSeenOver: out of bounds blocks sight; a Full edifice blocks it unless it is an open door.</summary>
    public bool CanBeSeenOver(Cell c)
    {
        if (!InBounds(c))
        {
            return false;
        }
        var t = Thing(c);
        if (t.Fillage != Fill.Full)
        {
            return true;
        }
        return t.Door && DoorOpen[Idx(c)];
    }

    /// <summary>Ports the cover grid: a cell's cover thing is its thing when its fill percent exceeds 0.001 (CoverGrid.RecalculateCell).</summary>
    public bool HasCover(Cell c) => InBounds(c) && Thing(c).FillPercent > 0.001;

    /// <summary>Ports CoverUtility.BaseBlockChance(Thing): an open door gives none, a Full thing 0.75, otherwise its fill percent.</summary>
    public double BaseBlockChance(Cell c)
    {
        var t = Thing(c);
        if (t.Door && DoorOpen[Idx(c)])
        {
            return 0;
        }
        if (t.Fillage == Fill.Full)
        {
            return 0.75;
        }
        return t.FillPercent;
    }

    /// <summary>PathGrid.CalculatedCostAt without snow, sand and fire: the larger of the terrain's and the thing's path cost, or <see cref="ImpassableCost"/>.</summary>
    public int CellCost(Cell c) => CellCostAt(Idx(c));

    /// <summary>The cell cost for the cell at index <paramref name="i"/>.</summary>
    public int CellCostAt(int i)
    {
        var te = Terrain[TerrainI[i]];
        var th = Things[ThingI[i]];
        if (!te.Passable || th.Passability == Passability.Impassable)
        {
            return ImpassableCost;
        }
        return Math.Max(te.PathCost, th.PathCost);
    }

    public bool Walkable(Cell c) => InBounds(c) && CellCost(c) < ImpassableCost;

    /// <summary>Ports GenGrid.Standable: walkable and the thing, if any, standable.</summary>
    public bool Standable(Cell c) => Walkable(c) && Thing(c).Passability == Passability.Standable;

    /// <summary>
    /// Ports GenSight.LineOfSight(start, end, map, skipFirstCell, validator, halfXOffset, halfZOffset): a
    /// Bresenham walk from start toward end that fails on the first cell that cannot be seen over. The end
    /// cell itself is never tested.
    /// </summary>
    public bool LineOfSight(Cell start, Cell end, bool skipFirst, int halfX, int halfZ)
    {
        if (!InBounds(start) || !InBounds(end))
        {
            return false;
        }
        var sideOnEqual = start.Z < end.Z;
        if (start.X != end.X)
        {
            sideOnEqual = start.X < end.X;
        }
        var dx = Abs32(end.X - start.X);
        var dz = Abs32(end.Z - start.Z);
        int x = start.X, z = start.Z;
        var n = 1 + dx + dz;
        int xInc = -1, zInc = -1;
        if (end.X > start.X)
        {
            xInc = 1;
        }
        if (end.Z > start.Z)
        {
            zInc = 1;
        }
        dx *= 4;
        dz *= 4;
        dx += halfX * 2;
        dz += halfZ * 2;
        var errv = (dx / 2) - (dz / 2);
        while (n > 1)
        {
            var c = new Cell(x, z);
            if ((!skipFirst || c != start) && !CanBeSeenOver(c))
            {
                return false;
            }
            if (errv > 0 || (errv == 0 && sideOnEqual))
            {
                x += xInc;
                errv -= dz;
            }
            else
            {
                z += zInc;
                errv += dx;
            }
            n--;
        }
        return true;
    }

    /// <summary>
    /// Ports ShootLeanUtility.LeanShootingSourcesFromTo: the cells a shooter at <paramref name="shooterLoc"/>
    /// may lean into to see <paramref name="targetPos"/>, in the game's order, written to
    /// <paramref name="output"/> (which is cleared first).
    /// </summary>
    public void LeanSources(Cell shooterLoc, Cell targetPos, List<Cell> output)
    {
        output.Clear();
        var angle = AngleFlat(new Cell(targetPos.X - shooterLoc.X, targetPos.Z - shooterLoc.Z));
        var north = angle is > 270 or < 90;
        var south = angle is > 90 and < 270;
        var west = angle > 180;
        var east = angle < 180;
        Span<bool> blocked = stackalloc bool[8];
        for (var i = 0; i < blocked.Length; i++)
        {
            blocked[i] = !CanBeSeenOver(Add(shooterLoc, Adjacent[i]));
        }
        if (!blocked[1] && ((blocked[0] && !blocked[5] && north) || (blocked[2] && !blocked[4] && south)))
        {
            output.Add(Add(shooterLoc, new Cell(1, 0)));
        }
        if (!blocked[3] && ((blocked[0] && !blocked[6] && north) || (blocked[2] && !blocked[7] && south)))
        {
            output.Add(Add(shooterLoc, new Cell(-1, 0)));
        }
        if (!blocked[2] && ((blocked[3] && !blocked[7] && west) || (blocked[1] && !blocked[4] && east)))
        {
            output.Add(Add(shooterLoc, new Cell(0, -1)));
        }
        if (!blocked[0] && ((blocked[3] && !blocked[6] && west) || (blocked[1] && !blocked[5] && east)))
        {
            output.Add(Add(shooterLoc, new Cell(0, 1)));
        }
        if (CanBeSeenOver(shooterLoc))
        {
            output.Add(shooterLoc);
        }
        Span<bool> toward = [north, east, south, west];
        for (var j = 0; j < 4; j++)
        {
            if (blocked[j] || !toward[j])
            {
                continue;
            }
            var c = Add(shooterLoc, Adjacent[j]);
            if (HasCover(c))
            {
                output.Add(c);
            }
        }
    }

    /// <summary>
    /// Ports GenSight.PointsOnLineOfSight(start, end), the IEnumerable overload: the cells of the line from
    /// start to end, both included, in walking order, each passed to <paramref name="visit"/> until it returns
    /// false.
    /// </summary>
    public static void PointsOnLine(Cell start, Cell end, Func<Cell, bool> visit)
    {
        var sideOnEqual = start.Z < end.Z;
        if (start.X != end.X)
        {
            sideOnEqual = start.X < end.X;
        }
        var dx = Abs32(end.X - start.X);
        var dz = Abs32(end.Z - start.Z);
        int x = start.X, z = start.Z;
        int xInc = -1, zInc = -1;
        if (end.X > start.X)
        {
            xInc = 1;
        }
        if (end.Z > start.Z)
        {
            zInc = 1;
        }
        var errv = dx - dz;
        dx *= 2;
        dz *= 2;
        for (var n = 1 + Abs32(end.X - start.X) + Abs32(end.Z - start.Z); n > 0; n--)
        {
            if (!visit(new Cell(x, z)))
            {
                return;
            }
            if (errv > 0 || (errv == 0 && sideOnEqual))
            {
                x += xInc;
                errv -= dz;
            }
            else
            {
                z += zInc;
                errv += dx;
            }
        }
    }

    /// <summary>Resets the palettes from a scenario's.</summary>
    public void LoadPalettes(IReadOnlyList<TerrainDef> terrain, IReadOnlyList<ThingDef> things)
    {
        Terrain.Clear();
        Terrain.AddRange(terrain);
        Things.Clear();
        Things.Add(NoThing);
        Things.AddRange(things);
    }
}
