using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsClearanceSource : IColonySource, IClearanceSource, IFieldNative
{
}

public sealed class RoundsClearanceResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    /// <summary>The ticks the native work of the step will take, when the verdict waits on it.</summary>
    public uint NativeWorkTicks { get; init; }
}

/// <summary>Clears the home obstructions: roof, furniture, doors, walls and floors in the way of the planned ground.</summary>
public sealed partial class RoundsClearancePlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsClearanceSource _native;

    public RoundsClearancePlanner(Rounder reviewer, IRoundsClearanceSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsClearancePlanner: reviewer == nil || native == nil");
        }
        _reviewer = reviewer;
        _native = native;
    }

    internal RoundsClearanceResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsClearanceResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsClearanceResult { Verdict = BuildingReasonNoReview };
        }
        var (stepCall, recorded) = RecordPlannerStep(call, PolicyApi.ClearHomeObstructions, state.Snapshot, review.Tick);
        try
        {
            return StepRecorded(stepCall, epoch, p, state, review);
        }
        finally
        {
            recorded();
        }
    }

    private RoundsClearanceResult StepRecorded(CancellationToken call, CancellationToken epoch, Player p, ControlState state, RoundsReview review)
    {
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.ClearHomeObstructions, call);
        if (!workable)
        {
            return new RoundsClearanceResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan0))
            {
                return new RoundsClearanceResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var started = _reviewer.Clock.Now();
        var identity = _native.Identity(call).Identity;
        var expected = ObservationApi.DecodeIdentity(identity);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var colony = _reviewer.ObserveColony(call, _native, expected, null);
        var ground = PlannedGround(colony.Projection);
        var read = ObservationApi.ObserveClearanceCensusOnGround(call, _native, expected, true, ground);
        if (!read.TryGetValue(out var census))
        {
            return new RoundsClearanceResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "clearance_census_unknown") };
        }
        var (others, player) = PolicyApi.SplitGroundRows(census.Targets);
        var step = RecoveryStep(call, Boundary.Identity(state.Snapshot), review.RecoveryQueue, others, colony.Projection);
        var id = PlanIds.MintPlanId();
        string prefix;
        IReadOnlyList<DomainAction> actions;
        if (step.Roof.Count > 0 || step.Targets.Count > 0)
        {
            step = PendingClearanceStep(step);
            if (step.Roof.Count + step.Targets.Count == 0)
            {
                return new RoundsClearanceResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = StockWaitTicks };
            }
            (prefix, actions) = RecoveryStepMethod(id, step);
        }
        else
        {
            var (planned, ok) = PlannedGroundStep(colony.Projection, StampPacking(player, colony.Projection), census.Floors, _reviewer.ClearFloors(colony.Projection));
            if (!ok)
            {
                return Dump(census);
            }
            step = PendingClearanceStep(planned);
            if (step.Roof.Count + step.Targets.Count + step.Floors.Count == 0)
            {
                return new RoundsClearanceResult { Verdict = BuildingReasonExistingWork, NativeWorkTicks = StockWaitTicks };
            }
            (prefix, actions) = GroundStepMethod(id, step);
        }
        var (verdict, admitted) = AdmitSubject(call, p.Journal, prefix, StandardMethodPlans(goal.History, goal.Standard.Episode), state.Snapshot);
        if (!admitted)
        {
            return new RoundsClearanceResult { Verdict = verdict };
        }
        var methodId = NextMethodId(prefix, HistoryMethodIds(goal.History, goal.Standard.Episode));
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, methodId, spec, call);
        return new RoundsClearanceResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }

    private RoundsClearanceResult Dump(ClearanceCensus census)
    {
        if (PolicyApi.PendingChunks(census.Chunks).Count == 0)
        {
            return new RoundsClearanceResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "chunk_haul_chunks") };
        }
        return new RoundsClearanceResult { Verdict = NoSpace("chunk_dump_site") };
    }
}

internal static partial class BuildingRuntimePackage
{
    /// <summary>The step with the already designated targets and floors dropped.</summary>
    internal static GroundStep PendingClearanceStep(GroundStep step) =>
        step with
        {
            Targets = step.Targets.Where(t => !t.Designated).ToList(),
            Floors = step.Floors.Where(f => !f.Designated).ToList(),
        };

    internal static (GroundStep Step, bool Ok) PlannedGroundStep(ColonyProjection colony, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, RoomFloors wants)
    {
        if (!colony.LayoutPlan.TryGetValue(out var plan))
        {
            return (default!, false);
        }
        var (ground, known) = ColonyGround(colony);
        if (!known)
        {
            return (default!, false);
        }
        return PolicyApi.PlannedGroundStep(plan, ground, player, floors, ColonyRooms(colony), RetiredGround(colony), wants);
    }

