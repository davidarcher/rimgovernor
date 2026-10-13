namespace RimGovernor.Brain.Policy;

// Go: combat_groups.go
//
// Divide and conquer: a raid split into groups on different flanks gets one squad per group. The group nearest
// the layout choke (the line's middle without one) keeps the hold; each other group gets a share of the line's
// gunners proportional to its hostile count, posted at firing cells the game proposes around the midpoint of
// the group's centroid and home (the line's middle), and a fallback cell one step toward home that the squad
// takes, for good, once a group hostile closes to the fallback range.

/// <summary>
/// One raid group's squad. Hostiles are the group's members when it was given its squad; an empty Pawns means
/// the line had no gunner to spare or the game proposed no cells, and the fight does not ask again for that
/// group until it re-forms.
/// </summary>
public sealed record CombatGroup
{
    public List<PawnId> Hostiles { get; set; } = [];

    public List<PawnId> Pawns { get; set; } = [];

    public List<Cell> Cells { get; set; } = [];

    public List<Cell> Fallback { get; set; } = [];

    public bool FellBack { get; set; }

    public Cell Centroid { get; set; }

    public CombatGroup Copy() => this with { Hostiles = [.. Hostiles], Pawns = [.. Pawns], Cells = [.. Cells], Fallback = [.. Fallback] };
}

internal sealed class RaidGroup
{
    public List<PawnId> Hostiles { get; } = [];

    public List<Cell> Cells { get; } = [];

    public Cell Centroid { get; set; }
}

public static partial class PolicyFunctions
{
    /// <summary>The single-link gap between hostiles of one group: a hostile within this many cells (8-way) of a group member joins it.</summary>
    internal const int GroupGap = 8;

    /// <summary>The smallest cluster that counts as a group; stragglers stay the main line's.</summary>
    internal const int GroupMinHostiles = 2;

    /// <summary>The 8-way distance at which a group hostile drives the squad back to its fallback cells.</summary>
    internal const int GroupFallbackRange = 3;

    /// <summary>
    /// Clusters the live hostiles with known cells, single-link at the group gap, keeping clusters of the
    /// minimum size or more, in id order of their first member.
    /// </summary>
    private static List<RaidGroup> RaidGroups(CombatView view, CombatTuning t)
    {
        var hs = RankThreats(view).Where(h => h.Cell.IsKnown).ToList();
        hs = PolicySort.ByLess(hs, (a, b) => a.Id < b.Id);
        var group = new int[hs.Count];
        for (var i = 0; i < group.Length; i++)
        {
            group[i] = i;
        }

        int Find(int i)
        {
            while (group[i] != i)
            {
                i = group[i];
            }

            return i;
        }

        for (var i = 0; i < hs.Count; i++)
        {
            for (var j = i + 1; j < hs.Count; j++)
            {
                hs[i].Cell.TryGetValue(out var a);
                hs[j].Cell.TryGetValue(out var b);
                if (Within(a, b, t.GroupGap))
                {
                    int ri = Find(i), rj = Find(j);
                    group[Math.Max(ri, rj)] = Math.Min(ri, rj);
                }
            }
        }

        var output = new List<RaidGroup>();
        var index = new Dictionary<int, int>();
        for (var i = 0; i < hs.Count; i++)
        {
            var r = Find(i);
            if (!index.TryGetValue(r, out var k))
            {
                k = output.Count;
                index[r] = k;
                output.Add(new RaidGroup());
            }

            hs[i].Cell.TryGetValue(out var c);
            output[k].Hostiles.Add(hs[i].Id);
            output[k].Cells.Add(c);
        }

        output.RemoveAll(g => g.Hostiles.Count < GroupMinHostiles);
        foreach (var g in output)
        {
            long x = 0, z = 0;
            foreach (var c in g.Cells)
            {
                x += c.X;
                z += c.Z;
            }

            long n = g.Cells.Count;
            g.Centroid = new Cell((int)(x / n), (int)(z / n));
        }

        return output;
    }

    /// <summary>The layout line's middle cell, and its choke (or that middle again without one).</summary>
    private static (Cell Home, Cell Choke)? GroupHome(CombatView view)
    {
        if (!view.Layout.TryGetValue(out var layout) || layout.Firing.Count == 0)
        {
            return null;
        }

        long x = 0, z = 0;
        foreach (var c in layout.Firing.Items)
        {
            x += c.X;
            z += c.Z;
        }

        long n = layout.Firing.Count;
        var home = new Cell((int)(x / n), (int)(z / n));
        if (layout.Choke.TryGetValue(out var choke))
        {
            return (home, choke);
        }

        return (home, home);
    }

