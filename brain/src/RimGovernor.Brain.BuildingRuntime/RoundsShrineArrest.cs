namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationCustodyPlanner
{
    internal static RoundsPopulationCustodyResult CommitArrest(
        Player p,
        ControlState state,
        DateTimeOffset started,
        StandardState goal,
        RoundsFacts facts,
        IReadOnlyList<ShrineDefenderFacts
> squad,
        Domain.PawnId target,
        StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var bed = PolicyFunctions.ShrineArrestBed(facts.Sleeping);
        var performer = PolicyFunctions.ShrineArrester(squad);
        if (bed.Length == 0 || performer.IsEmpty || performer == target)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "shrine_arrest") };
        }
        return new RoundsPopulationCustodyResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_shrine_arrest",
            "Nonhostile recruitment arrest requires shared participant and outcome comparison.") };
    }
}
