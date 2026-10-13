namespace RimGovernor.Brain.Policy;

// Go: combat_manhunter.go
//
// CombatTactic.Manhunter is a manhunter pack's own formation: a fight whose live hostiles are all manhunter
// animals picks it instead of the generic squad fallback.

/// <summary>One manhunter's classification.</summary>
/// <param name="Slow">An animal slower than a colonist: kitable. Unknown speed is not slow.</param>
/// <param name="Exploder">Explodes on death (boomalope, boomrat): never melee-blocked, shot only away from our pawns.</param>
public readonly record struct AnimalClass(bool Slow, bool Exploder);

public static partial class PolicyFunctions
{
    /// <summary>A baseline colonist's MoveSpeed, cells/s; an animal slower than it is kitable.</summary>
    internal const double ColonistMoveSpeed = 4.6;

    /// <summary>How close to a colonist an exploder is never shot, in cells: a boomalope's death blast and fire.</summary>
    internal const double ExploderRadius = 6.0;

    /// <summary>Sorts one animal by speed and by its death explosion.</summary>
    internal static AnimalClass ClassifyAnimal(CombatPawnState s) => new(
        s.MoveSpeed > 0 && s.MoveSpeed < ColonistMoveSpeed,
        s.Kind.Contains("Boomalope", StringComparison.Ordinal) || s.Kind.Contains("Boomrat", StringComparison.Ordinal));

    /// <summary>
    /// Reports a fight whose live hostile pawns (at least one) are all known manhunter animals or melee-only
    /// entities and mutants: a pack that charges in is fought alike whatever it is.
    /// </summary>
    public static bool ManhunterPack(CombatView view)
    {
        var down = DownPawns(view);
        var n = 0;
        foreach (var t in view.Threats.Items)
        {
            if (t.Building || Positive(t.Dead) || Positive(t.Downed) || down.Contains(ThreatPawn(t)))
            {
                continue;
            }

            if (!(Positive(t.Animal) && Positive(t.Manhunter)) && !MeleeEntity(t))
            {
                return false;
            }

            n++;
        }

        return n > 0;
    }

    /// <summary>The pawns the live state shows dead or downed.</summary>
    internal static HashSet<PawnId> DownPawns(CombatView view)
    {
        var down = new HashSet<PawnId>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Dead || p.Downed)
            {
                down.Add(p.Id);
            }
        }

        return down;
    }

    /// <summary>The live hostile exploders, by id.</summary>
    internal static Dictionary<PawnId, CombatPawnState> LiveExploders(CombatView view)
    {
        var output = new Dictionary<PawnId, CombatPawnState>();
        foreach (var h in RankThreats(view))
        {
            if (ClassifyAnimal(h).Exploder)
            {
                output[h.Id] = h;
            }
        }

        return output;
    }

    /// <summary>The live exploders within ExploderRadius of a live colonist, or at an unknown cell: no one shoots them this stop.</summary>
    internal static HashSet<PawnId> NearExploders(CombatView view)
    {
        var output = new HashSet<PawnId>();
        var exploders = LiveExploders(view);
        if (exploders.Count == 0)
        {
            return output;
        }

        var colonists = new HashSet<PawnId>(view.Defenders.Items.Select(d => d.Id));
        var down = DownPawns(view);
        foreach (var (id, h) in exploders)
        {
            if (!h.Cell.TryGetValue(out var at))
            {
                output.Add(id);
                continue;
            }

            foreach (var p in view.Pawns.Items)
            {
                var known = p.Cell.TryGetValue(out var c);
                if (colonists.Contains(p.Id) && !down.Contains(p.Id) && known
                    && Math.Sqrt(((double)(c.X - at.X) * (c.X - at.X)) + ((double)(c.Z - at.Z) * (c.Z - at.Z))) <= ExploderRadius)
                {
                    output.Add(id);
                }
            }
        }

        return output;
    }

    /// <summary>
    /// The manhunter tactic's roles. The hold refuses an animal and squad defense mirrors its weapon (none, so
    /// it would send riflemen into melee), so the pack gets its own: every armed gunner shoots, from the
    /// layout's firing cells (and the game's covered cells) when there is a layout; brawlers take blocking
    /// duties when the choke is blocked, and otherwise engage in melee. No melee role holds an exploder as its
    /// target.
    /// </summary>
    internal static List<CombatRole> ManhunterFormation(CombatView view, GeometryReply geometry, IReadOnlyList<PawnId> relieved)
    {
        var gunners = new List<SquadDefenderFacts>();
        var rest = new List<SquadDefenderFacts>();
        foreach (var d in view.Defenders.Items)
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

        var exploders = LiveExploders(view);
        PawnId top = default;
        PawnId melee = default;
        foreach (var h in RankThreats(view))
        {
            if (top.IsEmpty)
            {
                top = h.Id;
            }

            if (!exploders.ContainsKey(h.Id) && melee.IsEmpty)
            {
                melee = h.Id;
            }
        }

        var blocking = BlockingChoke(view) is not null;
        var cells = new List<Cell>();
        if (view.Layout.TryGetValue(out var layout))
        {
            cells = [.. layout.Firing.Items];
            IEnumerable<Cell> candidates = geometry.Proposals.Items;
            if (blocking)
            {
                candidates = CoveredAround(layout.Firing.Items, geometry);
            }

            foreach (var c in candidates)
            {
                if (!cells.Contains(c))
                {
                    cells.Add(c);
                }
            }

            cells = SpaceCells(RankByCover(cells, geometry.Scored.Items), 1);
        }

        var roles = new List<CombatRole>();
        for (var i = 0; i < gunners.Count; i++)
        {
            var role = new CombatRole { Pawn = gunners[i].Id, Target = top, Ranged = true };
            if (i < cells.Count)
            {
                role.Cell = cells[i];
            }

            roles.Add(role);
        }

        List<CombatRole> duties;
        var chokes = WaveChokes(view);
        if (chokes.Count > 0)
        {
            // A psychic wave blocks every door it approaches.
            duties = WaveBlockers(chokes, Rotated(Brawlers(rest), relieved));
        }
        else
        {
            duties = BrawlerRoles(view, rest, blocking, geometry, relieved);
        }

        roles.AddRange(duties);
        foreach (var b in Brawlers(rest))
        {
            if (!duties.Any(r => r.Pawn == b.Id))
            {
                roles.Add(new CombatRole { Pawn = b.Id, Target = melee });
            }
        }

        return SortRoles(roles);
    }

    /// <summary>The manhunter tactic's re-formation row: a role's target is down. Gunners retarget every stop by focus fire.</summary>
    internal static bool ReformManhunter(CombatView view, CombatMemory m) => SquadTargetDown(view, m);
}
