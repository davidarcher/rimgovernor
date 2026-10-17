namespace RimGovernor.Brain.Policy;

/// <summary>Observed occupancy, not an assignment or a prediction that a job will complete.</summary>
public sealed partial record ForecastCurrentWork(PawnId Pawn, Fact<bool> Occupied, Fact<WorkType> Work,
    Fact<double> RemainingTicks);

public sealed partial record ForecastCurrentWork
{
    /// <summary>The roster and timed forecast share current-job interpretation. Unknown duration is never free time.</summary>
    public static ForecastCurrentWork ForecastCurrentWorkOf(WorkPawn pawn, Fact<double> remainingTicks = default)
    {
        if (remainingTicks.TryGetValue(out var remaining) && (!double.IsFinite(remaining) || remaining < 0))
            throw new DomainException("invalid current job remaining time");
        if (!pawn.Job.TryGetValue(out var job)) return new(pawn.Id, default, default, default);
        if (ConstructionHelpersRules.IdleJob(job)) return new(pawn.Id, Known(false), Known(WorkType.None), Known(0.0));
        return new(pawn.Id, Known(true), Known(job.Work), remainingTicks);
    }
}
