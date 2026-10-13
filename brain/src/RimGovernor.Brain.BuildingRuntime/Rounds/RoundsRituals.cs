namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    internal RoundsReading ReviewRituals(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var facts = reading.Projection.Facts;
        var tick = reading.Projection.Identity.Tick;
        facts = facts with { RitualPlans = PolicyFunctions.PlanRituals(facts.Ideology, reading.Projection.WorkPawns, facts.RitualSites, tick, RoundsCalm(reading, snapshot)) };
        facts = facts with { RitualsOwed = PolicyFunctions.RitualsOwed(facts.RitualPlans) };
        return reading with { Projection = reading.Projection with { Facts = facts } };
    }

    internal Fact<bool> RoundsCalm(RoundsReading reading, GenerationSnapshot snapshot)
    {
        var tick = reading.Projection.Identity.Tick;
        try
        {
            var emergency = PolicyFunctions.NewEmergencySnapshot(snapshot, tick, reading.Emergency);
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
        if (reviewer is null || !reviewer.MethodEnabled(ConcernId.MaintainRituals))
        {
            throw new ControlException("NewRoundsRitualsPlanner: reviewer null or method disabled");
        }

        return new RoundsRitualsPlanner(reviewer);
    }

    internal RoundsRitualsResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
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

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainRituals);
        if (!workable)
        {
            return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = BuildingRuntimePackage.StepScope(call, reviewer.Native);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = reviewer.ObserveOwned(call, reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        if (!read.Projection.Facts.RitualPlans.TryGetValue(out var plans) || plans.Count == 0)
        {
            return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "ritual_plans") };
        }

        Verdict held = default;
        var blocked = false;
        foreach (var plan in plans)
        {
            foreach (var site in plan.Sites)
            {
                var prefix = $"ritual-{plan.Ritual}-{site.X}-{site.Z}-";
                var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
                if (!ok)
                {
                    held = verdict;
                    blocked = true;
                    continue;
                }

                var attendees = plan.Attendees().Select(pawn => new PawnId(pawn)).ToList();
                if (!arbiter.TryClaim(attendees))
                {
                    return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.ClaimHeld("attendees") };
                }

                var slots = plan.Slots.Select(slot => new RitualSlot { Slot = slot.Slot, Pawns = slot.Pawns.Select(pawn => new PawnId(pawn)).ToList() }).ToList();
                var spectators = plan.Spectators.Select(pawn => new PawnId(pawn)).ToList();
                var ritual = RitualBegin.NewRitualBegin(new PawnId(plan.Organizer), plan.Ritual, site, slots, spectators);
                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewRitualAction(new ActionId($"{id}-0"), ritual);
                var spec = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
                p.Current(call, epoch);
                var elapsed = reviewer.Clock.Now() - started;
                if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
                {
                    throw new ControlException("step: session state changed or the read took too long");
                }

                var reason = $"ideology: {plan.Organizer} leads {plan.Def} at ({site.X},{site.Z}) with {attendees.Count} attending";
                p.Journal.CommitMethodReason(call, goal.Standard.Id, goal.Revision, method, reason, spec);
                return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
            }
        }

        if (blocked)
        {
            return new RoundsRitualsResult { Verdict = held };
        }

        return new RoundsRitualsResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "ritual_sites") };
    }
}

public readonly record struct RoundsRitualsResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
