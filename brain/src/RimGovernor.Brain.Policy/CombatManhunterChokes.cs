namespace RimGovernor.Brain.Policy;

// Go: combat_manhunter_chokes.go

/// <summary>One door of the planned rooms a wave's animals approach, the cells just inside it, and the animal nearest it.</summary>
internal sealed class WaveChoke
{
    public Cell Door { get; init; }

    public List<Cell> Cells { get; set; } = [];

    public int Animals { get; set; }

    public PawnId Nearest { get; set; }
}

public static partial class PolicyFunctions
{
    /// <summary>The smallest pack that counts as a psychic wave: fewer animals come through one door at a time.</summary>
    internal const int WaveMinAnimals = 6;

    /// <summary>Squared Euclidean distance between two cells.</summary>
    internal static long Distance2(Cell a, Cell b)
    {
        long x = (long)a.X - b.X;
        long z = (long)a.Z - b.Z;
        return (x * x) + (z * z);
    }

    /// <summary>Index of the cell nearest <paramref name="at"/> (first wins ties).</summary>
    internal static int NearestIndex(Cell at, IReadOnlyList<Cell> cells)
    {
        var best = 0;
        for (var i = 1; i < cells.Count; i++)
        {
            if (Distance2(at, cells[i]) < Distance2(at, cells[best]))
            {
                best = i;
            }
        }

        return best;
    }

    internal static bool CellLess(Cell a, Cell b) => a.X < b.X || (a.X == b.X && a.Z < b.Z);

    /// <summary>The outward unit step from a rectangle through a door on its edge; none when the door is not on an edge.</summary>
    internal static Cell? Outward(Rectangle r, Cell door)
    {
        var insideX = door.X >= r.X && door.X < r.X + r.Width;
        var insideZ = door.Z >= r.Z && door.Z < r.Z + r.Height;
        if (door.X == r.X - 1 && insideZ)
        {
            return new Cell(-1, 0);
        }

        if (door.X == r.X + r.Width && insideZ)
        {
            return new Cell(1, 0);
        }

        if (door.Z == r.Z - 1 && insideX)
        {
            return new Cell(0, -1);
        }

        if (door.Z == r.Z + r.Height && insideX)
        {
            return new Cell(0, 1);
        }

        return null;
    }

    /// <summary>The doors a psychic wave approaches: at least WaveMinAnimals animals and no exploder, then SpreadChokes.</summary>
    internal static List<WaveChoke> WaveChokes(CombatView view)
    {
        var ranked = RankThreats(view);
        if (ranked.Count < WaveMinAnimals || LiveExploders(view).Count > 0)
        {
            return [];
        }

        return SpreadChokes(view, ranked);
    }

    /// <summary>
    /// The doors a shielded melee charge approaches: when most live hostiles are melee with a worn shield,
    /// every door they approach is a choke, so the charge splits across them. Empty otherwise.
    /// </summary>
    internal static List<WaveChoke> ChargeChokes(CombatView view)
    {
        var ranked = RankThreats(view);
        var shielded = ranked.Count(h => h.Shield.IsKnown && MeleeHostile(h));
        if (2 * shielded <= ranked.Count)
        {
            return [];
        }

        return SpreadChokes(view, ranked);
    }

    /// <summary>A hostile whose weapon reaches about one cell: the known range, else the weapon def's.</summary>
    internal static bool MeleeHostile(CombatPawnState h)
    {
        var reach = h.WeaponRange;
        if (reach <= 0)
        {
            reach = h.WeaponFacts.Range;
        }

        return reach <= 1.5;
    }

    /// <summary>
    /// Counts each ranked hostile toward the planned-room door nearest it; with two or more doors approached,
    /// every approached door is a choke, most hostiles first. Empty otherwise.
    /// </summary>
    internal static List<WaveChoke> SpreadChokes(CombatView view, IReadOnlyList<CombatPawnState> ranked)
    {
        var doors = new List<Cell>();
        foreach (var r in view.Rooms.Items)
        {
            foreach (var d in r.Doors.Items)
            {
                if (!doors.Contains(d))
                {
                    doors.Add(d);
                }
            }
        }

        if (doors.Count < 2)
        {
            return [];
        }

        var byDoor = new Dictionary<Cell, WaveChoke>();
        var best = new Dictionary<Cell, long>();
        foreach (var h in ranked)
        {
            if (!h.Cell.TryGetValue(out var at))
            {
                continue;
            }

            var door = doors[NearestIndex(at, doors)];
            if (!byDoor.TryGetValue(door, out var c))
            {
                c = new WaveChoke { Door = door, Cells = InsideDoor(view, door, at) };
                byDoor[door] = c;
            }

            c.Animals++;
            var d = Distance2(at, door);
            if (c.Nearest.IsEmpty || d < best[door])
            {
                c.Nearest = h.Id;
                best[door] = d;
            }
        }

        if (byDoor.Count < 2)
        {
            return [];
        }

        return PolicySort.ByLess(byDoor.Values, (a, b) => a.Animals != b.Animals ? a.Animals > b.Animals : CellLess(a.Door, b.Door));
    }

    /// <summary>
    /// The blocker cells just inside the door, on the side of its room away from <paramref name="from"/> (an
    /// approaching animal): straight in, then the two inside diagonals, each on the room's floor.
    /// </summary>
    internal static List<Cell> InsideDoor(CombatView view, Cell door, Cell from)
    {
        var cells = new List<Cell>();
        long far = -1;
        foreach (var r in view.Rooms.Items)
        {
            if (!r.Doors.Items.Contains(door))
            {
                continue;
            }

            if (Outward(r.Interior, door) is not { } n)
            {
                continue;
            }

            var inside = new Cell(door.X - n.X, door.Z - n.Z);
            var d = Distance2(inside, from);
            if (d <= far)
            {
                continue;
            }

            far = d;
            var t = new Cell(n.Z, n.X);
            cells = [];
            foreach (var c in new[] { inside, new Cell(inside.X + t.X, inside.Z + t.Z), new Cell(inside.X - t.X, inside.Z - t.Z) })
            {
                if (r.Contains(c))
                {
                    cells.Add(c);
                }
            }
        }

        return cells;
    }

    /// <summary>
    /// Deals the brawlers, best armored first, round-robin to the chokes (most animals first), at most
    /// MaxChokeBlockers each, onto the cells just inside each door; each engages the animal nearest its door.
    /// </summary>
    internal static List<CombatRole> WaveBlockers(IReadOnlyList<WaveChoke> chokes, IReadOnlyList<SquadDefenderFacts> pool)
    {
        var roles = new List<CombatRole>();
        var next = new int[chokes.Count];
        var rest = new Queue<SquadDefenderFacts>(pool);
        while (rest.Count > 0)
        {
            var placed = false;
            for (var i = 0; i < chokes.Count; i++)
            {
                if (rest.Count == 0 || next[i] >= Math.Min(MaxChokeBlockers, chokes[i].Cells.Count))
                {
                    continue;
                }

                var cell = chokes[i].Cells[next[i]];
                next[i]++;
                roles.Add(new CombatRole { Pawn = rest.Dequeue().Id, Cell = cell, Target = chokes[i].Nearest, Duty = CombatDuty.Blocker });
                placed = true;
            }

            if (!placed)
            {
                break;
            }
        }

        return roles;
    }
}
