using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Conditional work potential, never an output promise or idle-time grant. Shared admission still protects current work.</summary>
public static class NativeLaborCapacity
{
    public static ForecastTerm HoursPerDay(PawnState worker, NeedsForecastRead? read, IReadOnlyList<WorkType> work, double days = 1)
    {
        DomainException.Require(double.IsFinite(days) && days > 0, "labor capacity horizon");
        if (worker.Dead || worker.Downed || worker.Drafted || worker.MentalState is not null || worker.Settings.Work is PawnWork.NeverWorks)
            return new ForecastTerm.Assessed(0);
        if (read?.Pawns.SingleOrDefault(p => p.Pawn == worker.Pawn.Id) is not { } pawn)
            return new ForecastTerm.Unassessed("labor_needs_unread:" + worker.Pawn.Id);
        if (!ForecastNeeds.Voluntary(pawn.State)) return new ForecastTerm.Assessed(0);
        if (BabyCareRules.CarePawns(read).Contains(pawn.Pawn))
            return new ForecastTerm.Unassessed("labor_active_care_remaining:" + pawn.Pawn);
        var job = new PawnJob { Observed = worker.Job };
        if (!ConstructionHelpersRules.IdleJob(job) && !work.Contains(job.Work) && pawn.CurrentService is null)
            return new ForecastTerm.Unassessed("labor_native_work_remaining:" + pawn.Pawn + ":" + job.Def);
        if (PawnRules.WorkHours([.. worker.Settings.Schedule.Select(s => s.AssignmentDefName)]) is not { } preferred)
            return new ForecastTerm.Unassessed("labor_schedule_unread:" + pawn.Pawn);

        var supported = pawn with { Targets = EquatableArray.From(pawn.Targets.Where(t => t.Unsupported.Count == 0)) };
        if (pawn.CurrentService is { Unsupported.Count: > 0 })
            return new ForecastTerm.Unassessed("labor_current_service_unprojected:" + pawn.Pawn);
        var remaining = pawn.CurrentService?.RemainingTicks ?? 0;
        DomainException.Require(double.IsFinite(remaining) && remaining >= 0, "labor current service duration");
        double upkeep = 0, debt = remaining / GameTime.TicksPerHour;
        foreach (var need in pawn.Needs)
        {
            DomainException.Require(double.IsFinite(need.Level) && need.Level is >= 0 and <= 1
                && double.IsFinite(need.FallPerTick) && need.FallPerTick >= 0 && need.Threshold is >= 0 and < 1, "labor need mechanics");
            if (need.Unsupported.Count > 0)
                return new ForecastTerm.Unassessed("labor_need_unprojected:" + pawn.Pawn + ":" + need.Kind);
            if (need.FallPerTick == 0) continue;
            var model = NeedServiceRules.Steady(supported, need);
            if (need.Kind == "food")
            {
                if (model.Service is not ModelService.Observed(var gain, var service, var approach))
                    return new ForecastTerm.Unassessed("labor_meal_service_unavailable:" + pawn.Pawn);
                upkeep += NeedServiceRules.MealHours(model.Fall, gain, service, approach);
                if (need.Level <= need.Threshold && pawn.CurrentService?.Kind != need.Kind) debt += (service + approach) / GameTime.TicksPerHour;
                continue;
            }
            if (!(model.Gain > 0))
                return new ForecastTerm.Unassessed("labor_need_service_unavailable:" + pawn.Pawn + ":" + need.Kind);
            upkeep += NeedServiceRules.ContinuousHours(model.Fall, model.Gain);
            var targets = supported.Targets.Where(t => t.Kind == need.Kind && t.Recovery is NeedRecovery.Rate).ToArray();
            var travel = model.Gain <= need.UnservedGainPerTick || targets.Length == 0 ? 0
                : targets.Where(t => t.Recovery is NeedRecovery.Rate r && r.GainPerTick == model.Gain).Min(t => t.TravelTicks);
            // One refill from the native threshold to full. This is service overhead, not a native future timetable.
            var uses = model.Fall * GameTime.TicksPerDay / (1 - need.Threshold);
            upkeep += uses * travel / GameTime.TicksPerHour;
            if (need.Level <= need.Threshold && pawn.CurrentService?.Kind != need.Kind)
                debt += ((1 - need.Level) / model.Gain + travel) / GameTime.TicksPerHour;
        }
        // Timetable preference is only a ceiling. Needs, approach and current service debt still consume the day.
        return new ForecastTerm.Assessed(Math.Max(0, Math.Min(preferred, 24 - upkeep) - debt / days));
    }
}
