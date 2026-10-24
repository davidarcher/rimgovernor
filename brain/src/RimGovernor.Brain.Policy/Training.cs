using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;


/// <summary>Who trains (#3259, #3813, #3846): every pawn capable of Shooting practice below the range ceiling, and every pawn
/// capable of Melee practice below the sparring ceiling.</summary>
public sealed record TrainingReview
{
    /// <summary>The colonists the review read.</summary>
    public int Capable { get; init; }

    /// <summary>The pawns who would train: the Shooting trainees, and the Melee trainees when a bout has a partner.</summary>
    public int Below { get; init; }

    /// <summary>The pawns the one practice eligibility rule admits for Shooting (SkillPractice.Drill.Capable, #3846) below the
    /// range ceiling: the training range's stands.</summary>
    public int ShootingTrainees { get; init; }

    /// <summary>The pawns the one practice eligibility rule admits for Melee (SkillPractice.Drill.Capable, #3813) below the
    /// sparring ceiling. Under two, nobody spars: a bout needs a partner.</summary>
    public int MeleeTrainees { get; init; }
}

public static partial class TrainingRules
{
    /// <summary>
    /// The military/training predicate's review (#3259, #3813, #3846): every pawn the practice eligibility rule admits for
    /// Shooting or Melee whose level is under that skill's practice ceiling (colony defense is every capable pawn's). One
    /// Shooting trainee wants a training range; two or more Melee trainees want a sparring ring, one alone having nobody to
    /// train with. Unlocked ceilings constrain feasibility. Raises <see cref="RequiredFactException"/> while the work pawns are unread.
    /// </summary>
    public static TrainingReview ReviewTraining(RoundsFacts facts)
    {
        if (!facts.ForecastObserved.WorkPawns.TryGetValue(out var pawns)) throw new RequiredFactException("work pawns");
        var shooting = new HashSet<string>(StringComparer.Ordinal); var melee = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pawn in pawns)
        {
            var skills = pawn.Skills;
            foreach (var (name, trainees) in new[] { ("Shooting", shooting), ("Melee", melee) })
            {
                if (skills.FirstOrDefault(s => s.Name == name) is not { Learning.Practice: SkillPractice.Drill { Capable: true, Ceiling: var ceiling } } skill) continue;
                if (skill.Level < ceiling) trainees.Add(pawn.Id.Value);
            }
        }
        var below = shooting.Union(melee.Count >= 2 ? melee : []).Count();
        return new TrainingReview { Capable = pawns.Count, Below = below, ShootingTrainees = shooting.Count, MeleeTrainees = melee.Count };
    }
}
