namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class RoundsBuildingPlanner
{
    internal const uint RefrigerationCoolingTicks = 2 * GameTime.TicksPerDay;
    internal const uint RefrigerationCoolingWindowTicks = GameTime.TicksPerHour;

    public static RoundsBuildingPlanner NewRoundsRefrigerationPlanner(Rounder reviewer, IRoundsBuildingSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(ConcernId.MaintainRefrigeration))
        {
            throw new ControlException("NewRoundsRefrigerationPlanner: reviewer, native or method disabled");
        }

        if (native is not IRoundsSource || native is not IRefrigerationSource)
        {
            throw new ControlException("NewRoundsRefrigerationPlanner: native lacks the rounds or refrigeration source");
        }

        return new RoundsBuildingPlanner { Reviewer = reviewer, Native = native, Concern = ConcernId.MaintainRefrigeration, Definition = "Cooler" };
    }

    internal (RoundsBuildingPlanner? Planner, Verdict Verdict) SelectRefrigeration(CancellationToken ct, ColonyProjection facts, RoundsLatches latches, bool allowance)
    {
        var review = PolicyFunctions.ReviewRefrigeration(facts.Facts.FoodStorageUpkeep, latches.Refrigeration, Reviewer.Policy.FoodStorage);
        review = review.WithWarmRooms(WarmCoolingRooms(facts));
        if (!review.Active)
        {
            return (null, BuildingReasonNoDeficit);
        }

        var (coolers, _) = ObservationPackage.ReadRefrigerationCoolers(ct, (IRefrigerationSource)Native, facts.Identity, facts.PowerPlanning);
        var fact = ObservationPackage.RefrigerationFacts(facts, coolers);
        var proposal = PolicyFunctions.SelectRefrigerationMethod(review, fact, Reviewer.Policy.FoodStorage, allowance);
        switch (proposal.Method)
        {
            case RefrigerationMethod.Build:
            case RefrigerationMethod.SetTarget:
                var resolved = Copy();
                resolved.Refrigeration = proposal;
                resolved.Definition = "Cooler";
                return (resolved, default);
            case RefrigerationMethod.Unknown:
                return (null, FieldUnavailable("refrigeration"));
            case RefrigerationMethod.NoMethod:
                return (null, BuildingReasonNoDeficit);
            default:
                return (null, AwaitingMethod(proposal.Method));
        }
    }

    internal static (uint Allowance, bool Exhausted, bool Lent) RefrigerationOutputAllowance(CancellationToken ct, StoreJournal journal, StandardState goal, GenerationSnapshot current, Tick tick, Tick since)
    {
        var methods = journal.LoadOwnerMethods(ct, goal);
        if (methods.Count == 0)
        {
            var (latchAllowance, latchExhausted) = RefrigerationLatchAllowance(since, tick);
            return (latchAllowance, latchExhausted, true);
        }

        uint allowance = 0;
        var exhausted = false;
        foreach (var method in methods)
        {
            var plan = journal.LoadPlan(ct, method.Plan);
            var (remaining, completed) = RefrigerationNativeWorkTicks(plan, current, tick);
            allowance = Math.Max(allowance, remaining);
            exhausted = exhausted || (completed && remaining == 0);
        }

        return (allowance, exhausted && allowance == 0, false);
    }

    internal static (uint Allowance, bool Exhausted) RefrigerationLatchAllowance(Tick since, Tick tick)
    {
        if (since.Value <= 0 || tick < since)
        {
            return (0, false);
        }

        if (tick.Value - since.Value >= RefrigerationCoolingTicks)
        {
            return (0, true);
        }

        return (Math.Min(RefrigerationCoolingWindowTicks, (uint)(RefrigerationCoolingTicks - (tick.Value - since.Value))), false);
    }

    internal static (uint Remaining, bool Completed) RefrigerationNativeWorkTicks(PlanState plan, GenerationSnapshot current, Tick tick)
    {
        if (plan.Progress.Count != 1)
        {
            return (0, false);
        }

        var progress = plan.Progress[0];
        var action = progress.Action;
        if (action.TryBuilding(out var building))
        {
            if (building!.Definition != "Cooler")
            {
                return (0, false);
            }
        }
        else if (!action.TryBuildingTemperature(out _))
        {
            return (0, false);
        }

        current = current with { Plan = plan.Spec.Id, Revision = plan.Spec.Revision };
        var v = progress.View();
        current = current with { Native = v.Snapshot.Native };
        if (v.Stage != ProgressStage.Completed || v.Unresolved || !v.Effect.TryGetValue(out var effect) || effect != EffectKind.Completed || !v.Snapshot.Matches(current) || tick < v.Tick)
        {
            return (0, false);
        }

        if (tick.Value - v.Tick.Value >= RefrigerationCoolingTicks)
        {
            return (0, true);
        }

        return (Math.Min(RefrigerationCoolingWindowTicks, (uint)(RefrigerationCoolingTicks - (tick.Value - v.Tick.Value))), true);
    }

    internal (IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict) PreviewRefrigeration(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check)
    {
        if (Refrigeration is null || Refrigeration.Method != RefrigerationMethod.Build)
        {
            throw new ControlException("previewRefrigeration: no refrigeration build proposal");
        }

        return PreviewWallBuilding(ct, snapshot, facts, protectedCells, check, "Build", Refrigeration.Cell, Refrigeration.Rotation, false);
    }

    internal (IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict) PreviewWallBuilding(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection facts, IReadOnlyList<Cell> protectedCells, Action check, string def, Cell cell, Rotation rotation, bool overRock)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        if (protectedCells.Contains(cell))
        {
            return (null!, stock, BuildingReasonExistingWork);
        }

        if (facts.DefinitionAvailable(def).TryGetValue(out var available) && !available)
        {
            return (null!, stock, FieldUnavailable(def + "_research"));
        }

        var building = Building.NewBuilding(def, cell, rotation, "");
        var (previews, planned, reason) = PreviewPlannedBuilding(ct, snapshot, facts, building, 0, overRock, false);
        if (!reason.IsZero)
        {
            return (null!, planned, reason);
        }

        previews[0].Footprint.TryGetValue(out var footprint);
        if (footprint is null || footprint.Count != 1 || footprint[0] != cell)
        {
            return (null!, planned, NoSpace(def + "_wall_footprint"));
        }

        return (previews, planned, default);
    }

    internal (IReadOnlyList<Preview> Previews, StockObservation Stock, Verdict Verdict) PreviewPlannedBuilding(CancellationToken ct, GenerationSnapshot snapshot, ColonyProjection facts, Building building, int index, bool overRock, bool goOpen)
    {
        var stock = new StockObservation { Snapshot = snapshot, Tick = facts.Identity.Tick };
        var action = DomainAction.NewBuildingAction(new ActionId($"{snapshot.Plan}-{index}"), building, PolicyFunctions.PlannerTier(Concern, Phase));
        BuildingPreview preview;
        if (overRock)
        {
            if (Native is not IOverRockPreviewer source)
            {
                return (null!, stock, FieldUnavailable("over_rock_preview"));
            }

            (preview, _) = source.PreviewBuildingOverRock(ct, action, snapshot);
        }
        else
        {
            (preview, _) = Native.PreviewBuilding(ct, action, snapshot);
        }

        var p = preview.Preview;
        var fk = p.Footprint.TryGetValue(out var footprint);
        var mk = p.MadeFromStuff.TryGetValue(out var made);
        var lk = p.CanPlace.TryGetValue(out var legal);
        var sk = p.SafeToPlace.TryGetValue(out var safe);
        if (!fk || !mk || !lk || !sk)
        {
            return (null!, stock, FieldUnavailable(building.Definition + "_preview"));
        }

        if (goOpen && !legal && p.Blockers.Count == 0)
        {
            throw new ControlException($"rock step: native refused {building.Definition} at {building.Cell} that the frame lists open and names no blocker");
        }

        if (made != (building.Stuff != "") || !footprint!.Contains(building.Cell) || !legal || !safe)
        {
            return (null!, stock, NoSpace("refrigeration_site"));
        }

        MergeRoundsStock(ref stock, preview.Stock, true);
        return ([p], stock, default);
    }

    internal RoundsBuildingResult CommitRefrigerationTarget(CancellationToken call, StandardState goal, Action check)
    {
        var p = Reviewer.Player;
        var proposal = Refrigeration;
        if (proposal is null || proposal.Method != RefrigerationMethod.SetTarget)
        {
            throw new ControlException("commitRefrigerationTarget: no set-target proposal");
        }

        var method = proposal.Key;
        try
        {
            p.Journal.LoadOwnerMethod(call, goal, method);
            return new RoundsBuildingResult { Verdict = WaitFor(Cause.MethodUsed, "refrigeration_target") };
        }
        catch (NotFoundException)
        {
        }

        var patch = BuildingTemperature.NewBuildingTemperature(proposal.Cooler, proposal.TargetC);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewBuildingTemperatureAction(new ActionId($"{id}-0"), patch);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        check();
        p.Journal.CommitOwnerMethod(call, goal, method, "", plan);
        return new RoundsBuildingResult { Verdict = BuildingReasonAdmitted };
    }
}
