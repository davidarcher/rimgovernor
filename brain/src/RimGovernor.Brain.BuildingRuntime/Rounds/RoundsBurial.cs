using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public sealed class RoundsBurialResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }
}

/// <summary>Plans the morgue and tomb rooms and the plain graves near the base.</summary>
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
        var step = TombStep(reading.Projection);
        var stock = new PackedStock(_reviewer.Native, Boundary.Identity(state.Snapshot));
        RoundsBuildingResult result;
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
            result = await _building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = step.Room, Template = step.Template, Name = RoomName("tomb", step.Room), Reason = "tomb" }, call, epoch).ConfigureAwait(false);
        }
        else if (step.Kind == TombStepKind.Grave)
        {
            return await PlaceGraveAsync(state, goal, reading.Projection, step, call, epoch).ConfigureAwait(false);
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

    /// <summary>
    /// Places one plain grave on the nearest GraveSites site the native preview accepts, admitted as one building
    /// method per grave count; a grave blueprint or frame standing is the step in flight.
    /// </summary>
    private async Task<RoundsBurialResult> PlaceGraveAsync(ControlState state, StandardState goal, ColonyProjection f, TombStep step, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        if (!f.Facts.CurrentConstruction.TryGetValue(out var census))
        {
            return new RoundsBurialResult { Verdict = FieldUnavailable("construction") };
        }
        if (census.Sites.Any(s => s.Building?.Definition == PolicyApi.GraveDefinition))
        {
            return new RoundsBurialResult { Verdict = BuildingReasonExistingWork };
        }
        if (!f.LayoutPlan.TryGetValue(out var plan))
        {
            return new RoundsBurialResult { Verdict = BuildingReasonNoLayoutPlan };
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"grave-{step.Graves}"));
        if (goal.Methods.Any(m => m.MethodId == method))
        {
            return new RoundsBurialResult { Verdict = WaitFor(Cause.MethodUsed, "grave_method") };
        }
        IReadOnlyList<Cell> traffic = f.Facts.Upkeep.Routes.TryGetValue(out var routes) ? [.. routes.Traffic.Select(t => t.Cell)] : [];
        var sites = PolicyApi.GraveSites(new GraveSiteInput { Bounds = f.Bounds, Cells = f.Cells, Light = f.LightTerrains, Plan = plan!, Traffic = traffic });
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var size = new Cell(1, 2);
        var unknown = false;
        foreach (var site in sites.Take(GraveSiteTries))
        {
            var building = new Building(PolicyApi.GraveDefinition, PolicyApi.AnchorForRect(site, size, Rotation.North), Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTier.Expand);
            var (got, _) = await _building!.Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var pv = got.Preview;
            if (!pv.Footprint.TryGetValue(out var footprint) || !pv.MadeFromStuff.TryGetValue(out var made) || !pv.CanPlace.TryGetValue(out var legal)
                || !pv.SafeToPlace.TryGetValue(out var safe) || !pv.WatchCellsAccessible.TryGetValue(out var reachable))
            {
                unknown = true;
                continue;
            }
            if (made || !legal || !safe || !reachable || !FootprintIsRect(footprint!, site))
            {
                continue;
            }
            p.Current(call, epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("step: session state changed");
            }
            var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
            {
                Owner = goal,
                Method = method,
                Plan = new PlanSpec(planId, new PlanRevision(1), [action]),
                Current = snapshot,
                Tick = f.Identity.Tick,
                Bounds = Known(f.Bounds),
                Stock = got.Stock,
                Previews = [pv],
                Purpose = Purpose.Rounds,
            }, call).ConfigureAwait(false);
            return new RoundsBurialResult { Verdict = decision.Admitted ? BuildingReasonAdmitted : AdmissionLog.AdmissionRefused(decision) };
        }
        return new RoundsBurialResult { Verdict = unknown ? FieldUnavailable("grave_preview") : NoSpace("grave_cell") };
    }

    /// <summary>The grave sites previewed per step, nearest first.</summary>
    private const int GraveSiteTries = 8;

    internal static string RoomName(string role, PlannedRoom room)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{role}-room{room.Id}");
    }
}
