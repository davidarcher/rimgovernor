namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsEnvelopeResult
{
    public Verdict Verdict { get; init; }
}

/// <summary>Keeps the climate envelope of the planned rooms: the ring walls and roof that hold the temperature.</summary>
public sealed class RoundsEnvelopePlanner
{
    private readonly Rounder _reviewer;
    private readonly RoundsBuildingPlanner? _building;

    public RoundsEnvelopePlanner(Rounder reviewer)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("NewRoundsEnvelopePlanner: reviewer == nil || reviewer.native == nil");
        }
        _reviewer = reviewer;
        if (reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = PolicyApi.MaintainClimateEnvelope };
        }
    }

    internal RoundsEnvelopeResult Step(CancellationToken call, CancellationToken epoch, StepArbiter? arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsEnvelopeResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsEnvelopeResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.MaintainClimateEnvelope, call);
        if (!workable)
        {
            return new RoundsEnvelopeResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsEnvelopeResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (_building is null)
        {
            return new RoundsEnvelopeResult { Verdict = FieldUnavailable("building_source") };
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = _reviewer.ObserveRooms(call, _reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>(), PolicyApi.ClimateDefs().VentDef);
        var (rooms, known) = EnvelopeWork(reading.Projection);
        if (!known)
        {
            return new RoundsEnvelopeResult { Verdict = FieldUnavailable("layout_plan") };
        }
        var verdict = BuildingReasonNoDeficit;
        foreach (var room in rooms)
        {
            var rr = new RoomReconcile { Room = room, Name = RoomName("envelope", room), Reason = "envelope", RingOnly = true, ClimateOnly = true };
            var result = _building.ReconcileRoom(call, epoch, state, review, goal, reading, null, rr);
            verdict = result.Verdict;
            if (verdict.Outcome == Outcome.Admitted)
            {
                return new RoundsEnvelopeResult { Verdict = verdict };
            }
        }
        return new RoundsEnvelopeResult { Verdict = verdict };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The planned rooms whose ring or roof the envelope still has to build; not known without a layout plan or colony ground.</summary>
    internal static (List<PlannedRoom> Rooms, bool Known) EnvelopeWork(ColonyProjection facts)
    {
        if (!facts.LayoutPlan.TryGetValue(out var plan))
        {
            return ([], false);
        }
        var rooms = new List<PlannedRoom>();
        foreach (var room in plan.Rooms)
        {
            if (room.Parent != 0)
            {
                continue; // an airlock rides its parent's ring and roof
            }
            var (input, ground) = RoomRingInput(facts, plan, room);
            if (!ground)
            {
                return ([], false);
            }
            if (PolicyApi.EnvelopeOps(PolicyApi.ReconcileRoom(input)).Count > 0)
            {
                rooms.Add(room);
            }
        }
        var (_, colonyGround) = ColonyGround(facts);
        if (!colonyGround)
        {
            return ([], false);
        }
        return (rooms, true);
    }

    internal static Fact<bool> EnvelopeOwed(ColonyProjection facts)
    {
        var (rooms, known) = EnvelopeWork(facts);
        return known ? Known(rooms.Count > 0) : Unknown<bool>();
    }
}
