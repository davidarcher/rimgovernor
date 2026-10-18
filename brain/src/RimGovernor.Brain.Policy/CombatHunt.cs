namespace RimGovernor.Brain.Policy;

//
// CombatTactic.Hunt is a roster hunt: the view's threats are wild prey the threat census does not call hostile,
// and the selected roster stages in a half circle at weapon range and focus-fires them down. Once the prey are all
// manhunter the fight is the manhunter tactic's.

public static partial class CombatHuntRules
{
    /// <summary>The staging reach when no gunner's weapon range is known, in cells.</summary>
    internal const int HuntDefaultRange = 20;

    /// <summary>
    /// Puts the selected roster's armed ranged defenders on a half circle around the prey's centre, at the shortest gunner's
    /// weapon range so each can reach the group, on the squad's side of it so no one shoots across a friend.
    /// Targets are focus fire's. Melee defenders take no part.
    /// </summary>
    internal static List<CombatRole> HuntFormation(CombatView view)
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
        var reach = double.MaxValue;
        foreach (var g in gunners)
        {
            var r = S(g.Id).WeaponRange;
            if (r > 0)
            {
                reach = Math.Min(reach, r);
            }
        }

        if (reach == double.MaxValue)
        {
            reach = HuntDefaultRange;
        }

        // Every prey stays within reach of every cell: the radius gives up the group's own spread.
        var spread = 0.0;
        foreach (var p in prey)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                var dx = (double)(c.X - centre.X);
                var dz = (double)(c.Z - centre.Z);
                spread = Math.Max(spread, Math.Sqrt((dx * dx) + (dz * dz)));
            }
        }

        // Rounding the arc point to a cell moves it up to half a diagonal outward, and the game's range check is the
        // cells' Euclidean distance, so the radius gives that up too.
        var radius = Math.Max(1, Math.Floor(reach - spread - Math.Sqrt(0.5)));

        // The axis points from the prey to the squad's side.
        double sx = 0, sz = 0;
        foreach (var g in gunners)
        {
            if (S(g.Id).Cell.TryGetValue(out var c))
            {
                sx += c.X - centre.X;
                sz += c.Z - centre.Z;
            }
        }

        var axis = Math.Atan2(sz, sx);
        if (sx == 0 && sz == 0)
        {
            axis = Math.PI / 2;
        }

        // Gunners sorted by where they stand around the axis take the arc's cells in order, so their walks do not cross.
        double Rel(PawnId id)
        {
            if (!S(id).Cell.TryGetValue(out var c))
            {
                return 0;
            }

            var a = Math.Atan2(c.Z - centre.Z, c.X - centre.X) - axis;
            return Math.Atan2(Math.Sin(a), Math.Cos(a));
        }

        gunners = PolicySort.ByLess(gunners, (a, b) => Rel(a.Id) < Rel(b.Id));
        for (var i = 0; i < gunners.Count; i++)
        {
            var offset = 0.0;
            if (gunners.Count > 1)
            {
                offset = (-Math.PI / 2) + (Math.PI * i / (gunners.Count - 1));
            }

            var a = axis + offset;
            var cell = new Cell(
                centre.X + (int)Math.Round(radius * Math.Cos(a), MidpointRounding.AwayFromZero),
                centre.Z + (int)Math.Round(radius * Math.Sin(a), MidpointRounding.AwayFromZero)
            );
            roles.Add(new CombatRole { Pawn = gunners[i].Id, Cell = cell, Target = top, Ranged = true });
        }

        return CombatDecideRules.SortRoles(roles);
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
    /// prey turned on the squad and the hunt has no potshot door yet.
    /// </summary>
    internal static bool ReformHunt(CombatView view, CombatMemory m) =>
        CombatDecideRules.SquadTargetDown(view, m) || CombatManhunterRules.ManhunterPack(view) || (m.PotshotDoor is null && CombatDoorRules.ProvokedPrey(view));
}
