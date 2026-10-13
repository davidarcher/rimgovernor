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
            building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = PolicyConstants.MaintainPopulation, Definition = "Wall" };
        }

        return new RoundsPrisonerInteractionPlanner(reviewer, building);
    }

    internal async Task<RoundsPrisonerInteractionResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        var (goal, workable) = p.Journal.Workable(review, PolicyConstants.MaintainPopulation, call);
        if (!workable || goal is null)
        {
            return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var choice = PolicyFunctions.SelectPrisonerInteractionMethod(read.Projection.Facts.Prisoners, read.Projection.Facts.PrisonerColony, read.Projection.Facts.FoodDays, reviewer.Policy.Prisoners());
        switch (choice.Reason)
        {
            case PrisonerPlanReason.NoDeficit:
                return new RoundsPrisonerInteractionResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "prisoner_interaction") };
            case PrisonerPlanReason.Unknown:
                return new RoundsPrisonerInteractionResult { Verdict = Verdict.FieldUnavailable("prisoners") };
        }

        var (jail, handled) = await StageJailAsync(state, review, goal, expected, call, epoch).ConfigureAwait(false);
        if (handled)
        {
            return jail;
        }

        var prefix = $"{choice.Interaction}-{choice.Pawn}-";
        var (method, verdict, ok) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
        if (!ok)
        {
            return new RoundsPrisonerInteractionResult { Verdict = verdict };
        }

        var interaction = new PrisonerInteraction((Domain.PawnId)choice.Pawn, choice.Interaction ?? throw new ControlException("step: interaction missing"));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewPrisonerInteractionAction(new ActionId($"{id}-0"), interaction);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsPrisonerInteractionResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal async Task<(RoundsPrisonerInteractionResult Result, bool Handled)> StageJailAsync(ControlState state, Rounds review, StandardState goal, Identity expected, CancellationToken call, CancellationToken epoch)
    {
        if (building is null)
        {
            return (default, false);
        }

        var reading = await reviewer.ObserveRoomsAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), ["Wall", "Door"], call).ConfigureAwait(false);
        var step = JailStep(reading.Projection);
        var result = new RoundsBuildingResult();
        switch (step.Kind)
        {
            case JailStepKind.None:
                return (default, false);
            case JailStepKind.Reconcile:
            {
                var stock = new PackedStock(reviewer.Native, Boundary.Identity(state.Snapshot));
                var room = step.Room ?? throw new ControlException("stageJail: reconcile step without a room");
                result = await building.ReconcileRoomAsync(state, review, goal, reading, stock, new RoomReconcile { Room = room, Template = step.Template, Name = RoundsBurialPlanner.RoomName("jail", room), Reason = "jail" }, call, epoch).ConfigureAwait(false);
                break;
            }

            case JailStepKind.Mark:
                result = new RoundsBuildingResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "jail_bed_method") };
                foreach (var bed in step.Marks)
                {
                    var one = await MarkJailBedAsync(state, goal, reading.Projection, bed, call, epoch).ConfigureAwait(false);
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

    internal async Task<RoundsBuildingResult> MarkJailBedAsync(ControlState state, StandardState goal, ColonyProjection facts, string bed, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        if (reviewer.Native is not IRoundsHospitalSource native)
        {
            return new RoundsBuildingResult { Verdict = Verdict.FieldUnavailable("hospital_source") };
        }

        var method = new MethodId("jail-mark-" + bed);
        try
        {
            p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, method, call);
            return new RoundsBuildingResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "jail_bed_method") };
        }
        catch (NotFoundException)
        {
        }

        var (target, _) = await native.ReadBedUseTargetAsync(Boundary.Identity(state.Snapshot), bed, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(target.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("markJailBed: context", e);
        }

        if (target.Context is not { } targetContext || targetContext.Tick < facts.Identity.Tick.Value)
        {
            throw new ControlException("markJailBed: target read older than the facts");
        }

        if (target.Prisoners)
        {
            return new RoundsBuildingResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "jail_bed_prisoners") };
        }

        var patch = BedUse.NewBedPrisoners(bed);
        p.CommitBedPatch(state, goal, method, patch, null, call, epoch);
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

public readonly record struct RoundsPrisonerInteractionResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
