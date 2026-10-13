using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsSupplySource
{
    Task<(SupplyRead Read, BridgeResult Result)> ReadAllowSuppliesAsync(C.Identity identity, Cell cell, CancellationToken ct);
}

/// <summary>The forbid variant of the supply read, offered by a source that can forbid as well as allow.</summary>
public interface IRoundsSupplyForbidSource
{
    Task<(SupplyRead Read, BridgeResult Result)> ReadForbidSuppliesAsync(C.Identity identity, Cell cell, CancellationToken ct);
}

public sealed class RoundsSupplyResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }
    public PlanId Plan { get; init; }
}

public sealed class RoundsSupplyPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsSupplySource _native;

    private RoundsSupplyPlanner(Rounder reviewer, IRoundsSupplySource native)
    {
        _reviewer = reviewer;
        _native = native;
    }

    public static RoundsSupplyPlanner Create(Rounder? reviewer, IRoundsSupplySource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsSupplyPlanner: reviewer == nil || native == nil");
        }
        return new RoundsSupplyPlanner(reviewer, native);
    }

    internal Rounder Reviewer => _reviewer;

    internal async Task<RoundsSupplyResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsSupplyResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsSupplyResult { Verdict = BuildingReasonNoReview };
        }
        Sto.StandardState? goal = null;
        IReadOnlyList<Pol.StartingSupply> cohort = [];
        foreach (var binding in review.Standards)
        {
            if (binding.Concern != ManageSupplySafety)
            {
                continue;
            }
            var safety = p.Journal.LoadStandard(binding.Standard, call);
            goal = safety;
            if (safety.Deficit)
            {
                cohort = review.EventLoot.Pending;
                break;
            }
        }
        if (goal is null || goal.Standard.Id.Value == "" || !goal.Deficit || review.Veto(goal.Standard) != "")
        {
            return new RoundsSupplyResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            var rows = plan.Progress.ToList();
            for (var i = 0; i < rows.Count; i++)
            {
                var progress = rows[i];
                var v = progress.View;
                if (v.Unresolved || v.Stage is not (Stage.Pending or Stage.Prepared))
                {
                    continue;
                }
                if (progress.Action.SupplyAllow is not { } target)
                {
                    continue;
                }
                var keep = cohort.Any(row => row.Thing == target.Thing && row.Definition == target.Definition && row.Cell == target.Cell && row.Forbid == target.Forbidden);
                if (!keep)
                {
                    var cancelled = p.Journal.Cancel(plan.Spec.Id, progress.Action.Id, call);
                    rows[i] = cancelled;
                }
            }
            plan = plan with { Progress = [.. rows] };
            if (StorePackage.PlanOpen(plan))
            {
                return new RoundsSupplyResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var started = _reviewer.Clock.Now();
        // Each pending stack is read at the cell the review's census last saw it, one read per cell; a stack
        // that moved since is simply absent and waits for the next census to report its new cell.
        var pending = new Dictionary<string, Pol.StartingSupply>(StringComparer.Ordinal);
        var cells = new List<Cell>();
        foreach (var row in cohort)
        {
            pending[row.Thing] = row;
            cells.Add(row.Cell);
        }
        cells = [.. cells.OrderBy(c => c.Z).ThenBy(c => c.X).Distinct()];
        // Forbid unsafe items before releasing safe items.
        var forbidBatch = cohort.Any(row => row.Forbid);
        Func<C.Identity, Cell, CancellationToken, Task<(SupplyRead Read, BridgeResult Result)>> readSupply = _native.ReadAllowSuppliesAsync;
        if (forbidBatch)
        {
            if (_native is not IRoundsSupplyForbidSource forbidSource)
            {
                throw new ControlException("step: !ok");
            }
            readSupply = forbidSource.ReadForbidSuppliesAsync;
        }
        var targets = new List<SupplyAllow>();
        foreach (var cell in cells)
        {
            var (read, _) = await readSupply(Boundary.Identity(state.Snapshot), cell, call).ConfigureAwait(false);
            try
            {
                Boundary.Context(read.Context, state.Snapshot);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("step: err != nil || read.Context.GetTick() < int64(review.Tick)");
            }
            if ((read.Context?.Tick ?? 0) < review.Tick.Value)
            {
                throw new ControlException("step: err != nil || read.Context.GetTick() < int64(review.Tick)");
            }
            foreach (var target in read.Targets)
            {
                if (target.Supply.Cell != cell)
                {
                    throw new ControlException("step: target.Supply.Cell() != cell");
                }
                if (pending.TryGetValue(target.Supply.Thing, out var row) && row.Forbid == forbidBatch && row.Definition == target.Supply.Definition && row.Cell == cell)
                {
                    targets.Add(target.Supply);
                }
            }
        }
        if (targets.Count == 0)
        {
            return new RoundsSupplyResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "supply_targets") };
        }
        targets = [.. targets.OrderBy(t => t.Thing, StringComparer.Ordinal)];
        var text = new StringBuilder();
        foreach (var supply in targets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{supply.Thing}/{supply.Definition}/{supply.Cell.X}/{supply.Cell.Z}/{(supply.Forbidden ? "true" : "false")}\n");
        }
        var method2 = new MethodId(string.Create(CultureInfo.InvariantCulture, $"supply-{HashHex16(text.ToString())}-{review.Revision}"));
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < targets.Count; i++)
        {
            actions.Add(DomainAction.NewSupplyAllowAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), targets[i]));
        }
        var spec = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitOwnerMethod(goal, method2, "", spec, call);
        return new RoundsSupplyResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
