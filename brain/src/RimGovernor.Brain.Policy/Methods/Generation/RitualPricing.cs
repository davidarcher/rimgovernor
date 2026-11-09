using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>
/// The ritual mechanics the forecast prices, shared by the worship room credit (<see cref="ForecastWorship"/>), the activity-site
/// term and the ritual roots (<see cref="RitualMethodCandidates"/>, #4058): when a held ritual is due, the quality a site and an
/// attendance predict, and the outcome draw that quality gives.
/// </summary>
internal static class RitualPricing
{
    /// <summary>The finding of a ritual whose outcome effect is not drawn from quality: <c>ritual_outcome_unmodelled:{worker}</c>.</summary>
    public const string RitualUnmodelled = "ritual_outcome_unmodelled:";

    /// <summary>
    /// Whether the held ritual is due at tick. A running ritual is not. An active obligation (the game's own,
    /// raised by the pattern's triggers) is due. Otherwise the pattern must allow a free start, the repeat
    /// penalty must be off and the pattern's own cooldown (the minimum of its free start interval, in days)
    /// must have passed since the raw lastFinishedTick.
    /// </summary>
    internal static bool Due(RitualDef def, IdeoRitual held, Tick tick)
    {
        if (held.Running)
        {
            return false;
        }
        if (held.ActiveObligations > 0)
        {
            return true;
        }
        if ((!def.CanStartAnytime && !def.AlwaysStartAnytime) || held.RepeatPenaltyActive)
        {
            return false;
        }
        return (double)(tick.Value - held.LastFinishedTick) >= def.IntervalDaysMin * GameTime.TicksPerDay;
    }

    /// <summary>
    /// The ritual's quality held with <paramref name="participants"/> attending, at full progress
    /// (RitualOutcomeEffectWorker_FromQuality.GetQuality): startingQuality plus each comp's curve at its input (the
    /// participant count capped at the curve's last x; the room stat capped there and rounded to 0.1), clamped. A room-stat comp
    /// applies only to a spot with a room (RitualOutcomeComp_RoomStat.Applies): outdoors it adds nothing.
    /// </summary>
    internal static double Quality(RitualOutcomeModel model, int participants, double impressiveness, double cleanliness, bool indoors = true)
    {
        var quality = model.StartingQuality;
        foreach (var comp in model.Comps)
        {
            if (comp.Curve.Count == 0 || !indoors && comp.Input != RitualQualityInput.Participants) continue;
            var last = comp.Curve[^1].X;
            var x = comp.Input == RitualQualityInput.Participants ? Math.Floor(Math.Min(participants, last))
                : Math.Round(Math.Min(comp.Input == RitualQualityInput.Impressiveness ? impressiveness : cleanliness, last) * 10) / 10;
            quality += Curves.Evaluate(comp.Curve, x);
        }
        return Math.Clamp(quality, model.MinQuality, Math.Max(model.MinQuality, model.MaxQuality));
    }

    /// <summary>
    /// The expected memory mood a day of a ritual held every <paramref name="intervalDays"/> at <paramref name="quality"/>: each
    /// outcome drawn by <see cref="OutcomeWeights"/>, its memory's mood over min(duration, interval) / interval.
    /// </summary>
    internal static double ExpectedMood(RitualOutcomeModel model, double quality, double intervalDays)
    {
        var weights = OutcomeWeights(model, quality);
        if (weights.Length == 0 || !(intervalDays > 0)) return 0;
        return model.Chances.Select((o, i) => weights[i] * o.Mood * Math.Min(o.DurationDays, intervalDays) / intervalDays).Sum();
    }

    /// <summary>
    /// Each outcome's draw probability at <paramref name="quality"/> (RitualOutcomeEffectWorker_FromQuality: weight chance x quality
    /// for a positive outcome, chance otherwise, normalized), by the model's chance order; empty when no weight is positive (#4058).
    /// </summary>
    internal static double[] OutcomeWeights(RitualOutcomeModel model, double quality)
    {
        var weights = model.Chances.Select(o => o.Positive ? Math.Max(o.Chance * quality, 0) : o.Chance).ToArray();
        var total = weights.Sum();
        return total > 0 ? weights.Select(w => w / total).ToArray() : [];
    }
}
