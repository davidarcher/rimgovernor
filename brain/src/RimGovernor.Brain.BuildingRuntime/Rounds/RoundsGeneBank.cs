namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainGeneBank's building method: place a gene bank beside the gene assemblers.</summary>
public sealed partial class RoundsGeneBankPlanner
{
    internal Rounder Reviewer { get; init; } = null!;

    internal IRoundsBuildingSource Native { get; init; } = null!;

    public static RoundsGeneBankPlanner NewRoundsGeneBankPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(ConcernId.MaintainGeneBank))
        {
            throw new ControlException("NewRoundsGeneBankPlanner: reviewer, native or method disabled");
        }

        if (native is not IRoundsSource)
        {
            throw new ControlException("NewRoundsGeneBankPlanner: native is not a rounds source");
        }

        return new RoundsGeneBankPlanner { Reviewer = reviewer, Native = native };
    }
}

internal static partial class BuildingRuntimePackage
{
    internal const int GeneBankSiteTries = 8;

    internal static IReadOnlyList<PlanningDefinition> GeneBankDefinitions(ref RoundsReading read)
    {
        if (read.Frame.Catalog is null)
        {
            return [];
        }

        var names = read.Frame.Catalog.GeneBanks();
        read = read with { Projection = read.Projection.AddDefinitions(read.Frame, names) };
        var output = new List<PlanningDefinition>();
        foreach (var d in read.Projection.Definitions)
        {
            if (d.Available.TryGetValue(out var available) && available && names.Contains(d.Name))
            {
                output.Add(d);
            }
        }

        return output;
    }

    internal static (Cell Anchor, bool Found) GeneBankAnchor(ColonyProjection facts, BiotechColony biotech)
    {
        if (biotech.GeneAssemblers.Count > 0)
        {
            return (biotech.GeneAssemblers[0].Position, true);
        }

        if (biotech.GeneBanks.Count > 0)
        {
            return (biotech.GeneBanks[0].Position, true);
        }

        if (!facts.Center().TryGetValue(out var center))
        {
            return (default, false);
        }

        return RoomAnchor(facts, PlannedRole.Workshop, center);
    }
}

public sealed partial class RoundsGeneBankPlanner
{
    internal RoundsBuildingResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = Reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainGeneBank);
        if (!workable)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(plan0))
            {
                return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
            }
        }

        var started = Reviewer.Clock.Now();
        var expected = StepScope(call, Native);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var claims = p.Journal.ConstructionClaims(call, state.Snapshot, expected.Tick);
        var read = Reviewer.ObserveRooms(call, (IRoundsSource)Native, expected, claims);
        var f = read.Projection;
        if (!f.Facts.GeneBankOwed.TryGetValue(out var owed))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("gene_banks") };
        }

        if (!owed)
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonNoDeficit };
        }

        if (!f.Biotech.TryGetValue(out var biotech))
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("biotech") };
        }

        var defs = GeneBankDefinitions(ref read);
        f = read.Projection;
        if (defs.Count == 0)
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("gene_bank_definition") };
        }

        var definition = defs[0].Name;
        if (DefinitionIntentStanding(defs, f.Facts.ConstructionClaims, f.Facts.CurrentConstruction))
        {
            return new RoundsBuildingResult { Verdict = BuildingReasonExistingWork };
        }

        var method = new MethodId($"gene-bank-{definition}-{biotech.GeneBanks.Count}");
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "gene_bank_method") };
        }
        catch (NotFoundException)
        {
        }

        if (!defs[0].Size.TryGetValue(out var size) || size.Width <= 0 || size.Height <= 0)
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("gene_bank_size") };
        }

        var planId = PlanIds.MintPlanId();
        var snapshot = state.Snapshot with { Plan = planId, Revision = new PlanRevision(1) };

        (Preview Preview, StockObservation Stock, DomainAction Action) PreviewAt(Cell at)
        {
            var building = Building.NewBuilding(definition, at, Rotation.North, "");
            var action = DomainAction.NewBuildingAction(new ActionId(planId.Value + "-0"), building, ConstructionTier.Expand);
            var (got, _) = Native.PreviewBuilding(call, action, snapshot);
            return (got.Preview, got.Stock, action);
        }

        var (anchor, planned) = GeneBankAnchor(f, biotech);
        if (!planned)
        {
            return new RoundsBuildingResult { Verdict = BuildingNoLayoutPlan };
        }

        var sites = PolicyFunctions.FreeSites(new FreeSiteRequest { Bounds = f.Bounds, Anchor = anchor, Cells = f.Cells, Light = f.LightTerrains }, size.Width, size.Height);
        var unknown = false;
        for (var i = 0; i < sites.Count; i++)
        {
            if (i == GeneBankSiteTries)
            {
                break;
            }

            var site = sites[i];
            var at = PolicyFunctions.AnchorForRect(site, new Cell(size.Width, size.Height), Rotation.North);
            var (pv, stock, action) = PreviewAt(at);
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

            if (made || !legal || !safe || !reachable || !FootprintIsRect(footprint!, site))
            {
                continue;
            }

            var plan = PlanSpec.NewPlan(planId, new PlanRevision(1), [action]);
            p.Current(call, epoch);
            var elapsed = Reviewer.Clock.Now() - started;
            if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > Reviewer.MaxAge)
            {
                throw new ControlException("step: session state changed or the read took too long");
            }

            var decision = AdmitMethod(call, p.Journal, new BuildingMethodRequest
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
                return new RoundsBuildingResult { Verdict = AdmissionRefused(decision), Decision = decision };
            }

            return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted, Decision = decision };
        }

        if (unknown)
        {
            return new RoundsBuildingResult { Verdict = FieldUnavailable("gene_bank_preview") };
        }

        return new RoundsBuildingResult { Verdict = NoSpace("gene_bank_cell") };
    }
}
