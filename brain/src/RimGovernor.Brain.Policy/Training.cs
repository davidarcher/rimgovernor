namespace RimGovernor.Brain.Policy;


/// <summary>Who trains (#3259, #3813): the assigned Shooting holders threatened by bounded native skill decay, and every pawn
/// capable of Melee practice below the sparring ceiling.</summary>
public sealed record TrainingReview
{
    /// <summary>The colonists the review read.</summary>
    public int Capable { get; init; }

    /// <summary>The assigned combat-capability holders: the training range's stands (#3846 prices the range).</summary>
    public int Holders { get; init; }

    /// <summary>The pawns who would train: the Shooting holders below their requirement, and the Melee trainees when a bout has a partner.</summary>
    public int Below { get; init; }

    /// <summary>Holders with supported Shooting maintenance alternatives.</summary>
    public int ShootingBelow { get; init; }

    /// <summary>The pawns the one practice eligibility rule admits for Melee (SkillLearning.PracticeCapable, #3813) below the
    /// sparring ceiling. Under two, nobody spars: a bout needs a partner.</summary>
    public int MeleeTrainees { get; init; }
}

public static partial class TrainingRules
{
    /// <summary>
    /// The military/training predicate's review (#3259, #3813): the assigned combat-capability holders forecast below their
    /// Shooting requirement, and every pawn the practice eligibility rule admits for Melee whose level is under the sparring
    /// ceiling (colony defense is every capable pawn's). Two or more trainees want a sparring ring; one alone has nobody to
    /// train with. Unlocked ceilings constrain feasibility.
    /// </summary>
    public static Fact<TrainingReview> ReviewTraining(RoundsFacts facts)
    {
        if (!facts.ForecastObserved.WorkPawns.TryGetValue(out var pawns)) return Unknown<TrainingReview>();
        if (!facts.Ideology.TryGetValue(out _)) return Unknown<TrainingReview>();
        var requirements = CapabilityMaintenanceRules.AssignedCombatCapabilities(facts.Ideology);
        var shooting = new HashSet<string>(StringComparer.Ordinal); var trainees = new HashSet<string>(StringComparer.Ordinal); var unknown = false;
        foreach (var pawn in pawns)
        {
            if (!pawn.Skills.TryGetValue(out var skills)) { unknown = true; continue; }
            if (skills.FirstOrDefault(s => s.Name == "Melee") is { Learning: { PracticeCapable: true } melee } skill)
            {
                if (!melee.PracticeCeiling.TryGetValue(out var ceiling)) unknown = true;
                else if (skill.Level < ceiling) trainees.Add(pawn.Id.Value);
            }
            foreach (var requirement in requirements.Where(r => r.Pawn == pawn.Id))
            {
                var consequence = ForecastMaintenancePolicy.ForecastMaintenance([new(requirement, EquatableArray.From(skills))], [],
                    new([], 0, 0), ForecastMaintenancePolicy.NoneCompleted, GameTime.TicksPerDay)[0];
                if (!consequence.MissingXp.TryGetValue(out var missing)) { unknown = true; continue; }
                if (missing > 0 && requirement.Alternatives.Any(a => a.Skill == "Shooting")
                    && skills.FirstOrDefault(s => s.Name == "Shooting")?.Learning?.PracticeEligible == Known(true))
                    shooting.Add(pawn.Id.Value);
            }
        }
        unknown |= requirements.Any(r => pawns.All(p => p.Id != r.Pawn));
        var below = shooting.Union(trainees.Count >= 2 ? trainees : []).Count();
        if (below == 0 && unknown) return Unknown<TrainingReview>();
        return Known(new TrainingReview { Capable = pawns.Count, Holders = requirements.Select(r => r.Pawn).Distinct().Count(),
            Below = below, ShootingBelow = shooting.Count, MeleeTrainees = trainees.Count });
    }

    /// <summary>
    /// The count the plan's training range (stands) or ring (markers) should hold: the count while the gap is
    /// open, else 0. A standing one stays and only grows; the plan never retires it.
    /// </summary>
    public static int RangesWanted(Fact<int> stands) => stands.TryGetValue(out var n) ? n : 0;
}
