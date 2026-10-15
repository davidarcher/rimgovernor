namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal static RoundsReading ReviewGathering(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var facts = reading.Projection.Facts;
        var benefit = Unknown<double>();
        var catalog = reading.Catalog;
        if (catalog is not null && catalog.ThoughtFacts(PolicyFunctions.PartyThought) is { } thought && thought.WorstOffset > 0)
        {
            benefit = Known(thought.WorstOffset);
        }

        var plan = PolicyFunctions.PlanGathering(new GatheringInput
        {
            Ledger = facts.MoodLedger,
            Pawns = facts.MoodPawns,
            Benefit = benefit,
            Calm = RoundsCalm(reading, snapshot),
            Census = facts.CurrentConstruction,
        });
        facts = facts with { GatheringPlan = plan, GatheringOwed = PolicyFunctions.GatheringOwed(plan) };
        return reading with { Reading = reading.Reading with { Projection = reading.Projection with { Facts = facts } } };
    }
}

/// <summary>Reports gathering demand until shared forecast participation is supported.</summary>
public sealed partial class RoundsGatheringPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    public static RoundsGatheringPlanner NewRoundsGatheringPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(PolicyFunctions.HoldGatherings))
        {
            throw new ControlException("NewRoundsGatheringPlanner: reviewer is missing or the method is disabled");
        }

        return new RoundsGatheringPlanner { Reviewer = reviewer };
    }
}

public readonly record struct RoundsGatheringResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

public sealed partial class RoundsGatheringPlanner
{
    internal async Task<RoundsGatheringResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsGatheringResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsGatheringResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.HoldGatherings, call);
        if (!workable || goal is null)
        {
            return new RoundsGatheringResult { Verdict = BuildingReasonNoDeficit };
        }

        var lastStart = new Tick(-1);
        foreach (var method in goal.History)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsGatheringResult { Verdict = BuildingReasonExistingWork };
            }

            foreach (var progress in plan.Progress)
            {
                var view = progress.View;
                if (view.Stage == Stage.Completed && view.Tick > lastStart)
                {
                    lastStart = view.Tick;
                }
            }
        }

        if (lastStart.Value >= 0 && PolicyFunctions.GatheringRunning(lastStart, review.Tick))
        {
            return new RoundsGatheringResult { Verdict = WaitFor(Cause.MethodUsed, "gathering_running") };
        }

        var expected = await StepScopeAsync(Reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = Reviewer.Clock.Now();
        var read = await Reviewer.ObserveOwnedAsync(Reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        if (!read.Projection.Facts.GatheringPlan.TryGetValue(out var party) || party.Organizer.IsEmpty)
        {
            return new RoundsGatheringResult { Verdict = WaitFor(Cause.MethodUsed, "gathering_plan") };
        }

        return new RoundsGatheringResult { Verdict = AwaitingPlan("unsupported_forecast_gathering",
            "Gathering participation requires shared forecast occupancy and need effects.") };
    }
}
