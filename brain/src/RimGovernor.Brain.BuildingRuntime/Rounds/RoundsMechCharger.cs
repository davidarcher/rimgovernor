namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes EnsureMechCharger's building method: place a mech charger on free ground away from fields.</summary>
public sealed class RoundsMechChargerPlanner
{
    private readonly Rounder reviewer;
    internal Rounder Reviewer => reviewer;
    private readonly IRoundsBuildingSource native;

    private RoundsMechChargerPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsMechChargerPlanner NewRoundsMechChargerPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(PolicyFunctions.EnsureMechCharger))
        {
            throw new ControlException("NewRoundsMechChargerPlanner: reviewer, native or method disabled");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsMechChargerPlanner: native is not a rounds source");
        }

        return new RoundsMechChargerPlanner(reviewer, native);
    }

    internal async Task<RoundsBuildingResult> StepAsync(StepArbiter? arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.EnsureMechCharger, call);
        if (!workable || goal is null)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var started = reviewer.Clock.Now();
        var expected = await BuildingRuntimePackage.StepScopeAsync(native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        var read = await reviewer.ObserveRoomsAsync((IRoundsSource)native, expected, claims, call).ConfigureAwait(false);
        var f = read.Projection;
        if (!f.Facts.MechChargerOwed.TryGetValue(out var owed))
        {
            return new RoundsBuildingResult { Verdict = Verdict.FieldUnavailable("mech_chargers") };
        }

        if (!owed)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        if (!f.Biotech.TryGetValue(out var biotech))
        {
            return new RoundsBuildingResult { Verdict = Verdict.FieldUnavailable("biotech") };
        }

        var defs = BuildingRuntimePackage.MechChargerDefinitions(ref read);
        f = read.Projection;
        if (defs.Count == 0)
        {
            return new RoundsBuildingResult { Verdict = Verdict.FieldUnavailable("mech_charger_definition") };
        }

        var definition = defs[0].Name;
        if (BuildingRuntimePackage.DefinitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction))
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
        }

        var method = new MethodId($"mech-charger-{definition}-{biotech.Chargers.Count}");
        try
        {
            p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, method, call);
            return new RoundsBuildingResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "mech_charger_method") };
        }
        catch (NotFoundException)
        {
        }

        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };
        async Task<(Preview Preview, StockObservation Stock, DomainAction Action)> PreviewAtAsync(Cell anchor)
        {
            var building = new Building(definition, anchor, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTier.Expand);
            var (got, _) = await native.PreviewBuildingAsync(action, snapshot, call).ConfigureAwait(false);
            return (got.Preview, got.Stock, action);
        }

        if (!defs[0].Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return new RoundsBuildingResult { Verdict = Verdict.FieldUnavailable("mech_charger_size") };
        }

        var fieldZones = new Dictionary<string, bool>();
        if (f.FoodFields.TryGetValue(out var fields))
        {
            foreach (var field in fields)
            {
                fieldZones[field.Id] = true;
            }
        }

        var siteFacts = new MechChargerSiteFacts { Bounds = f.Bounds, Cells = f.Cells, FieldZones = fieldZones, Rooms = f.Rooms };

        var disposal = new List<Cell>();
        foreach (var atomizer in biotech.Atomizers)
        {
            disposal.Add(atomizer.Position);
        }

        siteFacts = siteFacts with { Disposal = disposal };
        var sites = PolicyFunctions.MechChargerSites(siteFacts, size.Width, size.Height);
        var unknown = false;
        foreach (var site in sites)
        {
            var anchor = PolicyFunctions.AnchorForRect(site.Site, new Cell(size.Width, size.Height), Rotation.North);
            var (pv, stock, action) = await PreviewAtAsync(anchor).ConfigureAwait(false);
            var fk = pv.Footprint.TryGetValue(out var footprint);
            var mk = pv.MadeFromStuff.TryGetValue(out var made);
            var lk = pv.CanPlace.TryGetValue(out var legal);
            var ssk = pv.SafeToPlace.TryGetValue(out var safe);
            var rk = pv.WatchCellsAccessible.TryGetValue(out var reachable);
            if (!fk || !mk || !lk || !ssk || !rk)
            {
                unknown = true;
                continue;
            }

            if (made || !legal || !safe || !reachable || !BuildingRuntimePackage.FootprintIsRect(footprint!, site.Site))
            {
                continue;
            }

            var plan = new PlanSpec(planId, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("step: session state changed or the read took too long");
            }

            var decision = await AdmissionLog.AdmitMethodAsync(p.Journal, new BuildingMethodRequest
            {
                Owner = goal,
                Method = method,
                Plan = plan,
                Current = snapshot,
                Tick = f.Identity.Tick,
                Bounds = Known(f.Bounds),
                Stock = stock,
                Previews = [pv],
                Purpose = Purpose.Rounds,
            }, call).ConfigureAwait(false);
            if (!decision.Admitted)
            {
                return new RoundsBuildingResult { Verdict = AdmissionLog.AdmissionRefused(decision), Decision = decision };
            }

            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Decision = decision };
        }

        if (unknown)
        {
            return new RoundsBuildingResult { Verdict = Verdict.FieldUnavailable("mech_charger_preview") };
        }

        return new RoundsBuildingResult { Verdict = Verdict.NoSpace("charger_cell") };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static IReadOnlyList<PlanningDefinition> MechChargerDefinitions(ref RoundsReading read)
    {
        if (read.Catalog is not { } catalog)
        {
            return [];
        }

        var names = catalog.MechChargers();
        read = read with { Reading = read.Reading with { Projection = read.Projection.AddDefinitions(read.Frame, catalog, names) } };
        var output = new List<PlanningDefinition>();
        foreach (var d in ObservationPackage.MechChargerDefs(read.Projection.Definitions))
        {
            if (d.Available.TryGetValue(out var available) && available)
            {
                output.Add(d);
            }
        }

        return output;
    }

    internal static bool FootprintIsRect(IReadOnlyList<Cell> footprint, Rectangle r)
    {
        if (footprint.Count != r.Width * r.Height)
        {
            return false;
        }

        foreach (var c in footprint)
        {
            if (c.X < r.X || c.X >= r.X + r.Width || c.Z < r.Z || c.Z >= r.Z + r.Height)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool DefinitionIntentStanding(IReadOnlyList<PlanningDefinition> defs, Fact<EquatableArray<ConstructionClaim>> claims, Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return false;
        }

        var charger = defs.Select(d => d.Name).ToHashSet();
        if (!claims.TryGetValue(out var history))
        {
            return false;
        }

        foreach (var claim in history)
        {
            if (claim.Building is { } building && charger.Contains(building.Definition) && PolicyFunctions.WorkOpen(building, observed) == BuildingWork.BuildingOpen)
            {
                return true;
            }
        }

        return false;
    }
}
