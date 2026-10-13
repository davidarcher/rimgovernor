namespace RimGovernor.Brain.Policy;

// Go: training.go.

/// <summary>
/// Counts the colony's colonists against the ceiling of each skill's own ladder: the combat skill level at which a
/// colonist stops counting toward the gap. RimWorld skills run 0 to 20. Each pawn is judged on the one skill it
/// trains (TrainingSkill) against that skill's ceiling.
/// </summary>
public sealed record TrainingReview
{
    /// <summary>The adult colonists who can train a skill.</summary>
    public int Capable { get; init; }

    /// <summary>The capable ones whose chosen skill is under its ceiling.</summary>
    public int Below { get; init; }

    /// <summary>The Below ones who train Shooting (the range's ceiling, UnlockedTrainingTier).</summary>
    public int ShootingBelow { get; init; }

    /// <summary>The Below ones who train Melee (the ring's, UnlockedSparringTier).</summary>
    public int MeleeBelow { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The Military department's standing skill gap (#2619): colonists whose chosen combat skill is below its
    /// ladder's ceiling. It is a Standard whose target is no such colonist, and it is not gated by defense
    /// admission: the gap is its own deficit. Open, it asks layout for a training range (RoomDemand.Ranges) while
    /// a shooter is below, and for a sparring ring (RoomDemand.Rings) while two fighters are; a standing range or
    /// ring takes no further action from the Concern, because the native jobs spend the pawn-hours.
    /// </summary>
    public static readonly ConcernId MaintainTraining = new("MaintainTraining");

    /// <summary>Ranks MaintainTraining with the other priority-3 chores.</summary>
    private const int TrainingPriority = 3;

    /// <summary>
    /// The skill a pawn trains, mirroring the native choice: the higher usable level, then the stronger passion,
    /// then Shooting. A skill is unusable when disabled; Shooting also for a Brawler. False when neither is
    /// usable, or when either skill row is unread.
    /// </summary>
    public static (string Skill, bool Ok) TrainingSkill(PawnProfile p)
    {
        var sRead = p.Skills.TryGetValue("Shooting", out var shooting);
        var mRead = p.Skills.TryGetValue("Melee", out var melee);
        if (!sRead || !mRead)
        {
            return ("", false);
        }
        var canShoot = !shooting!.Disabled && !p.Effects.MeleeOnly;
        var canMelee = !melee!.Disabled;
        if (canShoot && canMelee)
        {
            if (melee.Level > shooting.Level || (melee.Level == shooting.Level && GearPassionRank(melee.Passion) > GearPassionRank(shooting.Passion)))
            {
                return ("Melee", true);
            }
            return ("Shooting", true);
        }
        if (canShoot)
        {
            return ("Shooting", true);
        }
        if (canMelee)
        {
            return ("Melee", true);
        }
        return ("", false);
    }

    /// <summary>
    /// Counts the pawns below the ceiling of the skill each trains. A child is skipped, as is a pawn who can train
    /// neither skill. A pawn whose profile lacks a Melee or Shooting row is unread: it is no gap, and when no gap
    /// is found elsewhere the review is unknown rather than closed.
    /// </summary>
    public static Fact<TrainingReview> ReviewTraining(Fact<EquatableArray<PawnProfile>> profiles, Fact<ResearchFacts> research)
    {
        if (!profiles.TryGetValue(out var list))
        {
            return Unknown<TrainingReview>();
        }
        var shootingCeiling = TrainingTier.UnlockedTrainingTier(research).Ceiling;
        var meleeCeiling = SparringTier.UnlockedSparringTier(research).Ceiling;
        var capable = 0;
        var shootingBelow = 0;
        var meleeBelow = 0;
        var unread = false;
        foreach (var p in list)
        {
            if (p.Child)
            {
                continue;
            }
            var (skill, ok) = TrainingSkill(p);
            if (!ok)
            {
                var sRead = p.Skills.ContainsKey("Shooting");
                var mRead = p.Skills.ContainsKey("Melee");
                unread = unread || !sRead || !mRead;
                continue;
            }
            capable++;
            var level = p.Skills[skill].Level;
            if (skill == "Shooting" && level < shootingCeiling)
            {
                shootingBelow++;
            }
            else if (skill == "Melee" && level < meleeCeiling)
            {
                meleeBelow++;
            }
        }
        var below = shootingBelow + meleeBelow;
        if (below == 0 && unread)
        {
            return Unknown<TrainingReview>();
        }
        return Known(new TrainingReview { Capable = capable, Below = below, ShootingBelow = shootingBelow, MeleeBelow = meleeBelow });
    }

    /// <summary>
    /// The stands the training range should hold: none while no pawn that trains Shooting is below the range's
    /// ceiling, else RangeStandsFor the capable adults (every capable colonist, not only those under the target,
    /// so the count does not fall as colonists train). Unknown while the profiles or their skills are unread.
    /// </summary>
    public static Fact<int> TrainingStands(Fact<EquatableArray<PawnProfile>> profiles, Fact<ResearchFacts> research)
    {
        if (!ReviewTraining(profiles, research).TryGetValue(out var r))
        {
            return Unknown<int>();
        }
        if (r.ShootingBelow == 0)
        {
            return Known(0);
        }
        return Known(RangeStandsFor(r.Capable));
    }

    /// <summary>
    /// The markers the sparring ring should hold: none until at least two pawns that train Melee are below the
    /// ring's ceiling (a lone one has no partner), else RingMarkersFor the capable adults. Unknown while the
    /// profiles or their skills are unread.
    /// </summary>
    public static Fact<int> TrainingRings(Fact<EquatableArray<PawnProfile>> profiles, Fact<ResearchFacts> research)
    {
        if (!ReviewTraining(profiles, research).TryGetValue(out var r))
        {
            return Unknown<int>();
        }
        if (r.MeleeBelow < 2)
        {
            return Known(0);
        }
        return Known(RingMarkersFor(r.Capable));
    }

    /// <summary>Raises MaintainTraining while a capable colonist is below the target, its deficit the share of capable colonists that are.</summary>
    internal static void InspectTraining(RoundsRun c)
    {
        var review = ReviewTraining(c.F.WorkProfiles, c.F.Research);
        c.Assess(MaintainTraining, TrainingPriority, Measured(review, r => r.Below == 0));
        if (review.TryGetValue(out var r) && r.Below > 0)
        {
            c.Raise(MaintainTraining, TrainingPriority).Deficit = Known((double)r.Below / r.Capable);
        }
    }

    /// <summary>
    /// The count the plan's training range (stands) or ring (markers) should hold: the count while the gap is
    /// open, else 0. A standing one stays and only grows; the plan never retires it.
    /// </summary>
    public static int RangesWanted(Fact<int> stands) => stands.TryGetValue(out var n) ? n : 0;
}
