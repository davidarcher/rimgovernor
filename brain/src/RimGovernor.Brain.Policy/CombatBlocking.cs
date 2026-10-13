namespace RimGovernor.Brain.Policy;

// Go: combat_blocking.go

/// <summary>A defender's special duty in a formation, beyond a firing cell and a target.</summary>
public enum CombatDuty
{
    [WireName("")]
    None,

    /// <summary>Holds a cell just outside the choke.</summary>
    Blocker,

    /// <summary>Waits to relieve a hurt blocker.</summary>
    Reserve,

    /// <summary>Intercepts melee attackers on the gunners.</summary>
    Peeler,

    Kiter,

    Evacuee,

    Doorway,

    Interceptor,

    Harasser,

    Tank,
}

/// <summary>The choke and the our_side anchor of the adjacent_to_choke ask.</summary>
internal readonly record struct BlockingChokeCells(Cell Choke, Cell OurSide);

public static partial class PolicyFunctions
{
    /// <summary>How many brawlers block the choke (up to 3).</summary>
    private const int MaxChokeBlockers = 3;

    /// <summary>
    /// How far along the corridor direction the adjacent_to_choke ask's our_side anchor lies: far enough that
    /// only the choke's neighbours on our side are strictly nearer to it.
    /// </summary>
    private const int ChokeAnchorSteps = 3;

    /// <summary>
    /// Says whether Formation runs the melee blocking formation: a layout with a known choke and at least one
    /// eligible brawler. It returns the choke and the our_side anchor for the adjacent_to_choke ask.
    /// </summary>
    private static BlockingChokeCells? BlockingChoke(CombatView view)
    {
        if (!view.Layout.TryGetValue(out var layout))
        {
            return null;
        }

        var ok = layout.Choke.TryGetValue(out var choke);
        var (v, vok) = TowardVector(layout.Toward);
        var (rest, _) = SplitTanks(view);

        // A manhunter pack with an exploder is never melee-blocked.
        if (!ok || !vok || Brawlers(rest).Count == 0 || (ManhunterPack(view) && LiveExploders(view).Count > 0))
        {
            return null;
        }

        var side = new Cell(choke.X + (ChokeAnchorSteps * v.X), choke.Z + (ChokeAnchorSteps * v.Z));
        if (side.X < 0 || side.Z < 0)
        {
            return null;
        }

        return new BlockingChokeCells(choke, side);
    }

    /// <summary>A cell on the floor of a standing room, or null when the frame has no rooms.</summary>
    private static Func<Cell, bool>? InsideRooms(CombatView view)
    {
        if (view.Rooms.Count == 0)
        {
            return null;
        }

        return c => view.Rooms.Items.Any(r => r.Contains(c));
    }

    /// <summary>
    /// The hostile cells the hold's blockers stop at the choke: with a live colonist standing in the
    /// blockers' row (a neighbour of the choke one step toward the colony), a hostile at the choke or in front
    /// of it within two cells is held there, however hurt the blockers are. Null without a known choke.
    /// </summary>
    private static Func<Cell, bool>? ChokeHeld(CombatView view, CombatLayout layout)
    {
        var ok = layout.Choke.TryGetValue(out var choke);
        var (v, vok) = TowardVector(layout.Toward);
        if (!ok || !vok)
        {
            return null;
        }

        int Project(Cell c) => ((c.X - choke.X) * v.X) + ((c.Z - choke.Z) * v.Z);
        bool Near(Cell c, int r) => c.X - choke.X >= -r && c.X - choke.X <= r && c.Z - choke.Z >= -r && c.Z - choke.Z <= r;

        var colonist = new HashSet<PawnId>(view.Defenders.Items.Select(d => d.Id));
        var blocked = false;
        foreach (var p in view.Pawns.Items)
        {
            var known = p.Cell.TryGetValue(out var at);
            if (colonist.Contains(p.Id) && !p.Dead && !p.Downed && known && Near(at, 1) && Project(at) == 1)
            {
                blocked = true;
            }
        }

        if (!blocked)
        {
            return null;
        }

        return c => Near(c, 2) && Project(c) <= 0;
    }

    /// <summary>The eligible melee-only defenders, best armored first (unknown armor last), then by id.</summary>
    private static List<SquadDefenderFacts> Brawlers(IEnumerable<SquadDefenderFacts> defenders) =>
        ByArmor([.. defenders.Where(d => SquadDefenderEligible(d) && Positive(d.MeleeEquipped) && !Positive(d.RangedEquipped))]);

