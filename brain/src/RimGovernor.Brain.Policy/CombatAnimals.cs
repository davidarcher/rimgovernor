namespace RimGovernor.Brain.Policy;

// Go: combat_animals.go
//
// Animal tactics order the colony's animals with the animal orders, which need no draft claim. One use per
// stop, first match wins:
//  - waiting a manhunter pack out: every animal is zoned to its own cell, one standing on a door to the
//    nearest floor cell of that door's room, so no animal opens a door;
//  - an explosives raid: each animal is zoned beside an explosive hostile, on our side, so its shots and
//    blasts hit its own raiders;
//  - a manhunter pack without exploders, or pikemen, with a known choke: every animal is zoned onto the choke
//    cell as a body blocker;
//  - melee raiders: every animal not known untrained is released at the nearest one.
//
// An animal the fight zoned and no longer wants zoned is cleared, which restores its prior restriction; the
// fight's close clears the rest (AnimalClears).

/// <summary>The last release or zone an animal was given.</summary>
public sealed record AnimalOrder
{
    public PawnId Pawn { get; init; }

    public CombatOrderKind Kind { get; init; }

    public Cell Cell { get; init; }

    public PawnId Target { get; init; }

    internal CombatOrder Order() => new() { Pawn = Pawn, Kind = Kind, Cell = Cell, Target = Target, Reason = CombatOrderReason.Animal };
}

