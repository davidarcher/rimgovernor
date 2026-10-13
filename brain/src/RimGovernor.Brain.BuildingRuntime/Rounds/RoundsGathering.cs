namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal void ReviewGathering(ref RoundsReading reading, GenerationSnapshot snapshot)
    {
        var facts = reading.Projection.Facts;
        var benefit = Unknown<double>();
        var catalog = reading.Frame.Catalog;
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
        reading = reading with { Projection = reading.Projection with { Facts = facts } };
    }
}

/// <summary>Composes HoldGatherings' rounds step: one organizer throws a gathering on a colony-wide mood dip.</summary>
public sealed partial class RoundsGatheringPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    public static RoundsGatheringPlanner NewRoundsGatheringPlanner(Rounder reviewer)
    {
        if (reviewer is null || !reviewer.MethodEnabled(ConcernId.HoldGatherings))
        {
            throw new ControlException("NewRoundsGatheringPlanner: reviewer is missing or the method is disabled");
        }

        return new RoundsGatheringPlanner { Reviewer = reviewer };
    }
}

public readonly record struct RoundsGatheringResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    internal const string GatheringMethodPrefix = "gathering-";
}

public sealed partial class RoundsGatheringPlanner
{
    internal RoundsGatheringResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
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

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.HoldGatherings);
        if (!workable)
        {
            return new RoundsGatheringResult { Verdict = BuildingReasonNoDeficit };
        }

        var lastStart = new Tick(-1);
        foreach (var method in goal.History)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
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

        var expected = StepScope(call, Reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = Reviewer.Clock.Now();
        var read = Reviewer.ObserveOwned(call, Reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        if (!read.Projection.Facts.GatheringPlan.TryGetValue(out var party) || party.Organizer.Length == 0)
        {
            return new RoundsGatheringResult { Verdict = WaitFor(Cause.MethodUsed, "gathering_plan") };
        }

        var (method2, verdict, ok) = AdmitStandardMethod(call, p.Journal, goal, GatheringMethodPrefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsGatheringResult { Verdict = verdict };
        }

        if (!arbiter.TryClaim([new PawnId(party.Organizer)]))
        {
            return new RoundsGatheringResult { Verdict = ClaimHeld("organizer") };
        }

        var gathering = Gathering.NewGathering(party.Def, new PawnId(party.Organizer));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewGatheringAction(new ActionId($"{id}-0"), gathering);
        var spec = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = Reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        var reason = $"gathering: {party.Organizer} organizes a {party.Def} on a colony-wide mood dip";
        p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method2, reason, spec);
        return new RoundsGatheringResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
