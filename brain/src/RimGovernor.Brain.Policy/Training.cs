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

public static partial class PolicyFunctions
{
    /// <summary>Facilities for justified assigned capability maintenance; unlocked ceilings constrain feasibility only.</summary>
    public static readonly ConcernId MaintainTraining = new("MaintainTraining");

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
            var consequence = ForecastMaintenance([new(requirement, EquatableArray.From(skills))], [],
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
    /// <summary>Raises MaintainTraining for assigned holders with a forecast shortfall; deficit is their share of assigned holders.</summary>
    internal static void InspectTraining(RoundsRun c)
    {
        var review = ReviewTraining(c.F);
        c.Assess(MaintainTraining, Measured(review, r => r.Below == 0));
        if (review.TryGetValue(out var r) && r.Below > 0)
        {
            c.Raise(MaintainTraining).Deficit = Known((double)r.Below / r.Capable);
        }
    }

    /// <summary>
    /// The count the plan's training range (stands) or ring (markers) should hold: the count while the gap is
    /// open, else 0. A standing one stays and only grows; the plan never retires it.
    /// </summary>
    public static int RangesWanted(Fact<int> stands) => stands.TryGetValue(out var n) ? n : 0;
}
