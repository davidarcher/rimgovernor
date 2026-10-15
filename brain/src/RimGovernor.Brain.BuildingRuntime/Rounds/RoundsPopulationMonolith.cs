namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsPopulationCustodyPlanner
{
    /// <summary>Advances the monolith (investigate, activate, interact) with a capable colonist; not handled when nothing is owed.</summary>
    internal async Task<(RoundsPopulationCustodyResult Result, bool Handled)> StepMonolithAsync(Player p, ControlState state, DateTimeOffset started, StandardState goal, RoundsFacts facts, StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        if (!PolicyFunctions.MonolithInPlay(facts.Monolith))
        {
            return (default, false);
        }

        var review = p.Journal.LoadRounds(call);
        var identity = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await native.ReadEmergencyAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("stepMonolith: emergency read outside the review", e);
        }

        if (emergency.Context is not { } emergencyContext || emergencyContext.Tick < review.Tick.Value)
        {
            throw new ControlException("stepMonolith: emergency read outside the review");
        }

        var snapshot = EmergencySnapshot.New(state.Snapshot, review.Tick, emergency.Facts);
        var (hostiles, _) = PolicyFunctions.EmergencyNeeds(snapshot, state.Snapshot, review.Tick);
        facts = facts with { Hostiles = hostiles };
        var advance = PolicyFunctions.MonolithAdvanceOwed(facts.Monolith, PolicyFunctions.MonolithGate(facts));
        foreach (var issue in advance.Issues)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "refused", "monolith_advance_unread", "monolith", new Dictionary<string, object?> { ["detail"] = issue }, call);
        }

        if (advance.Order == MonolithOrder.None)
        {
            return (default, false);
        }

        return (new RoundsPopulationCustodyResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_monolith",
            "Monolith interaction requires shared forecast worker, duration and outcome admission.") }, true);
    }
}