using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsBurialResult
{
    public Verdict Verdict { get; init; }
}

/// <summary>Plans the morgue, tomb and graveyard rooms and the disposal of the tombs the colony no longer needs.</summary>
public sealed class RoundsBurialPlanner
{
    private readonly Rounder _reviewer;
    private readonly RoundsBuildingPlanner? _building;

    public RoundsBurialPlanner(Rounder reviewer)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("NewRoundsBurialPlanner: reviewer == nil || reviewer.native == nil");
        }
        _reviewer = reviewer;
        if (reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = PolicyApi.MaintainBurial };
        }
    }

    internal RoundsBurialResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBurialResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native == 0");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBurialResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.MaintainBurial, call);
        if (!workable)
        {
            return new RoundsBurialResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StoreApi.PlanOpen(plan))
            {
                return new RoundsBurialResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (_building is null)
        {
            return new RoundsBurialResult { Verdict = FieldUnavailable("building_source") };
        }
        var expected = StepScope(call, _reviewer.Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = _reviewer.ObserveRooms(call, _reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>(), BurialDefinitions);
        _reviewer.ReviewStrangers(call, state.Snapshot, reading.Projection);
        var step = TombStep(reading.Projection);
        var stock = new PackedStock(_reviewer.Native, BoundaryApi.Identity(state.Snapshot));
        RoundsBuildingResult result;
        if (step.Kind == PolicyApi.TombDispose)
        {
            return Dispose(call, epoch, state, review, goal, reading, step.Disposal[0]);
        }
        if (step.Kind == PolicyApi.TombNone)
        {
            var (morgue, owed) = PlannedMorgue(reading.Projection);
            if (!owed)
            {
                return new RoundsBurialResult { Verdict = BuildingReasonNoDeficit };
            }
            result = _building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, new RoomReconcile { Room = morgue, Name = RoomName("morgue", morgue), Reason = "morgue" });
        }
        else if (step.Kind == PolicyApi.TombReconcile)
        {
            var role = step.Room.Role == PolicyApi.PlannedGraveyard ? "graveyard" : "tomb";
            result = _building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, new RoomReconcile { Room = step.Room, Template = step.Template, Name = RoomName(role, step.Room), Reason = role });
        }
        else if (step.Kind == PolicyApi.TombFull)
        {
            return new RoundsBurialResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "tomb_room") };
        }
        else
        {
            return new RoundsBurialResult();
        }
        return new RoundsBurialResult { Verdict = result.Verdict };
    }

    private RoundsBurialResult Dispose(CancellationToken call, CancellationToken epoch, ControlState state, RoundsReview review, StandardState goal, RoundsReading reading, CurrentBuilding b)
    {
        var result = _building!.RetireBuilding(call, epoch, state, review, goal, reading.ColonyReading, b.Id, b.Building.Definition, b.Building.Cell, "tomb-dispose", "tomb_disposal");
        return new RoundsBurialResult { Verdict = result.Verdict };
    }

    internal static string RoomName(string role, PlannedRoom room)
    {
        var bounds = room.Region.Bounds();
        return string.Create(CultureInfo.InvariantCulture, $"{role}-{bounds.X}-{bounds.Z}");
    }
}
