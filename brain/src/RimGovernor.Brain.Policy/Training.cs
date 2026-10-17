namespace RimGovernor.Brain.Policy;

// Go: training.go.

/// <summary>Assigned combat requirements threatened by bounded native skill decay.</summary>
public sealed record TrainingReview
{
    /// <summary>The adult colonists who can train a skill.</summary>
    public int Capable { get; init; }

    /// <summary>Assigned holders with a projected requirement shortfall.</summary>
    public int Below { get; init; }

    /// <summary>Holders with supported Shooting maintenance alternatives.</summary>
    public int ShootingBelow { get; init; }

    /// <summary>Holders with supported Melee maintenance alternatives.</summary>
    public int MeleeBelow { get; init; }
}

public static partial class TrainingRules
{
    /// <summary>
    /// The military/training predicate's review (#3259): the assigned combat-capability holders forecast below their
    /// requirement, with supported Shooting or Melee practice. Facilities follow actual requirements only; unlocked
    /// ceilings constrain feasibility.
    /// </summary>
    public static Fact<TrainingReview> ReviewTraining(RoundsFacts facts)
    {
        if (!facts.ForecastObserved.WorkPawns.TryGetValue(out var pawns)) return Unknown<TrainingReview>();
        if (!facts.Ideology.TryGetValue(out _)) return Unknown<TrainingReview>();
        var requirements = AssignedCombatCapabilities(facts.Ideology);
        var active = new List<(string Pawn, string Skill)>(); var unknown = false;
        foreach (var requirement in requirements)
        {
            var pawn = pawns.FirstOrDefault(p => p.Id == requirement.Pawn);
            if (pawn is null || !pawn.Skills.TryGetValue(out var skills)) { unknown = true; continue; }
            var consequence = ForecastMaintenancePolicy.ForecastMaintenance([new(requirement, EquatableArray.From(skills))], [],
                new([], 0, 0), new Dictionary<string, Fact<bool>>(), GameTime.TicksPerDay)[0];
            if (!consequence.MissingXp.TryGetValue(out var missing)) { unknown = true; continue; }
            if (missing <= 0) continue;
            foreach (var alternative in requirement.Alternatives.Where(a => a.Skill is "Shooting" or "Melee"))
                if (skills.FirstOrDefault(s => s.Name == alternative.Skill)?.Learning?.PracticeEligible == Known(true))
                    active.Add((requirement.Pawn.Value, alternative.Skill));
        }
        if (active.Count == 0 && unknown) return Unknown<TrainingReview>();
        return Known(new TrainingReview { Capable = requirements.Select(r => r.Pawn).Distinct().Count(),
            Below = active.Select(a => a.Pawn).Distinct().Count(), ShootingBelow = active.Where(a => a.Skill == "Shooting").Select(a => a.Pawn).Distinct().Count(),
            MeleeBelow = active.Where(a => a.Skill == "Melee").Select(a => a.Pawn).Distinct().Count() });
    }

    /// <summary>
    /// The count the plan's training range (stands) or ring (markers) should hold: the count while the gap is
    /// open, else 0. A standing one stays and only grows; the plan never retires it.
    /// </summary>
    public static int RangesWanted(Fact<int> stands) => stands.TryGetValue(out var n) ? n : 0;
}
