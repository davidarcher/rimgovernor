namespace RimGovernor.Brain.Policy;

// Go: combat_grenade.go

public static partial class PolicyFunctions
{
    /// <summary>How far (Chebyshev cells) a ground target stays from every colonist: a grenade scatters one tile.</summary>
    internal const int GrenadeScatterClear = 2;

    /// <summary>
    /// The grenade decision: (carrier, hostiles, colonist cells) to the cell to attack_ground, or none.
    /// Candidates are the live hostiles' cells in the carrier's range; each scores the hostiles inside the
    /// blast (EMP: only shielded or mech hostiles), and a cell within the scatter clearance of a colonist is
    /// rejected. Most hits wins, then the nearer cell, then the lower x, z. A carrier whose primary is not an
    /// area weapon, or with no cell, gets none.
    /// </summary>
    public static Cell? GrenadeTarget(CombatPawnState carrier, IReadOnlyList<CombatPawnState> hostiles, IReadOnlyList<Cell> colonists)
    {
        var blast = carrier.WeaponFacts.Blast;
        var known = carrier.Cell.TryGetValue(out var from);
        if (blast <= 0 || !known)
        {
            return null;
        }

        var reach = GrenadeReach(carrier);
        var emp = carrier.WeaponFacts.EMP;
        var fire = carrier.WeaponFacts.Incendiary;
        var cells = new List<Cell>();
        var worth = new List<Cell>();
        foreach (var h in hostiles)
        {
            if (!h.Cell.TryGetValue(out var c) || h.Dead || h.Downed)
            {
                continue;
            }

            cells.Add(c);

            // Mechs are immune to fire: an incendiary never counts one.
            if ((!emp || EmpWorth(h)) && !(fire && IsMech(h)))
            {
                worth.Add(c);
            }
        }

        return BestGround(from, reach, blast, cells, worth, colonists);
    }

    /// <summary>The carrier's range: its weapon range, else the def's.</summary>
    private static double GrenadeReach(CombatPawnState carrier) => carrier.WeaponRange > 0 ? carrier.WeaponRange : carrier.WeaponFacts.Range;

    /// <summary>The candidate cell in reach, clear of colonists, whose blast covers the most worth cells: then the nearer, then the lower x, z.</summary>
    private static Cell? BestGround(Cell from, double reach, double blast, IReadOnlyList<Cell> cells, IReadOnlyList<Cell> worth, IReadOnlyList<Cell> colonists)
    {
        Cell best = default;
        var bestHits = 0;
        var bestDist = double.MaxValue;
        foreach (var c in cells)
        {
            var d = Dist(from, c);
            if (d > reach || NearColonist(c, colonists))
            {
                continue;
            }

            var hits = 0;
            foreach (var w in worth)
            {
                if (Dist(c, w) <= blast)
                {
                    hits++;
                }
            }

            if (hits == 0)
            {
                continue;
            }

            if (hits > bestHits || (hits == bestHits && (d < bestDist || (d == bestDist && (c.X < best.X || (c.X == best.X && c.Z < best.Z))))))
            {
                best = c;
                bestHits = hits;
                bestDist = d;
            }
        }

        return bestHits > 0 ? best : null;
    }

    /// <summary>A hostile an EMP disables: a worn shield or a mechanoid.</summary>
    private static bool EmpWorth(CombatPawnState h) => h.Shield.IsKnown || IsMech(h);

    /// <summary>A mechanoid pawn.</summary>
    private static bool IsMech(CombatPawnState h) => h.Mech;

    private static bool NearColonist(Cell c, IReadOnlyList<Cell> colonists) =>
        colonists.Any(p => Math.Max(Math.Abs(c.X - p.X), Math.Abs(c.Z - p.Z)) <= GrenadeScatterClear);

    /// <summary>
    /// Aims each hold or squad role whose pawn holds an area primary: Ground is the cell GrenadeTarget picks,
    /// null when it picks none, and the pawn keeps its cell and target for then. It runs after RocketClumps,
    /// which clears Ground and shares its order.
    /// </summary>
    private static void Grenade(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Hold && m.Tactic != CombatTactic.Squad)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var hostiles = RankThreats(view);
        var colonists = new List<Cell>();
        foreach (var d in view.Defenders.Items)
        {
            if (state.TryGetValue(d.Id, out var s) && !s.Dead && s.Cell.TryGetValue(out var c))
            {
                colonists.Add(c);
            }
        }

        foreach (var r in m.Roles)
        {
            if (r.Mortar is not null || m.Rescue?.Carrying(r.Pawn) == true)
            {
                continue;
            }

            var carrier = state.GetValueOrDefault(r.Pawn) ?? new CombatPawnState();
            if (r.Cell is { } held)
            {
                // Aim from the held cell: the throw waits until the pawn is there.
                if (!carrier.Cell.TryGetValue(out var at) || at != held)
                {
                    continue;
                }
            }

            if (GrenadeAim(view, m, carrier, hostiles, colonists) is { } c2)
            {
                r.Ground = c2;
            }
        }
    }
}
