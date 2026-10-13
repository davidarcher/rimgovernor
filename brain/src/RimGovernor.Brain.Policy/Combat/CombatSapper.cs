namespace RimGovernor.Brain.Policy;

/// <summary>A predicted breach: the wall cell the sappers will open and the room behind it.</summary>
public sealed record SapperBreach
{
    /// <summary>The room's wall cell nearest the sappers.</summary>
    public Cell Wall { get; init; }

    /// <summary>The floor cell just behind the wall.</summary>
    public Cell Inside { get; init; }

    /// <summary>The unit step from Wall into the room.</summary>
    public Cell In { get; init; }

    public CombatRoom Room { get; init; } = new();
}

public static partial class PolicyFunctions
{
    /// <summary>How far inside the breach, in cells, the gunners stand.</summary>
    internal const int SapperInset = 3;

    /// <summary>The gap between gunners along the wall, in cells.</summary>
    internal const int SapperSpread = 2;

    /// <summary>The live hostiles flagged sapper or breacher, by threat rank.</summary>
    internal static List<CombatPawnState> LiveSappers(CombatView view) => RankThreats(view).Where(h => h.Sapper).ToList();

    /// <summary>
    /// Predicts where the sappers break in. They head for the base's rooms, so the target is the frame's room
    /// whose centre is nearest the live sappers' centroid, and the breach is that room's wall cell nearest the
    /// centroid (a corner moves onto the nearer side). No rooms, no sapper at a known cell, or sappers already
    /// inside a room predict nothing. Ore is not avoided: the frame has no ore cells. Null when nothing is predicted.
    /// </summary>
    internal static SapperBreach? PredictBreach(CombatView view)
    {
        double sx = 0;
        double sz = 0;
        var n = 0;
        foreach (var h in LiveSappers(view))
        {
            if (h.Cell.TryGetValue(out var c))
            {
                sx += c.X;
                sz += c.Z;
                n++;
            }
        }
        if (n == 0 || view.Rooms.Count == 0)
        {
            return null;
        }
        var cx = sx / n;
        var cz = sz / n;
        var best = -1;
        var bestD = double.PositiveInfinity;
        for (var i = 0; i < view.Rooms.Count; i++)
        {
            var interior = view.Rooms[i].Interior;
            if (interior.Width <= 0 || interior.Height <= 0)
            {
                continue;
            }
            var mx = interior.X + ((interior.Width - 1) / 2.0);
            var mz = interior.Z + ((interior.Height - 1) / 2.0);
            var d = Math.Sqrt(((cx - mx) * (cx - mx)) + ((cz - mz) * (cz - mz)));
            if (d < bestD)
            {
                best = i;
                bestD = d;
            }
        }
        if (best < 0)
        {
            return null;
        }
        var room = view.Rooms[best];
        var inner = room.Interior;
        var x0 = inner.X;
        var x1 = inner.X + inner.Width - 1;
        var z0 = inner.Z;
        var z1 = inner.Z + inner.Height - 1;
        var x = Clamp32((int)Math.Round(cx, MidpointRounding.AwayFromZero), x0 - 1, x1 + 1);
        var z = Clamp32((int)Math.Round(cz, MidpointRounding.AwayFromZero), z0 - 1, z1 + 1);
        var outX = x < x0 || x > x1;
        var outZ = z < z0 || z > z1;
        if (!outX && !outZ)
        {
            return null;
        }
        if (outX && outZ)
        {
            // A corner: step onto the side the centroid is farther past.
            if (Math.Abs(cx - Clamp32(x, x0, x1)) >= Math.Abs(cz - Clamp32(z, z0, z1)))
            {
                z = Clamp32(z, z0, z1);
                outZ = false;
            }
            else
            {
                x = Clamp32(x, x0, x1);
                outX = false;
            }
        }
        Cell step;
        if (outX && x < x0)
        {
            step = new Cell(1, 0);
        }
        else if (outX)
        {
            step = new Cell(-1, 0);
        }
        else if (z < z0)
        {
            step = new Cell(0, 1);
        }
        else
        {
            step = new Cell(0, -1);
        }
        return new SapperBreach { Wall = new Cell(x, z), Inside = new Cell(x + step.X, z + step.Z), In = step, Room = room };
    }

