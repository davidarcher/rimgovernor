namespace RimGovernor.Brain.Policy;

//
// CombatTactic.Hunt is a roster hunt: the view's threats are wild prey the threat census does not call hostile,
// and the selected roster stages within the distance the hunt estimate priced, follows the prey when it walks off, and
// focus-fires them down. Once the prey are all manhunter the fight is the manhunter tactic's.

public static partial class CombatHuntRules
{
    /// <summary>The staging reach when no gunner's weapon range is known, in cells.</summary>
    internal const int HuntDefaultRange = 20;

    /// <summary>
    /// Stages the selected roster's armed ranged defenders where each can hit the prey: a gunner already within his
    /// stage distance (<see cref="StageDistance"/>) of the prey's centre stays where he stands, and one beyond it walks
    /// straight in to it, so the formation is the one the hunt estimate priced. A gunner whose stage cell native refused
    /// as unreachable (<paramref name="unreachable"/>) is left out. Targets are focus fire's. Melee defenders take no part.
    /// </summary>
    internal static List<CombatRole> HuntFormation(CombatView view, IReadOnlyCollection<Cell> unreachable)
    {
        var prey = CombatThreatRules.RankThreats(view);
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        CombatPawnState S(PawnId id) => state.GetValueOrDefault(id) ?? new CombatPawnState();

        // A gunner whose weapon is outranged by any prey stays out of the squad.
        double preyRange = 0;
        foreach (var p in prey)
        {
            preyRange = Math.Max(preyRange, p.WeaponRange);
        }

        var roster = new HashSet<PawnId>(view.HuntHunters.Items);
        var gunners = new List<SquadDefenderFacts>();
        foreach (var d in view.Defenders.Items)
        {
            var r = S(d.Id).WeaponRange;
            if (r > 0 && r < preyRange)
            {
                continue;
            }

            if (roster.Contains(d.Id) && SquadDefenseRules.SquadDefenderEligible(d) && S(d.Id).WeaponFacts.Hunts())
            {
                gunners.Add(d);
            }
        }

        if (gunners.Count == 0 || prey.Count == 0)
        {
            return [];
        }

        var top = prey[0].Id;
        int sumX = 0, sumZ = 0, known = 0;
        foreach (var p in prey)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                sumX += c.X;
                sumZ += c.Z;
                known++;
            }
        }

        var roles = new List<CombatRole>(gunners.Count);
        if (known == 0)
        {
            foreach (var g in gunners)
            {
                roles.Add(new CombatRole { Pawn = g.Id, Target = top, Ranged = true });
            }

            return CombatDecideRules.SortRoles(roles);
        }

        var centre = new Cell(sumX / known, sumZ / known);
        var spread = PreySpread(prey, centre);
        foreach (var g in gunners)
        {
            if (!S(g.Id).Cell.TryGetValue(out var at))
            {
                roles.Add(new CombatRole { Pawn = g.Id, Target = top, Ranged = true });
                continue;
            }

            var cell = StageCell(at, centre, StageDistance(S(g.Id).WeaponRange, spread));
            if (unreachable.Contains(cell))
            {
                continue;
            }

            roles.Add(new CombatRole { Pawn = g.Id, Cell = cell, Target = top, Ranged = true });
        }

        return CombatDecideRules.SortRoles(roles);
    }

    /// <summary>
    /// The farthest a gunner of weapon range <paramref name="range"/> stages from the prey's centre: the hunt estimate's
    /// engage distance (<see cref="HuntEstimator.EngageRangeFactor"/> of the range, the hunt job's cast position), less
    /// the group's <paramref name="spread"/> so every prey stays within it, and less half a diagonal because rounding the
    /// stage point to a cell moves it up to that far outward. At least one cell.
    /// </summary>
    internal static double StageDistance(double range, double spread)
    {
        var reach = range > 0 ? range : HuntDefaultRange;
        return Math.Max(1, Math.Floor((HuntEstimator.EngageRangeFactor * reach) - spread - Math.Sqrt(0.5)));
    }

    /// <summary>
    /// Where a gunner standing at <paramref name="at"/> stages against prey centred on <paramref name="centre"/>: his own
    /// cell while it is within <paramref name="distance"/>, else the cell that far from the centre on the line to him.
    /// </summary>
    internal static Cell StageCell(Cell at, Cell centre, double distance)
    {
        var d = Distance(at, centre);
        if (d <= distance)
        {
            return at;
        }

        return new Cell(
            centre.X + (int)Math.Round(distance * (at.X - centre.X) / d, MidpointRounding.AwayFromZero),
            centre.Z + (int)Math.Round(distance * (at.Z - centre.Z) / d, MidpointRounding.AwayFromZero)
        );
    }

    /// <summary>The farthest any placed prey stands from the group's centre.</summary>
    private static double PreySpread(IReadOnlyList<CombatPawnState> prey, Cell centre)
    {
        var spread = 0.0;
        foreach (var p in prey)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                spread = Math.Max(spread, Distance(c, centre));
            }
        }

        return spread;
    }

    private static double Distance(Cell a, Cell b)
    {
        double dx = a.X - b.X;
        double dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Why each rostered hunter is left out of the formation, or why there is no prey: what an empty squad reports.</summary>
    public static IReadOnlyList<string> HuntFormationRefusals(CombatView view)
    {
        var output = new List<string>();
        var prey = CombatThreatRules.RankThreats(view);
        if (prey.Count == 0)
        {
            output.Add("no_prey");
        }

        double preyRange = 0;
        foreach (var p in prey)
        {
            preyRange = Math.Max(preyRange, p.WeaponRange);
        }

        var states = view.Pawns.Items.ToDictionary(p => p.Id);
        var defenders = view.Defenders.Items.ToDictionary(d => d.Id);
        foreach (var id in view.HuntHunters.Items)
        {
            if (!defenders.TryGetValue(id, out var d))
            {
                output.Add(id + ":not_a_defender");
                continue;
            }

            var reasons = SquadDefenseRules.SquadDefenderReasons(d);
            var state = states.GetValueOrDefault(id) ?? new CombatPawnState();
            if (state.WeaponRange > 0 && state.WeaponRange < preyRange)
            {
                reasons.Add("outranged");
            }

            if (!state.WeaponFacts.Hunts())
            {
                reasons.Add("weapon_does_not_hunt(" + state.Weapon + (state.WeaponFacts.Ranged ? ",ranged" : ",not_ranged") + (state.WeaponFacts.Explosive ? ",explosive" : "") + (state.WeaponFacts.Incendiary ? ",incendiary" : "") + ")");
            }

            output.Add(id + ":" + (reasons.Count == 0 ? "eligible" : string.Join("|", reasons)));
        }

        if (view.HuntHunters.Count == 0)
        {
            output.Add("no_roster");
        }

        return output;
    }

    /// <summary>
    /// The hunt's re-formation row: a role's target is down, or the prey turned into a manhunter pack, or some
    /// prey turned on the squad and the hunt has no potshot door yet, or the prey walked off a role (<see cref="PreyOutOfReach"/>).
    /// </summary>
    internal static bool ReformHunt(CombatView view, CombatMemory m) =>
        CombatDecideRules.SquadTargetDown(view, m) || CombatManhunterRules.ManhunterPack(view) || (m.PotshotDoor is null && CombatDoorRules.ProvokedPrey(view)) || PreyOutOfReach(view, m);

    /// <summary>
    /// The hunt fight's roster pawns its formation gives no role, sorted: the review took them off the hunt (a released
    /// or re-compared roster), their stage cell is unreachable, or they can no longer hunt. The fight lets them go, so
    /// the undraft sweep undrafts them instead of leaving them standing drafted; a roster left with none ends the fight.
    /// </summary>
    public static List<PawnId> IdleHunters(IEnumerable<PawnId> roster, IEnumerable<CombatRole> roles)
    {
        var held = roles.Select(r => r.Pawn).ToHashSet();
        return [.. roster.Where(p => !held.Contains(p)).OrderBy(p => p.Value, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether a ranged role's target is no longer among the prey, or stands beyond the gunner's weapon range from the
    /// role's stage cell (his own cell when the role names none): the prey moved, and the formation follows it.
    /// </summary>
    internal static bool PreyOutOfReach(CombatView view, CombatMemory m)
    {
        var cells = new Dictionary<PawnId, Fact<Cell>>();
        var ranges = new Dictionary<PawnId, double>();
        foreach (var p in view.Pawns.Items)
        {
            cells[p.Id] = p.Cell;
            ranges[p.Id] = p.WeaponRange;
        }

        var prey = CombatThreatRules.RankThreats(view).Select(p => p.Id).ToHashSet();
        foreach (var r in m.Roles)
        {
            if (!r.Ranged || r.Target == default)
            {
                continue;
            }

            if (!prey.Contains(r.Target))
            {
                return true;
            }

            var from = r.Cell is { } c ? Known(c) : cells.GetValueOrDefault(r.Pawn);
            var range = ranges.GetValueOrDefault(r.Pawn);
            if (range > 0 && from.TryGetValue(out var at) && cells.GetValueOrDefault(r.Target).TryGetValue(out var target) && Distance(at, target) > range)
            {
                return true;
            }
        }

        return false;
    }
}