    /// <summary>The raid's groups other than the one nearest the choke, that no squad answers yet; none unless the raid is split.</summary>
    private static List<RaidGroup> SideGroups(CombatView view, CombatMemory m)
    {
        var home = GroupHome(view);
        var groups = RaidGroups(view, m.EffectiveTuning());
        if (home is not { } h || groups.Count < 2)
        {
            return [];
        }

        var main = 0;
        for (var i = 0; i < groups.Count; i++)
        {
            if (Distance2(groups[i].Centroid, h.Choke) < Distance2(groups[main].Centroid, h.Choke))
            {
                main = i;
            }
        }

        var output = new List<RaidGroup>();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var answered = m.Groups.Any(s => g.Hostiles.Any(x => s.Hostiles.Contains(x)));
            if (i != main && !answered)
            {
                output.Add(g);
            }
        }

        return output;
    }

    /// <summary>Midway between a group's centroid and home.</summary>
    private static Cell GroupAnchor(Cell home, RaidGroup g) => new((home.X + g.Centroid.X) / 2, (home.Z + g.Centroid.Z) / 2);

    /// <summary>The next unanswered side group's firing_cells ask on a hold.</summary>
    private static GeometryRequest? GroupAsk(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Hold || m.MechLure)
        {
            return null;
        }

        var side = SideGroups(view, m);
        if (side.Count == 0 || GroupHome(view) is not { } home)
        {
            return null;
        }

        return FiringCellsAsk(view, GroupAnchor(home.Home, side[0]), side[0].Centroid);
    }

    /// <summary>The hold's line gunners no flank or squad has taken, in pawn order.</summary>
    private static List<PawnId> GroupGunners(CombatMemory m)
    {
        var output = new List<PawnId>();
        foreach (var r in m.Roles)
        {
            if (!r.Ranged || r.Duty != CombatDuty.None || r.Retreat || r.Cell is null || r.Mortar is not null
                || (m.Flank is not null && m.Flank.Pawns.Contains(r.Pawn)) || m.Groups.Any(g => g.Pawns.Contains(r.Pawn)))
            {
                continue;
            }

            output.Add(r.Pawn);
        }

        return output;
    }

    /// <summary>
    /// Gives the first side group its squad from the answered proposals: the free gunners times the group's
    /// share of the grouped hostiles, at least one, always leaving one on the main line.
    /// </summary>
    private static void GroupDetach(CombatView view, GeometryReply geometry, CombatMemory m)
    {
        var side = SideGroups(view, m);
        if (side.Count == 0 || GroupHome(view) is not { } homes)
        {
            return;
        }

        var home = homes.Home;
        var g = side[0];
        var s = new CombatGroup { Hostiles = [.. g.Hostiles], Centroid = g.Centroid };
        try
        {
            var total = 0;
            foreach (var rg in RaidGroups(view, m.EffectiveTuning()))
            {
                total += rg.Hostiles.Count;
            }

            var gunners = GroupGunners(m);
            var n = Math.Max(1, gunners.Count * g.Hostiles.Count / total);
            n = Math.Min(n, gunners.Count - 1);
            if (n <= 0)
            {
                return;
            }

            var taken = new HashSet<Cell>();
            foreach (var r in m.Roles)
            {
                if (r.Cell is { } cell)
                {
                    taken.Add(cell);
                }
            }

            var cells = NearestFree(geometry, GroupAnchor(home, g), taken);
            n = Math.Min(n, cells.Count);
            if (n == 0)
            {
                return;
            }

            s.Pawns = gunners.GetRange(gunners.Count - n, n);
            s.Cells = cells.GetRange(0, n);
            foreach (var c in s.Cells)
            {
                s.Fallback.Add(new Cell(c.X + Sign(home.X - c.X), c.Z + Sign(home.Z - c.Z)));
            }
        }
        finally
        {
            m.Groups.Add(s);
        }
    }

    /// <summary>
    /// Posts each squad on its cells (its fallback cells once a live group hostile closes to the fallback
    /// range) and points it at its group's top-ranked live hostile.
    /// </summary>
    private static void GroupSquads(CombatView view, CombatMemory m)
    {
        var ranked = RankThreats(view);
        foreach (var g in m.Groups)
        {
            if (g.Pawns.Count == 0)
            {
                continue;
            }

            PawnId target = default;
            foreach (var h in ranked)
            {
                if (!g.Hostiles.Contains(h.Id))
                {
                    continue;
                }

                if (target.IsEmpty)
                {
                    target = h.Id;
                }

                if (h.Cell.TryGetValue(out var c) && g.Cells.Any(s => Within(s, c, GroupFallbackRange)))
                {
                    g.FellBack = true;
                }
            }

            foreach (var r in m.Roles)
            {
                var j = g.Pawns.IndexOf(r.Pawn);
                if (j < 0)
                {
                    continue;
                }

                r.Cell = g.FellBack ? g.Fallback[j] : g.Cells[j];
                r.Retreat = g.FellBack;
                if (!target.IsEmpty)
                {
                    r.Target = target;
                }
            }
        }
    }
}