    internal static int Clamp32(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

    /// <summary>
    /// Floor cells of the breach's room <paramref name="depth"/> cells in from the wall (fewer when the room is
    /// shallower), <paramref name="stride"/> cells apart along the wall: straight in first, then alternating either side.
    /// </summary>
    internal static List<Cell> BreachPosts(SapperBreach b, int depth, int stride, int n)
    {
        var inner = b.Room.Interior;
        var across = new Cell(b.In.Z * b.In.Z, b.In.X * b.In.X);
        var result = new List<Cell>();
        for (var d = depth; d >= 1 && result.Count < n; d--)
        {
            var baseCell = new Cell(b.Wall.X + (b.In.X * d), b.Wall.Z + (b.In.Z * d));
            if (!b.Room.Contains(baseCell))
            {
                continue;
            }
            var reach = Math.Max(inner.Width, inner.Height);
            for (var k = 0; k <= reach && result.Count < n; k++)
            {
                foreach (var sign in new[] { 1, -1 })
                {
                    if ((k == 0 && sign < 0) || result.Count >= n)
                    {
                        continue;
                    }
                    var c = new Cell(baseCell.X + (sign * k * stride * across.X), baseCell.Z + (sign * k * stride * across.Z));
                    if (b.Room.Contains(c))
                    {
                        result.Add(c);
                    }
                }
            }
            break;
        }
        return result;
    }

    /// <summary>
    /// Posts the defenders at the predicted breach: gunners on cells SapperInset inside it, SapperSpread apart
    /// along the wall, on the top-ranked hostile (a sapper, by threat score); brawlers on the cells just inside
    /// it as blockers with no target, so none walks out to fight in the open.
    /// </summary>
    internal static List<CombatRole> SapperFormation(CombatView view, SapperBreach b)
    {
        var gunners = new List<SquadDefenderFacts>();
        var rest = new List<SquadDefenderFacts>();
        foreach (var d in view.Defenders)
        {
            if (!SquadDefenderEligible(d) || !Positive(d.Armed))
            {
                continue;
            }
            if (Positive(d.RangedEquipped))
            {
                gunners.Add(d);
            }
            else
            {
                rest.Add(d);
            }
        }
        PawnId top = default;
        var ranked = RankThreats(view);
        if (ranked.Count > 0)
        {
            top = ranked[0].Id;
        }
        var roles = new List<CombatRole>();
        var posts = BreachPosts(b, SapperInset, SapperSpread, gunners.Count);
        for (var i = 0; i < gunners.Count; i++)
        {
            roles.Add(new CombatRole { Pawn = gunners[i].Id, Target = top, Ranged = true, Cell = i < posts.Count ? posts[i] : null });
        }
        var guards = Brawlers(rest);
        var cells = BreachPosts(b, 1, 1, guards.Count);
        for (var i = 0; i < guards.Count; i++)
        {
            roles.Add(new CombatRole { Pawn = guards[i].Id, Duty = CombatDuty.Blocker, Cell = i < cells.Count ? cells[i] : null });
        }
        return SortRoles(roles);
    }

    /// <summary>
    /// The sapper row of the reaction table: a sapper raid whose predicted breach is not the one the formation
    /// guards (a new raid, or the sappers turned toward another wall) re-forms, as does a sapper formation
    /// whose raid no longer predicts a breach or whose target is down.
    /// </summary>
    internal static bool ReformSapper(CombatView view, CombatMemory m)
    {
        var b = PredictBreach(view);
        if (m.Tactic != CombatTactic.Sapper)
        {
            return b is not null;
        }
        return b is null || m.SapperBreach is null || m.SapperBreach.Value != b.Wall || SquadTargetDown(view, m);
    }
}
