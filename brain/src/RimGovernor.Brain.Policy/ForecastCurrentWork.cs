namespace RimGovernor.Brain.Policy;

/// <summary>Useful time left in an observed job; an untimed job protects the whole forecast.</summary>
public abstract record ForecastWorkRemaining
{
    private ForecastWorkRemaining() { }
    public sealed record Timed(double Ticks) : ForecastWorkRemaining;
    public sealed record Untimed : ForecastWorkRemaining;
}

/// <summary>Observed useful job occupancy, independently of whether native execution can be interrupted.</summary>
public sealed record ForecastCurrentWork(PawnId Pawn, bool Occupied, WorkType Work, ForecastWorkRemaining Remaining)
{
    public static ForecastCurrentWork ForecastCurrentWorkOf(WorkPawn pawn, ForecastWorkRemaining? remaining = null)
    {
        remaining ??= new ForecastWorkRemaining.Untimed();
        if (remaining is ForecastWorkRemaining.Timed(var ticks) && (!double.IsFinite(ticks) || ticks < 0))
            throw new DomainException("invalid current job remaining time");
        var job = pawn.Job;
        if (ConstructionHelpersRules.IdleJob(job)) return new(pawn.Id, false, WorkType.None, new ForecastWorkRemaining.Timed(0));
        return new(pawn.Id, true, job.Work, remaining);
    }
}
