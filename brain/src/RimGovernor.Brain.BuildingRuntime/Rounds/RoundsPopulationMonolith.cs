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
        var advance = PolicyFunctions.MonolithAdvanceOwed(facts.Monolith, PolicyFunctions.MonolithGate(facts, reviewer.Staged().ColonyStage));
        foreach (var issue in advance.Issues)
        {
            BuildingRuntimePackage.DefenseAction("routine-population-custody", TelemetryLevel.Warn, "refused", "monolith_advance_unread", "monolith", new Dictionary<string, object?> { ["detail"] = issue }, call);
        }

        if (advance.Order == MonolithOrder.None)
        {
            return (default, false);
        }

        var able = new List<PawnId>();
        var target = advance.Monolith;
        var method = Domain.RecoveryMethod.ActivateMonolith;
        var job = $"activate-{advance.Level}";
        switch (advance.Order)
        {
            case MonolithOrder.Investigate:
                method = Domain.RecoveryMethod.InvestigateMonolith;
                job = $"investigate-{advance.Level}";
                break;
            case MonolithOrder.Interact:
                target = advance.Target;
                method = Domain.RecoveryMethod.Interact;
                job = "interact-" + target;
                break;
        }

        if (advance.Performers.Count > 0)
        {
            able.AddRange(advance.Performers.Select(id => new PawnId(id)));
        }
        else
        {
            if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete)
            {
                return (new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "colonists_complete") }, true);
            }

            foreach (var pawn in emergency.Facts.Colonists)
            {
                var dk = pawn.Dead.TryGetValue(out var dead);
                var wk = pawn.Downed.TryGetValue(out var downed);
                var inMental = pawn.MentalState.IsKnown;
                if (dk && !dead && wk && !downed && !inMental)
                {
                    able.Add(new PawnId(pawn.Id.Value));
                }
            }

            able.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        }

        var prefix = $"population-monolith-{job}-";
        var (attemptMethod, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!admitted)
        {
            return (new RoundsPopulationCustodyResult { Verdict = verdict }, true);
        }

        if (able.Count == 0)
        {
            return (new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "monolith_performer") }, true);
        }

        var pawnId = able[BuildingRuntimePackage.StandardMethodPlans(goal.History, goal.Standard.Episode, prefix).Count % able.Count];
        if (!arbiter.TryClaim([pawnId]))
        {
            return (new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "pawn_claim") }, true);
        }

        var service = new RecoveryService(new Domain.PawnId(pawnId.Value), target, method);
        var planId = PlanIds.MintPlanId();
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{planId}-0"), service);
        return (Commit(p, state, started, goal, attemptMethod, planId, action, call, epoch), true);
    }
}
