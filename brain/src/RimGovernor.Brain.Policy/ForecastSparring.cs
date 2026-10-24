namespace RimGovernor.Brain.Policy;

/// <summary>
/// The SparringRing credit (#3813, epic #3650 P1): the raid harm the Melee its trainees gain saves (<see cref="ForecastPractice"/>).
/// Each served day every trainee below the sparring ceiling spars to the game's daily cap, today's XP reaching
/// SkillRecord.MaxFullRateXpPerDay (SkillLearning.SaturationThreshold) at the pawn's learn rate, while two are still below it
/// (a bout needs a partner). A trainee's power is MeleeDPS x health, the melee power the defense capacity counts. Only the
/// lowest-id planned ring takes the trainees. Fewer than two trainees (nobody to train with), two standing Melee targets (the
/// colony already spars) or no raid to expect earn nothing; unread trainees, mechanics or raid harm raise
/// <see cref="RequiredFactException"/>.
/// </summary>
internal static class ForecastSparring
{
    /// <summary>
    /// The mechanics the credit leaves out, named with every served ring: sparring injuries, the bout's recreation, the work time
    /// practice takes (the sessions' own scheduling prices that), and an Ideology role's skill requirement (named per role by
    /// PracticeMethodCandidates).
    /// </summary>
    internal static readonly string[] Unmodelled =
    [
        "sparring_injury_unmodelled", "sparring_recreation_unmodelled", "sparring_work_time_unmodelled",
    ];

    /// <summary>The ring's credit (<see cref="RoomCredit"/>): one row per trainee whose Melee saves harm, under its pawn id.</summary>
    internal static IReadOnlyList<RoomGain> RingCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (context.PlannedRoomRoles.Where(p => p.Value == PlannedRole.SparringRing).Min(p => p.Key) != room.Room) return [];
        var sparring = context.Sparring ?? throw new RequiredFactException("work pawns");
        if (sparring.Unread.Count > 0) throw new RequiredFactException("sparring trainees: " + string.Join(", ", sparring.Unread));
        if (sparring.Trainees.Count < 2 || sparring.Targets >= 2 || horizon <= 0) return [];
        return ForecastPractice.Credit(sparring, context, room.ServedAt, outcome.Support, horizon, "Melee", dailyReset: true, static (cursors, below) =>
        {
            // The day's bouts: every trainee still below the ceiling spars to the daily cap while it has a partner.
            if (below.Length >= 2)
                foreach (var i in below) cursors[i].LearnToSaturation();
        });
    }
}