public sealed partial record CombatMemory
{
    /// <summary>
    /// RefuseAnimal is Forget for a refused animal order: an animal refused a release as untrained is never
    /// released again this fight; any other refusal is given again on the next stop.
    /// </summary>
    public CombatMemory RefuseAnimal(CombatOrder order, string refusal)
    {
        var m = Forget(order.Pawn);
        m.Animals.RemoveAll(a => a.Pawn == order.Pawn);
        if (order.Kind == CombatOrderKind.Release && refusal == RefusalUntrained && !m.Untrained.Contains(order.Pawn))
        {
            m.Untrained.Add(order.Pawn);
            m.Untrained.Sort();
        }

        return m;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Bridge.CombatRefusalUntrained.</summary>
    public const string RefusalUntrained = "untrained";

    /// <summary>Enrage ranges, squared: a wild animal within 10 cells of a live raider ...</summary>
    private const int EnrageNear = 10 * 10;

    /// <summary>... and beyond 20 of every live colonist is shot.</summary>
    private const int EnrageFar = 20 * 20;

    /// <summary>The live colony animals with a known cell, by id.</summary>
    private static List<CombatPawnState> ColonyAnimals(CombatView view) =>
        [.. view.Pawns.Items.Where(p => p.Cell.IsKnown && p.Animal && !p.Dead && !p.Downed)];

    /// <summary>What each colony animal should be doing this stop.</summary>
    private static Dictionary<PawnId, AnimalOrder> AnimalWants(CombatView view, CombatMemory m)
    {
        var animals = ColonyAnimals(view);
        var want = new Dictionary<PawnId, AnimalOrder>();

        void Zone(CombatPawnState p, Cell c) => want[p.Id] = new AnimalOrder { Pawn = p.Id, Kind = CombatOrderKind.AnimalArea, Cell = c };

        if (animals.Count == 0)
        {
        }
        else if (m.Wait && m.Tactic == CombatTactic.Manhunter)
        {
            foreach (var p in animals)
            {
                p.Cell.TryGetValue(out var at);
                Zone(p, OffDoor(view, at));
            }
        }
        else if (ExplosiveHostiles(view).Count > 0)
        {
            var cells = DecoyCells(view, ExplosiveHostiles(view));
            if (cells.Count != 0)
            {
                for (var i = 0; i < animals.Count; i++)
                {
                    Zone(animals[i], cells[i % cells.Count]);
                }
            }
        }
        else if (AnimalChoke(view) is { } choke)
        {
            foreach (var p in animals)
            {
                Zone(p, choke);
            }
        }
        else
        {
            var (ids, cells) = MeleeRaiders(view);
            if (ids.Count != 0)
            {
                foreach (var p in animals)
                {
                    if (m.Untrained.Contains(p.Id))
                    {
                        continue;
                    }

                    p.Cell.TryGetValue(out var at);
                    want[p.Id] = new AnimalOrder { Pawn = p.Id, Kind = CombatOrderKind.Release, Target = ids[NearestIndex(at, cells)] };
                }
            }
        }

        return want;
    }

    /// <summary>
    /// Turns the wants into changed orders and records them in m. An animal switching from a zone to a
    /// release is cleared first and released on a later stop, so a batch names an animal once.
    /// </summary>
    private static List<CombatOrder> AnimalStep(CombatView view, CombatMemory m)
    {
        var want = AnimalWants(view, m);
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var output = new List<CombatOrder>();
        var kept = new List<AnimalOrder>();
        foreach (var had in m.Animals)
        {
            var present = state.TryGetValue(had.Pawn, out var s);
            var wanted = want.TryGetValue(had.Pawn, out var w);
            if (!present || s!.Dead)
            {
                // Gone or dead: nothing to restore.
            }
            else if (had.Kind == CombatOrderKind.AnimalArea && (!wanted || w!.Kind != CombatOrderKind.AnimalArea))
            {
                output.Add(new CombatOrder { Pawn = had.Pawn, Kind = CombatOrderKind.AnimalClear, Reason = CombatOrderReason.Animal });
                want.Remove(had.Pawn);
            }
            else if (wanted && w == had && (w!.Kind == CombatOrderKind.AnimalArea || s.Target == w.Target || s.Stance != CombatStance.Idle))
            {
                // Already doing it.
                kept.Add(had);
                want.Remove(had.Pawn);
            }

            // Otherwise rezoned below; the native area moves to the new cell.
        }

        foreach (var id in want.Keys.OrderBy(k => k))
        {
            output.Add(want[id].Order());
            kept.Add(want[id]);
        }

        m.Animals = PolicySort.ByLess(kept, (a, b) => string.CompareOrdinal(a.Pawn.Value, b.Pawn.Value) < 0);
        return output;
    }

    /// <summary>The clears for every animal the fight still has zoned, sent when the fight closes.</summary>
    public static List<CombatOrder> AnimalClears(CombatMemory m) =>
        [.. m.Animals.Where(a => a.Kind == CombatOrderKind.AnimalArea).Select(a => new CombatOrder { Pawn = a.Pawn, Kind = CombatOrderKind.AnimalClear, Reason = CombatOrderReason.Animal })];

    /// <summary>Reports an animal order.</summary>
    public static bool AnimalOrderKind(CombatOrderKind k) =>
        k == CombatOrderKind.Release || k == CombatOrderKind.AnimalArea || k == CombatOrderKind.AnimalClear;

    /// <summary>The cell at, or when at is a room's door the nearest floor cell of that room.</summary>
    private static Cell OffDoor(CombatView view, Cell at)
    {
        foreach (var r in view.Rooms.Items)
        {
            if (!r.Doors.Items.Contains(at))
            {
                continue;
            }

            var inside = r.Interior;
            var best = at;
            var bestD = -1L;
            for (var x = inside.X; x < inside.X + inside.Width; x++)
            {
                for (var z = inside.Z; z < inside.Z + inside.Height; z++)
                {
                    var c = new Cell(x, z);
                    var d = Distance2(at, c);
                    if (!r.Doors.Items.Contains(c) && (bestD < 0 || d < bestD))
                    {
                        best = c;
                        bestD = d;
                    }
                }
            }

            return best;
        }

        return at;
    }

    /// <summary>The cells one step from each explosive hostile toward the nearest live colonist, in threat order.</summary>
    private static List<Cell> DecoyCells(CombatView view, IReadOnlyList<CombatPawnState> explosive)
    {
        var ours = new List<Cell>();
        foreach (var d in view.Defenders.Items)
        {
            if (Positive(d.Dead) || Positive(d.Downed))
            {
                continue;
            }

            foreach (var p in view.Pawns.Items)
            {
                if (p.Cell.TryGetValue(out var c) && p.Id == d.Id && !p.Dead && !p.Downed)
                {
                    ours.Add(c);
                }
            }
        }

        var output = new List<Cell>();
        foreach (var h in explosive)
        {
            if (!h.Cell.TryGetValue(out var at) || ours.Count == 0)
            {
                continue;
            }

            var to = ours[NearestIndex(at, ours)];
            var c = new Cell(at.X + Sign(to.X - at.X), at.Z + Sign(to.Z - at.Z));
            if (c != at && c.X >= 0 && c.Z >= 0 && !output.Contains(c))
            {
                output.Add(c);
            }
        }

        return output;
    }

    /// <summary>
    /// The layout's choke when animals body-block it: a manhunter pack with no exploder, or a fight whose
    /// live hostiles are all pikemen.
    /// </summary>
    private static Cell? AnimalChoke(CombatView view)
    {
        if (!view.Layout.TryGetValue(out var layout) || !layout.Choke.TryGetValue(out var choke))
        {
            return null;
        }

        if (ManhunterPack(view) && LiveExploders(view).Count == 0)
        {
            return choke;
        }

        var threats = RankThreats(view);
        if (threats.Count == 0)
        {
            return null;
        }

        foreach (var h in threats)
        {
            if (!h.Kind.StartsWith("Mech_Pikeman", StringComparison.Ordinal))
            {
                return null;
            }
        }

        return choke;
    }

    /// <summary>The live humanlike hostiles known to carry no ranged weapon, with a known position, by id, and their cells.</summary>
    private static (List<PawnId> Ids, List<Cell> Cells) MeleeRaiders(CombatView view)
    {
        var melee = new HashSet<PawnId>();
        foreach (var t in view.Threats.Items)
        {
            var known = t.RangedEquipped.TryGetValue(out var ranged);
            if (!t.Building && Positive(t.Humanlike) && known && !ranged && !Positive(t.Dead) && !Positive(t.Downed))
            {
                melee.Add(ThreatPawn(t));
            }
        }

        var down = DownPawns(view);
        var ids = new List<PawnId>();
        var cells = new List<Cell>();
        foreach (var t in view.Positional.Items)
        {
            if (t.Position.TryGetValue(out var c) && melee.Contains(t.Id) && !down.Contains(t.Id))
            {
                ids.Add(t.Id);
                cells.Add(c);
            }
        }

        return (ids, cells);
    }

    /// <summary>
    /// The first wild animal by id that is unhurt, near a live humanlike raider and away from every live
    /// colonist; the mirror sends only wild predators and large animals near hostiles.
    /// </summary>
    private static CombatPawnState? EnrageTarget(CombatView view)
    {
        var humanlike = new HashSet<PawnId>();
        foreach (var t in view.Threats.Items)
        {
            if (!t.Building && Positive(t.Humanlike) && !Positive(t.Dead) && !Positive(t.Downed))
            {
                humanlike.Add(ThreatPawn(t));
            }
        }

        var down = DownPawns(view);
        var raiders = new List<Cell>();
        var ours = new List<Cell>();
        foreach (var t in view.Positional.Items)
        {
            if (humanlike.Contains(t.Id) && !down.Contains(t.Id) && t.Position.TryGetValue(out var c))
            {
                raiders.Add(c);
            }
        }

        if (raiders.Count == 0)
        {
            return null;
        }

        foreach (var d in view.Defenders.Items)
        {
            foreach (var p in view.Pawns.Items)
            {
                if (p.Cell.TryGetValue(out var c) && p.Id == d.Id && !p.Dead && !p.Downed)
                {
                    ours.Add(c);
                }
            }
        }

        CombatPawnState? best = null;
        foreach (var p in view.Pawns.Items)
        {
            var atKnown = p.Cell.TryGetValue(out var at);
            var healthKnown = p.Health.TryGetValue(out var h);
            if (!atKnown || !p.Wild || p.Dead || p.Downed || (healthKnown && h < 1))
            {
                continue;
            }

            if (Distance2(at, raiders[NearestIndex(at, raiders)]) > EnrageNear)
            {
                continue;
            }

            if (ours.Count > 0 && Distance2(at, ours[NearestIndex(at, ours)]) <= EnrageFar)
            {
                continue;
            }

            if (best is null || p.Id < best.Id)
            {
                best = p;
            }
        }

        return best;
    }

    /// <summary>Has the nearest live ranged role shoot the enrage target with the attack order, in place of that role's own order.</summary>
    private static List<CombatOrder> EnrageWild(
        CombatView view,
        List<CombatOrder> orders,
        IReadOnlyList<CombatRole> roles,
        HashSet<PawnId> orderable,
        Dictionary<PawnId, CombatPawnState> state
    )
    {
        if (EnrageTarget(view) is not { } wild)
        {
            return orders;
        }

        wild.Cell.TryGetValue(out var at);
        PawnId gunner = default;
        var bestD = -1L;
        foreach (var r in roles)
        {
            var s = state.GetValueOrDefault(r.Pawn) ?? new CombatPawnState();
            var known = s.Cell.TryGetValue(out var c);
            if (!r.Ranged || !orderable.Contains(r.Pawn) || !known || s.Dead || s.Downed)
            {
                continue;
            }

            var d = Distance2(at, c);
            if (bestD < 0 || d < bestD)
            {
                gunner = r.Pawn;
                bestD = d;
            }
        }

        if (gunner == default || (state.GetValueOrDefault(gunner)?.Target ?? default) == wild.Id)
        {
            return orders;
        }

        var output = orders.Where(o => o.Pawn != gunner).ToList();
        output.Add(new CombatOrder { Pawn = gunner, Kind = CombatOrderKind.Attack, Target = wild.Id, Reason = CombatOrderReason.Enrage });
        return output;
    }
}
