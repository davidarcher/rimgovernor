namespace RimGovernor.Brain.Policy;

/// <summary>
/// The TrainingRange credit (#3846, epic #3650 P1): the raid harm the Shooting its trainees gain saves
/// (<see cref="ForecastPractice"/>). Every pawn the one practice eligibility rule admits for Shooting trains (colony defense is
/// every capable pawn's). Each served day every trainee below the range ceiling drills to the game's daily cap: the drill's
/// direct Learn applies XP at the pawn's direct learn rate up to RangeTraining.DailyXpBudget a day (SkillLearning.RangeDailyBudget),
/// skipping the tracker's daily saturation. A trainee's power is its ranged DPS at its hit factor at each stored Shooting level
/// (the biography's RangedDefenseDps curve: ShotReport's standard-target hit chance from the ShootingAccuracyPawn the level
/// gives, over the reference gunner's), the ranged damage the defense capacity counts; a pawn without a ranged primary lands no
/// shots and gains nothing. Only the lowest-id planned range takes the trainees. No capable pawn (nobody to train), a standing
/// Shooting practice target (the colony already drills) or no raid to expect earn nothing; unread trainees, mechanics or raid
/// harm raise <see cref="RequiredFactException"/>.
/// </summary>
internal static class ForecastTrainingRange
{
    /// <summary>
    /// The mechanics the credit leaves out, named with every served range: the work time the drill takes (the sessions' own
    /// scheduling prices that), the stands' capacity (every trainee drills to the cap whatever the stand count), a gun a
    /// trainee may carry later (the power curve holds the primary it carries now), and an Ideology role's skill requirement
    /// (named per role by PracticeMethodCandidates).
    /// </summary>
    internal static readonly string[] Unmodelled =
    [
        "range_work_time_unmodelled", "range_stand_capacity_unmodelled", "range_future_weapon_unmodelled",
    ];

    /// <summary>The range's credit (<see cref="RoomCredit"/>): one row per trainee whose Shooting saves harm, under its pawn id.</summary>
    internal static IReadOnlyList<RoomGain> RangeCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (context.PlannedRoomRoles.Where(p => p.Value == PlannedRole.TrainingRange).Min(p => p.Key) != room.Room) return [];
        var range = context.Range ?? throw new RequiredFactException("work pawns");
        if (range.Unread.Count > 0) throw new RequiredFactException("range trainees: " + string.Join(", ", range.Unread));
        if (range.Trainees.Count == 0 || range.Targets > 0 || horizon <= 0) return [];
        return ForecastPractice.Credit(range, context, room.ServedAt, outcome.Support, horizon, "Shooting", dailyReset: false, static (cursors, below) =>
        {
            // The day's drill: every trainee still below the ceiling shoots until the daily budget is applied.
            foreach (var i in below) cursors[i].LearnRangeDay();
        });
    }
}
