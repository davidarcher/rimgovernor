using System.Text;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Runtime;

public sealed partial class AdminReview
{
    /// <summary>The grave sites previewed per step, nearest first.</summary>
    private const int GraveSiteTries = 8;

    /// <summary>
    /// The admin/supply-safety work (#3304): cancels the pending or prepared supply actions of the Need's open
    /// Methods whose stack the cohort no longer lists, then reads the cohort's cells natively (one read per cell, at
    /// the cell the census last saw each stack) and commits one batch: the unsafe stacks to forbid first, else the
    /// safe forbidden stacks to allow. The MethodId names the batch's targets, so the same batch continues and a
    /// batch over other stacks is admitted beside it; a stack two batches share is refused as a claim conflict.
    /// </summary>
    private async Task<Verdict> SupplyStepAsync(NeedWork work, IReadOnlyList<StartingSupply> cohort, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = work.State;
        var review = await p.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return BuildingReasonNoReview;
        }
        foreach (var method in work.Owner.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            foreach (var progress in plan.Progress)
            {
                var v = progress.View;
                if (v.Unresolved || v.Stage is not (Stage.Pending or Stage.Prepared) || progress.Action.SupplyAllow is not { } target)
                {
                    continue;
                }
                if (!cohort.Any(row => row.Thing == target.Thing && row.Definition == target.Definition && row.Cell == target.Cell && row.Forbid == target.Forbidden))
                {
                    p.Journal.Cancel(plan.Spec.Id, progress.Action.Id, call);
                }
            }
        }
        var started = _reviewer.Clock.Now();
        var pending = new Dictionary<string, StartingSupply>(StringComparer.Ordinal);
        foreach (var row in cohort)
        {
            pending[row.Thing] = row;
        }
        var cells = cohort.Select(row => row.Cell).Distinct().OrderBy(c => c.Z).ThenBy(c => c.X).ToList();
        // Forbid unsafe items before releasing safe items.
        var forbidBatch = cohort.Any(row => row.Forbid);
        Func<RimGovernor.Brain.GameFacts.Common.Identity, Cell, CancellationToken, Task<SupplyRead>> readSupply = _supplies!.ReadAllowSuppliesAsync;
        if (forbidBatch)
        {
            if (_supplies is not GameClient forbidSource)
            {
                throw new ControlException("supply safety: the source cannot forbid");
            }
            readSupply = forbidSource.ReadForbidSuppliesAsync;
        }
        var targets = new List<SupplyAllow>();
        foreach (var cell in cells)
        {
            var read = await readSupply(Boundary.Identity(state.Snapshot), cell, call).ConfigureAwait(false);
            try
            {
                Boundary.Context(read.Context, state.Snapshot);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ControlException("supply safety: the supply read is from another world", e);
            }
            if ((read.Context?.Tick ?? 0) < review.Tick.Value)
            {
                throw new ControlException("supply safety: the supply read is older than the review");
            }
            foreach (var target in read.Targets)
            {
                if (target.Supply.Cell != cell)
                {
                    throw new ControlException("supply safety: a target is not at its read cell");
                }
                if (pending.TryGetValue(target.Supply.Thing, out var row) && row.Forbid == forbidBatch && row.Definition == target.Supply.Definition && row.Cell == cell)
                {
                    targets.Add(target.Supply);
                }
            }
        }
        if (targets.Count == 0)
        {
            return WaitFor(Cause.MethodUsed, "supply_targets");
        }
        targets = [.. targets.OrderBy(t => t.Thing, StringComparer.Ordinal)];
        var text = new StringBuilder();
        foreach (var supply in targets)
        {
            text.Append(CultureInfo.InvariantCulture, $"{supply.Thing}/{supply.Definition}/{supply.Cell.X}/{supply.Cell.Z}/{(supply.Forbidden ? "true" : "false")}\n");
        }
        var methodId = new MethodId("supply-" + AcquisitionPlanner.HashHex16(text.ToString()));
        var id = PlanIds.MintPlanId();
        var actions = new List<DomainAction>();
        for (var i = 0; i < targets.Count; i++)
        {
            actions.Add(DomainAction.NewSupplyAllowAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{i}")), targets[i]));
        }
        p.Current(call, work.Epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("supply safety: the session moved or the read took too long");
        }
        var commit = p.Journal.CommitClaimedMethod(work.Need.Id, work.Owner.Snapshot, methodId,
            forbidBatch ? "supply safety: forbid unsafe event loot" : "supply safety: allow safe event loot", new PlanSpec(id, new PlanRevision(1), actions), call);
        return OutcomeVerdict([commit]) ?? BuildingReasonAdmitted;
    }

