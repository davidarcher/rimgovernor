namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes EnsureMechCharger's building method: place a mech charger on free ground away from fields.</summary>
public sealed class RoundsMechChargerPlanner
{
    private readonly Rounder reviewer;
    private readonly IRoundsBuildingSource native;

    private RoundsMechChargerPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsMechChargerPlanner NewRoundsMechChargerPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(ConcernId.EnsureMechCharger))
        {
            throw new ControlException("NewRoundsMechChargerPlanner: reviewer, native or method disabled");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsMechChargerPlanner: native is not a rounds source");
        }

        return new RoundsMechChargerPlanner(reviewer, native);
    }

    internal RoundsBuildingResult Step(CancellationToken call, CancellationToken epoch, StepArbiter? arbiter)
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

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.EnsureMechCharger);
        if (!workable)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var started = reviewer.Clock.Now();
        var expected = BuildingRuntimePackage.StepScope(call, native);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = reviewer.ObserveRooms(call, (IRoundsSource)native, expected, claims);
        var f = read.Projection;
        if (!f.Facts.MechChargerOwed.TryGetValue(out var owed))
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.FieldUnavailable("mech_chargers") };
        }

        if (!owed)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        if (!f.Biotech.TryGetValue(out var biotech))
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.FieldUnavailable("biotech") };
        }

        var defs = BuildingRuntimePackage.MechChargerDefinitions(ref read);
        f = read.Projection;
        if (defs.Count == 0)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.FieldUnavailable("mech_charger_definition") };
        }

        var definition = defs[0].Name;
        if (BuildingRuntimePackage.DefinitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction))
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
        }

        var method = new MethodId($"mech-charger-{definition}-{biotech.Chargers.Count}");
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "mech_charger_method") };
        }
        catch (NotFoundException)
        {
        }

        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };

        (Preview Preview, StockObservation Stock, DomainAction Action) PreviewAt(Cell anchor)
        {
            var building = Building.NewBuilding(definition, anchor, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTier.Expand);
            var (got, _) = native.PreviewBuilding(call, action, snapshot);
            return (got.Preview, got.Stock, action);
        }

        if (!defs[0].Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.FieldUnavailable("mech_charger_size") };
        }

        var siteFacts = new MechChargerSiteFacts { Bounds = f.Bounds, Cells = f.Cells, FieldZones = new HashSet<string>(), Rooms = f.Rooms };
        if (f.FoodFields.TryGetValue(out var fields))
        {
            foreach (var field in fields)
            {
                siteFacts.FieldZones.Add(field.Id);
            }
        }

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
            var (pv, stock, action) = PreviewAt(anchor);
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

            var plan = PlanSpec.NewPlan(planId, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
            {
                throw new ControlException("step: session state changed or the read took too long");
            }

            var decision = BuildingRuntimePackage.AdmitMethod(call, p.Journal, new BuildingMethodRequest
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
            });
            if (!decision.Admitted)
            {
                return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.AdmissionRefused(decision), Decision = decision };
            }

            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Decision = decision };
        }

        if (unknown)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.FieldUnavailable("mech_charger_preview") };
        }

        return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.NoSpace("charger_cell") };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal static IReadOnlyList<PlanningDefinition> MechChargerDefinitions(ref RoundsReading read)
    {
        if (read.Frame.Catalog is null)
        {
            return [];
        }

        var names = read.Frame.Catalog.MechChargers();
        read = read with { Projection = read.Projection.AddDefinitions(read.Frame, names) };
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

    internal static bool DefinitionIntentStanding(IReadOnlyList<PlanningDefinition> defs, Fact<IReadOnlyList<ConstructionClaim>> claims, Fact<CurrentConstruction> observed)
    {
        if (!observed.TryGetValue(out var census) || !census.Colony)
        {
            return false;
        }

        var charger = defs.Select(d => d.Name).ToHashSet();
        claims.TryGetValue(out var history);
        foreach (var claim in history ?? [])
        {
            if (charger.Contains(claim.Building.Definition) && PolicyFunctions.WorkOpen(claim.Building, observed) == BuildingWorkState.Open)
            {
                return true;
            }
        }

        return false;
    }
}
