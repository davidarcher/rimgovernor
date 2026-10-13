using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsBurialResult : IPlannerMethod
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

    internal async Task<RoundsBurialResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBurialResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil || state.Snapshot.Native.Value == 0");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBurialResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.MaintainBurial, call);
        if (goal is null || !workable)
        {
            return new RoundsBurialResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsBurialResult { Verdict = BuildingReasonExistingWork };
            }
        }
        if (_building is null)
        {
            return new RoundsBurialResult { Verdict = FieldUnavailable("building_source") };
        }
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var reading = await _reviewer.ObserveRoomsAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), BurialDefinitions, call).ConfigureAwait(false);
        reading = reading.WithProjection(_reviewer.ReviewStrangers(state.Snapshot, reading.Projection, call));
        var step = TombStep(reading.Projection);
        var stock = new PackedStock(_reviewer.Native, Boundary.Identity(state.Snapshot));
        RoundsBuildingResult result;
        if (step.Kind == TombStepKind.Dispose)
        {
            return await DisposeAsync(state, review, goal, reading, step.Disposal[0], call, epoch).ConfigureAwait(false);
        }
        if (step.Kind == TombStepKind.None)
        {
            var (morgue, owed) = PlannedMorgue(reading.Projection);
            if (!owed)
            {
                return new RoundsBurialResult { Verdict = BuildingReasonNoDeficit };
            }
            result = await _building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = morgue, Name = RoomName("morgue", morgue), Reason = "morgue" }, call, epoch).ConfigureAwait(false);
        }
        else if (step.Kind == TombStepKind.Reconcile)
        {
            var role = step.Room.Role == PolicyApi.PlannedGraveyard ? "graveyard" : "tomb";
            result = await _building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = step.Room, Template = step.Template, Name = RoomName(role, step.Room), Reason = role }, call, epoch).ConfigureAwait(false);
        }
        else if (step.Kind == TombStepKind.Full)
        {
            return new RoundsBurialResult { Verdict = WaitFor(Cause.MethodUsed, "tomb_room") };
        }
        else
        {
            return new RoundsBurialResult();
        }
        return new RoundsBurialResult { Verdict = result.Verdict };
    }

    private async Task<RoundsBurialResult> DisposeAsync(ControlState state, RoundsReview review, StandardState goal, RoundsReading reading, CurrentBuilding b, CancellationToken call, CancellationToken epoch)
    {
        var building = b.Building ?? throw new InvalidOperationException("a standing tomb row carries its building");
        var result = await _building!.RetireBuildingAsync(state, review, goal, reading.Reading, b.Id, building.Definition, building.Cell, "tomb-dispose", "tomb_disposal", call, epoch).ConfigureAwait(false);
        return new RoundsBurialResult { Verdict = result.Verdict };
    }

    internal static string RoomName(string role, PlannedRoom room)
    {
        var bounds = room.Region.Bounds();
        return string.Create(CultureInfo.InvariantCulture, $"{role}-{bounds.X}-{bounds.Z}");
    }
}