    /// <summary>Sorts defenders best armored first (unknown armor last), then by id.</summary>
    private static List<SquadDefenderFacts> ByArmor(List<SquadDefenderFacts> output) =>
        PolicySort.ByLess(
            output,
            (a, b) =>
            {
                var ik = a.Armor.TryGetValue(out var ai);
                var jk = b.Armor.TryGetValue(out var aj);
                if (ik != jk)
                {
                    return ik;
                }

                if (ik && ai != aj)
                {
                    return ai > aj;
                }

                return a.Id < b.Id;
            }
        );

    /// <summary>The brawler pool with the relieved blockers last, in relief order, so a re-formation keeps the rotation.</summary>
    private static List<SquadDefenderFacts> Rotated(IReadOnlyList<SquadDefenderFacts> pool, IReadOnlyList<PawnId> relieved)
    {
        var fresh = pool.Where(d => !relieved.Contains(d.Id)).ToList();
        var rested = new List<SquadDefenderFacts>();
        foreach (var id in relieved)
        {
            foreach (var d in pool)
            {
                if (d.Id == id)
                {
                    rested.Add(d);
                }
            }
        }

        fresh.AddRange(rested);
        return fresh;
    }

    /// <summary>
    /// Gives a hold's brawlers their duties. In a blocking formation the best-armored take the game's proposed
    /// cells just outside the choke, at most three, and the next is the reserve (on the next proposed cell
    /// when there is one); with two or more brawlers one is always held back, so a hurt blocker can be
    /// relieved. The next brawler after those is the peeler, waiting at its home behind the gunners when the
    /// game found that cell standable. Blockers the reserve relieved rank last.
    /// </summary>
    private static List<CombatRole> BrawlerRoles(
        CombatView view,
        IReadOnlyList<SquadDefenderFacts> defenders,
        bool blocking,
        GeometryReply geometry,
        IReadOnlyList<PawnId> relieved
    )
    {
        var pool = Rotated(Brawlers(defenders), relieved);
        var proposals = geometry.Proposals.Items;
        var n = 0;
        if (blocking)
        {
            n = Math.Min(MaxChokeBlockers, Math.Min(proposals.Count, pool.Count));
            if (n == pool.Count && n >= 2)
            {
                n--;
            }
        }

        var roles = new List<CombatRole>();
        for (var i = 0; i < n; i++)
        {
            roles.Add(new CombatRole { Pawn = pool[i].Id, Cell = proposals[i], Duty = CombatDuty.Blocker });
        }

        if (n > 0 && n < pool.Count)
        {
            var role = new CombatRole { Pawn = pool[n].Id, Duty = CombatDuty.Reserve };
            if (n < proposals.Count)
            {
                role.Cell = proposals[n];
            }

            roles.Add(role);
            n++;
        }

        if (n < pool.Count)
        {
            var role = new CombatRole { Pawn = pool[n].Id, Duty = CombatDuty.Peeler };
            if (PeelerHome(view) is { } home && geometry.Stands(home))
            {
                role.Cell = home;
                role.Home = home;
            }

            roles.Add(role);
        }

        return roles;
    }

    /// <summary>
    /// The reaction table's relief row: on a serious injury to a blocker, the reserve takes the blocker's cell
    /// and the hurt blocker pulls back to where the reserve stands. Without a live reserve whose place is
    /// known nothing changes, and the fall-back row takes the stop. It reports whether it swapped.
    /// </summary>
    private static bool RelieveBlocker(CombatView view, StopEvent stop, CombatMemory m)
    {
        if (stop.Kind != CombatStopKind.SeriousInjury)
        {
            return false;
        }

        int hurt = -1, reserve = -1;
        for (var i = 0; i < m.Roles.Count; i++)
        {
            var r = m.Roles[i];
            if (r.Pawn == stop.Pawn && r.Duty == CombatDuty.Blocker && r.Cell is not null)
            {
                hurt = i;
            }
            else if (r.Duty == CombatDuty.Reserve && reserve < 0)
            {
                reserve = i;
            }
        }

        if (hurt < 0 || reserve < 0)
        {
            return false;
        }

        var back = m.Roles[reserve].Cell;
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var at) && p.Id == m.Roles[reserve].Pawn)
            {
                back = at;
            }
        }

        if (back is null)
        {
            return false;
        }

        m.Roles[reserve].Cell = m.Roles[hurt].Cell;
        m.Roles[reserve].Duty = CombatDuty.Blocker;

        // The relieved blocker's pull-back is a retreat: it passes the aim guard and the fall-back leaves it in place.
        m.Roles[hurt].Cell = back;
        m.Roles[hurt].Duty = CombatDuty.None;
        m.Roles[hurt].Target = default;
        m.Roles[hurt].Retreat = true;
        m.Relieved.RemoveAll(id => id == stop.Pawn);
        m.Relieved.Add(stop.Pawn);
        return true;
    }
}
