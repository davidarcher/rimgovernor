namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal static RoundsReading ReviewRituals(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var facts = reading.Projection.Facts;
        var tick = reading.Projection.Identity.Tick;
        facts = facts with { RitualPlans = PolicyFunctions.PlanRituals(facts.Ideology, reading.Projection.WorkPawns, facts.RitualSites, tick, RoundsCalm(reading, snapshot)) };
        facts = facts with { RitualsOwed = PolicyFunctions.RitualsOwed(facts.RitualPlans) };
        return reading.WithFacts(_ => facts);
    }

    internal static Fact<bool> RoundsCalm(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var tick = reading.Projection.Identity.Tick;
        try
        {
            var emergency = EmergencySnapshot.New(snapshot, tick, reading.Emergency);
            var (hostiles, patients) = PolicyFunctions.EmergencyNeeds(emergency, snapshot, tick);
            return PolicyFunctions.RitualCalm(hostiles, patients);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unknown<bool>();
        }
    }
}

/// <summary>Composes MaintainRituals: begins the ideology ritual the plan has attendees and a site for.</summary>
public sealed class RoundsRitualsPlanner
{
    private readonly Rounder reviewer;

    private RoundsRitualsPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsRitualsPlanner NewRoundsRitualsPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(PolicyFunctions.MaintainRituals))
        {
            throw new ControlException("NewRoundsRitualsPlanner: reviewer null or method disabled");
        }

        return new RoundsRitualsPlanner(reviewer);
    }

    internal async Task<RoundsRitualsResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainRituals, call);
        if (!workable || goal is null)
        {
            return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        if (!read.Projection.Facts.RitualPlans.TryGetValue(out var plans) || plans.Count == 0)
        {
            return new RoundsRitualsResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "ritual_plans") };
        }

        return new RoundsRitualsResult { Verdict = Verdict.AwaitingPlan("unsupported_forecast_ritual",
            "Ritual participants, duration and outcome require shared forecast admission.") };
    }
}
public readonly record struct RoundsRitualsResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