    /// <summary>
    /// The admin/burial work (#3304): the tomb's room or the next plain grave while the tomb step is due, else the
    /// morgue's room. A room reconcile commits under the Need owner by the room's name; a grave is one Method per
    /// grave count (<c>grave-&lt;n&gt;</c>), so the same grave continues and is re-issued once released.
    /// </summary>
    private async Task<Verdict> BurialStepAsync(NeedWork work, CancellationToken call)
    {
        if (_building is null)
        {
            return FieldUnavailable("building_source");
        }
        var p = _reviewer.Player;
        var state = work.State;
        var review = await p.Journal.LoadReviewAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingPlanner.BuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("burial: the scope is another world or older than the review");
        }
        var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, BurialFacts.BurialDefinitions, call).ConfigureAwait(false);
        var step = BurialFacts.TombStep(reading.Reading);
        var stock = new PackedStock(_reviewer.Native, Boundary.Identity(state.Snapshot));
        switch (step.Kind)
        {
            case TombStepKind.None:
                var (morgue, owed) = BurialFacts.PlannedMorgue(reading.Reading);
                if (!owed)
                {
                    return BurialFacts.MorgueAwaitingRoom(reading.Reading) ? WaitFor(Cause.MethodUsed, "morgue_room") : BuildingReasonNoDeficit;
                }
                return (await _building.ReconcileRoomAsync(state, review, work.Owner, reading, stock, new RoomReconcile { Room = morgue, Name = SleepingUpkeepPlanner.RoomReconcileName("morgue", morgue), Reason = "morgue" }, call, work.Epoch).ConfigureAwait(false)).Verdict;
            case TombStepKind.Reconcile:
                return (await _building.ReconcileRoomAsync(state, review, work.Owner, reading, stock, new RoomReconcile { Room = step.Room, Template = step.Template, Name = SleepingUpkeepPlanner.RoomReconcileName("tomb", step.Room), Reason = "tomb" }, call, work.Epoch).ConfigureAwait(false)).Verdict;
            case TombStepKind.Grave:
                return await PlaceGraveAsync(work, reading.Reading, step, call).ConfigureAwait(false);
            case TombStepKind.Full:
                return WaitFor(Cause.MethodUsed, "tomb_room");
            case var invalid:
                throw new ControlException($"burial: invalid tomb step {invalid}");
        }
    }

    /// <summary>
    /// Places one plain grave on the nearest GraveSites site the native preview accepts, admitted as one building
    /// method per grave count; a grave blueprint or frame standing is the step in flight.
    /// </summary>
    private async Task<Verdict> PlaceGraveAsync(NeedWork work, ColonyReading f, TombStep step, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = work.State;
        var census = f.Facts.CurrentConstruction;
        if (census.Sites.Any(s => s.Building?.Definition == TombRules.GraveDefinition))
        {
            return BuildingReasonExistingWork;
        }
        if (f.Facts.LayoutPlan is not { } plan)
        {
            return BuildingReasonNoLayoutPlan;
        }
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"grave-{step.Graves}"));
        IReadOnlyList<Cell> traffic = [.. (f.Facts.Upkeep.Routes).Traffic.Select(t => t.Cell)];
        var sites = GraveSitingRules.GraveSites(new GraveSiteInput { Bounds = f.Facts.MapBounds, Cells = f.PlanningWindow.Cells, Light = f.Facts.LightTerrains, Plan = plan!, Traffic = traffic });
        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        var size = new Cell(1, 2);
        foreach (var site in sites.Take(GraveSiteTries))
        {
            var building = new Building(TombRules.GraveDefinition, InteriorRules.AnchorForRect(site, size, Rotation.North), Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTier.Expand);
            var got = await _building!.Native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            var pv = got.Preview;
            if (pv.MadeFromStuff || !pv.CanPlace || !pv.SafeToPlace || PreviewRules.WatchUnreachable(pv) || !BiotechReview.FootprintIsRect(pv.Footprint, site))
            {
                continue;
            }
            p.Current(call, work.Epoch);
            if (p.Session.State() != state)
            {
                throw new ControlException("burial: session state changed");
            }
            var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
            {
                Owner = work.Owner,
                Method = method,
                Plan = new PlanSpec(planId, new PlanRevision(1), [action]),
                Current = snapshot,
                Tick = f.Identity.Tick,
                Bounds = f.Facts.MapBounds,
                Stock = got.Stock,
                Previews = [pv],
                Purpose = Purpose.ReviewState,
            }, call).ConfigureAwait(false);
            return decision.Admitted ? BuildingReasonAdmitted : AdmissionLog.AdmissionRefused(decision);
        }
        return NoSpace("grave_cell");
    }
}
