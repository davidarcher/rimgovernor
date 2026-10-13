namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainPopulation's prisoner interaction method, staging the jail first when it is owed.</summary>
public sealed class RoundsPrisonerInteractionPlanner
{
    private readonly Rounder reviewer;
    private readonly RoundsBuildingPlanner? building;

    private RoundsPrisonerInteractionPlanner(Rounder reviewer, RoundsBuildingPlanner? building)
    {
        this.reviewer = reviewer;
        this.building = building;
    }

    public static RoundsPrisonerInteractionPlanner NewRoundsPrisonerInteractionPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsPrisonerInteractionPlanner: reviewer == null");
        }

        RoundsBuildingPlanner? building = null;
        if (reviewer.Native is IRoundsBuildingSource source)
        {
            building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = ConcernId.MaintainPopulation, Definition = "Wall" };
        }

        return new RoundsPrisonerInteractionPlanner(reviewer, building);
    }

    internal RoundsPrisonerInteractionResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainPopulation);
        if (!workable)
        {
            return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = BuildingRuntimePackage.StepScope(call, reviewer.Native);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = reviewer.ObserveOwned(call, reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var choice = PolicyFunctions.SelectPrisonerInteractionMethod(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, read.Projection.Facts.FoodDays, reviewer.Policy.Prisoners());
        switch (choice.Reason)
        {
            case PrisonerReason.NoDeficit:
                return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "prisoner_interaction") };
            case PrisonerReason.Unknown:
                return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.FieldUnavailable("prisoners") };
        }

        var (jail, handled) = StageJail(call, epoch, state, review, goal, expected);
        if (handled)
        {
            return jail;
        }

        var prefix = $"{choice.Interaction}-{choice.Pawn}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(call, p.Journal, goal, prefix, state.Snapshot);
        if (!ok)
        {
            return new RoundsPrisonerInteractionResult { Verdict = verdict };
        }

        var interaction = PrisonerInteraction.NewPrisonerInteraction(choice.Pawn, choice.Interaction);
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewPrisonerInteractionAction(new ActionId($"{id}-0"), interaction);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal (RoundsPrisonerInteractionResult Result, bool Handled) StageJail(CancellationToken call, CancellationToken epoch, ControlState state, Rounds review, StandardState goal, ObservationIdentity expected)
    {
        if (building is null)
        {
            return (default, false);
        }

        var reading = reviewer.ObserveRooms(call, reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>(), "Wall", "Door");
        var step = JailStep(reading.Projection);
        RoundsBuildingResult result = default;
        switch (step.Kind)
        {
            case JailKind.None:
                return (default, false);
            case JailKind.Reconcile:
            {
                var stock = BuildingRuntimePackage.NewPackedStock(reviewer.Native, BoundaryPackage.Identity(state.Snapshot));
                result = building.ReconcileRoom(call, epoch, state, review, goal, reading, stock, new RoomReconcile { Room = step.Room, Template = step.Template, Name = BuildingRuntimePackage.RoomName("jail", step.Room), Reason = "jail" });
                break;
            }

            case JailKind.Mark:
                result = new RoundsBuildingResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "jail_bed_method") };
                foreach (var bed in step.Marks)
                {
                    var one = MarkJailBed(call, epoch, state, goal, reading.Projection, bed);
                    if (one.Verdict == BuildingRuntimePackage.BuildingReasonAdmitted || result.Verdict.Is(Cause.MethodUsed))
                    {
                        result = one;
                    }
                }

                break;
        }

        if (result.Verdict.Is(Cause.MethodUsed) || result.Verdict.Is(Cause.ExistingWork) || result.Verdict.Is(Cause.NoSpace) || result.Verdict.Is(Cause.FieldUnavailable) || result.Verdict.Is(Cause.SharedAdmission))
        {
            return (default, false);
        }

        return (new RoundsPrisonerInteractionResult { Verdict = result.Verdict }, true);
    }

    internal static JailStep JailStep(ColonyProjection facts)
    {
        var (plan, rooms, known) = BuildingRuntimePackage.PlannedLayout(facts);
        var pk = facts.Facts.Prisoners.TryGetValue(out var prisoners);
        var sk = facts.Facts.Sleeping.TryGetValue(out var sleeping);
        var bk = facts.Facts.CurrentConstruction.TryGetValue(out var built);
        if (!known || !pk || !sk || !bk || !built!.Colony)
        {
            return new JailStep();
        }

        return PolicyFunctions.NextJailStep(plan, rooms, HeldPrisoners(prisoners!), sleeping!.Beds, built.Buildings);
    }

    internal RoundsBuildingResult MarkJailBed(CancellationToken call, CancellationToken epoch, ControlState state, StandardState goal, ColonyProjection facts, string bed)
    {
        var p = reviewer.Player;
        if (reviewer.Native is not IRoundsHospitalSource native)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.FieldUnavailable("hospital_source") };
        }

        var method = new MethodId("jail-mark-" + bed);
        try
        {
            p.Journal.LoadMethod(call, goal.Standard.Id, goal.Standard.Episode, method);
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "jail_bed_method") };
        }
        catch (NotFoundException)
        {
        }

        var (target, _) = native.ReadBedUseTarget(call, BoundaryPackage.Identity(state.Snapshot), bed);
        try
        {
            BoundaryPackage.Context(target.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("markJailBed: context", e);
        }

        if (target.Context.Tick < facts.Identity.Tick.Value)
        {
            throw new ControlException("markJailBed: target read older than the facts");
        }

        if (target.Prisoners)
        {
            return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "jail_bed_prisoners") };
        }

        var patch = BedPrisoners.NewBedPrisoners(bed);
        p.CommitBedPatch(call, epoch, state, goal, method, patch, null);
        return new RoundsBuildingResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted };
    }

    internal static int HeldPrisoners(IReadOnlyList<PrisonerFacts> prisoners)
    {
        var n = 0;
        foreach (var p in prisoners)
        {
            var hk = p.Prisoner.TryGetValue(out var held);
            var dk = p.Dead.TryGetValue(out var dead);
            if (hk && held && dk && !dead)
            {
                n++;
            }
        }

        return n;
    }
}

public readonly record struct RoundsPrisonerInteractionResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
