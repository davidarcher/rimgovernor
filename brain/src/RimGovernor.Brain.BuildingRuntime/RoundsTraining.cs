namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsTrainingResult
{
    public Verdict Verdict { get; init; } = new();
}

/// <summary>
/// MaintainTraining (#2619): while a capable colonist is below the combat skill target the stockpile review asks
/// layout for a training range (RoomDemand.Ranges, the stand count; a further graveyard is asked for the same
/// way), and while two fighters are below the melee ceiling for a sparring ring (RoomDemand.Rings, the marker
/// count; #2707). This planner then stages the planned range's stands and dummies and the ring's markers
/// through the room reconciler. Both are unfenced open air (PlannedRoom.Unfenced), so the reconciler owes them
/// no ring, roof or floor, only the template; one that outgrew its stands or markers is widened by layout and
/// the same template builds the new ones. A standing range or ring takes no further action here: the native
/// training and spar jobs spend the pawn-hours.
/// </summary>
public sealed class RoundsTrainingPlanner
{
    private readonly Rounder _reviewer;

    // Stages the room; null for a source that cannot preview buildings.
    private readonly RoundsBuildingPlanner? _building;

    private RoundsTrainingPlanner(Rounder reviewer, RoundsBuildingPlanner? building)
    {
        _reviewer = reviewer;
        _building = building;
    }

    public static RoundsTrainingPlanner Create(Rounder? reviewer)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("NewRoundsTrainingPlanner: reviewer == nil || reviewer.native == nil");
        }
        RoundsBuildingPlanner? building = null;
        if (reviewer.Native is IRoundsBuildingSource source)
        {
            building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = MaintainTraining };
        }
        return new RoundsTrainingPlanner(reviewer, building);
    }

    /// <summary>The definitions the range step reads availability and stuff for: the range's own buildings.</summary>
    internal static readonly string[] TrainingDefinitions = [RangeDefNames[RangePiece.Stand], RangeDefNames[RangePiece.Dummy], RingMarkerDef];

    internal RoundsTrainingResult Step(CancellationToken call, CancellationToken epoch, StepArbiter _)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsTrainingResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid() || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsTrainingResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(call, review, MaintainTraining);
        if (!workable)
        {
            return new RoundsTrainingResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(call, method.Plan);
            if (Sto.PlanState.PlanOpen(plan))
            {
                return new RoundsTrainingResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (_building is null)
        {
            return new RoundsTrainingResult { Verdict = FieldUnavailable("building_source") };
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = _reviewer.ObserveRooms(call, _reviewer.Native, expected, Unknown<IReadOnlyList<Pol.ConstructionClaim>>(), TrainingDefinitions);
        if (!reading.Projection.LayoutPlan.TryGetValue(out var layout))
        {
            return new RoundsTrainingResult { Verdict = FieldUnavailable("layout_plan") };
        }
        var rooms = new List<RoomReconcile>();
        var ranges = layout.RangeRooms();
        if (ranges.Count > 0)
        {
            var room = ranges[0];
            rooms.Add(new RoomReconcile { Room = room, Template = RangeTemplate(room), Name = RoomName("range", room), Reason = "range" });
        }
        var rings = layout.RingRooms();
        if (rings.Count > 0)
        {
            var room = rings[0];
            rooms.Add(new RoomReconcile { Room = room, Template = RingTemplate(room), Name = RoomName("ring", room), Reason = "ring" });
        }
        if (rooms.Count == 0)
        {
            // The layout review grows the range and ring from RoomDemand.
            return new RoundsTrainingResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "range_room") };
        }
        var stock = PackedStock.Create(_reviewer.Native, Boundary.Identity(state.Snapshot));
        var verdict = new Verdict();
        foreach (var rr in rooms)
        {
            var result = _building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, rr);
            verdict = result.Verdict;
            // A room with nothing ready (finished, or waiting on its builders) leaves the next one its turn; work staged ends the step.
            if (!verdict.Is(Pol.Cause.ExistingWork))
            {
                break;
            }
        }
        return new RoundsTrainingResult { Verdict = verdict };
    }

    /// <summary>The range's stands and the ring's markers the review wants, read from the projection's work pawns; unknown while they are.</summary>
    internal static (Fact<int> Stands, Fact<int> Rings) TrainingDemand(Observation.ColonyProjection facts)
    {
        if (!facts.WorkPawns.TryGetValue(out var pawns))
        {
            return (Unknown<int>(), Unknown<int>());
        }
        var profiles = Known<IReadOnlyList<Pol.PawnProfile>>(Profiles(pawns));
        return (TrainingStands(profiles, facts.Facts.Research), TrainingRings(profiles, facts.Facts.Research));
    }
}