    internal static IReadOnlyList<string> PlannedGroundWork(ColonyProjection colony, IReadOnlyList<ClearanceTarget> player, IReadOnlyList<ClearanceFloor> floors, RoomFloors wants)
    {
        if (!colony.LayoutPlan.TryGetValue(out var plan))
        {
            return [];
        }
        var (ground, known) = ColonyGround(colony);
        if (!known)
        {
            return [];
        }
        return PolicyApi.PlannedGroundWork(plan, ground, player, floors, ColonyRooms(colony), RetiredGround(colony), wants);
    }

    /// <summary>Stamps the packable flag and the in-use flag (a bed with owners or a bench with an active bill) onto the rows.</summary>
    internal static List<ClearanceTarget> StampPacking(IReadOnlyList<ClearanceTarget> rows, ColonyProjection colony)
    {
        var inUse = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (colony.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            foreach (var bed in sleeping.Beds)
            {
                inUse[bed.Id] = (inUse.TryGetValue(bed.Id, out var was) && was) || bed.Owners.Count > 0;
            }
        }
        if (colony.ProductionBenches.TryGetValue(out var benches))
        {
            foreach (var bench in benches)
            {
                foreach (var bill in bench.Bills)
                {
                    if (bill.Active.TryGetValue(out var active) && active)
                    {
                        inUse[bench.Id] = true;
                    }
                }
            }
        }
        var output = new List<ClearanceTarget>(rows.Count);
        foreach (var row in rows)
        {
            output.Add(row with
            {
                Packable = colony.Packable.TryGetValue(row.DefName, out var packable) && packable,
                InUse = inUse.TryGetValue(row.EntityId, out var used) && used,
            });
        }
        return output;
    }

    internal static RoomObservation ColonyRooms(ColonyProjection colony)
    {
        colony.Rooms.TryGetValue(out var rooms);
        return rooms!;
    }

    internal static (string Prefix, IReadOnlyList<DomainAction> Actions) GroundStepMethod(PlanId id, GroundStep step)
    {
        string prefix;
        if (step.Phase == PolicyApi.GroundFurniture)
        {
            prefix = $"deconstruct-{step.Targets[0].EntityId}-";
        }
        else if (step.Phase == PolicyApi.GroundPack)
        {
            prefix = BatchPrefix("pack", step.Targets[0].EntityId, step.Targets.Count);
        }
        else if (step.Phase == PolicyApi.GroundDoors)
        {
            prefix = $"swap-door-{step.Targets[0].EntityId}-";
        }
        else if (step.Phase == PolicyApi.GroundWalls)
        {
            if (step.Roof.Count > 0 && step.Targets.Count == 0)
            {
                prefix = string.Create(CultureInfo.InvariantCulture, $"roof-off-{step.Roof[0].X}-{step.Roof[0].Z}-x{step.Roof.Count}-");
            }
            else if (!step.Ground.IsEmpty)
            {
                var b = step.Ground.Bounds();
                prefix = string.Create(CultureInfo.InvariantCulture, $"ground-walls-{b.X}-{b.Z}-");
            }
            else
            {
                prefix = BatchPrefix("ground-walls", step.Targets[0].EntityId, step.Targets.Count);
            }
        }
        else
        {
            var c = step.Floors[0].Cell;
            prefix = string.Create(CultureInfo.InvariantCulture, $"ground-floors-{c.X}-{c.Z}-x{step.Floors.Count}-");
        }
        return (prefix, GroundActions(id, step));
    }

    internal static string BatchPrefix(string kind, string first, int n) =>
        n > 1 ? string.Create(CultureInfo.InvariantCulture, $"{kind}-{first}-x{n}-") : $"{kind}-{first}-";

    internal static IReadOnlyList<DomainAction> GroundActions(PlanId id, GroundStep step)
    {
        var actions = new List<DomainAction>();
        ActionId Next() => new(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}"));
        if (step.Roof.Count > 0)
        {
            actions.Add(DomainAction.NewRemoveRoofAction(Next(), new RemoveRoof(step.Roof)));
        }
        foreach (var target in step.Targets)
        {
            if (step.Phase == PolicyApi.GroundPack)
            {
                var move = new MoveBuilding(target.EntityId, target.DefName, target.Minimum, Rotation.South);
                actions.Add(DomainAction.NewUninstallBuildingAction(Next(), move));
                continue;
            }
            var value = new Deconstruction(target.EntityId, target.DefName, target.Minimum);
            if (step.Phase == PolicyApi.GroundDoors)
            {
                value = value.WithWallReplacement();
            }
            actions.Add(DomainAction.NewDeconstructionAction(Next(), value));
        }
        foreach (var floor in step.Floors)
        {
            actions.Add(DomainAction.NewFloorRemovalAction(Next(), new FloorRemoval(floor.DefName, floor.Cell)));
        }
        return actions;
    }
}
