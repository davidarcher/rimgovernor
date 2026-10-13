namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes MaintainPopulation's custody methods: rescue, capture, arrest, lance, containment and tend.</summary>
public sealed partial class RoundsPopulationCustodyPlanner
{
    private readonly Rounder reviewer;
    private readonly IRoundsCustodySource native;

    private RoundsPopulationCustodyPlanner(Rounder reviewer, IRoundsCustodySource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsPopulationCustodyPlanner NewRoundsPopulationCustodyPlanner(Rounder reviewer, IRoundsCustodySource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsPopulationCustodyPlanner: reviewer or native is missing");
        }

        return new RoundsPopulationCustodyPlanner(reviewer, native);
    }

    internal async Task<RoundsPopulationCustodyResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyConstants.MaintainPopulation, call);
        if (!workable || goal is null)
        {
            return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var choice = PolicyFunctions.SelectCustodyMethod(read.Projection.Facts.Custody);
        var arrestTarget = PolicyFunctions.ShrineArrestTarget(read.Projection.Facts);
        if (arrestTarget.Value != "")
        {
            choice = new CustodyChoice { Pawn = (PawnId)arrestTarget };
        }
        else
        {
            if (PolicyFunctions.LanceCandidate(read.Projection.Facts) is { } lanceTarget && lanceTarget.Value != "")
            {
                var (lance, handled) = await StepLanceAsync(p, state, started, goal, review.Tick, lanceTarget, arbiter, call, epoch).ConfigureAwait(false);
                if (handled)
                {
                    return lance;
                }
            }
        }

        if (choice.Reason == CustodyPlanReason.NoDeficit)
        {
            if (PolicyFunctions.EntityCaptureTarget(read.Projection.Facts.Containment) is { } entity)
            {
                choice = new CustodyChoice { Pawn = (PawnId)entity, Decision = CustodyDecision.Capture };
            }
        }

        if (choice.Reason == CustodyPlanReason.NoDeficit)
        {
            return await StepContainmentAsync(p, state, started, goal, read.Projection.Facts, arbiter, call, epoch).ConfigureAwait(false);
        }

        if (choice.Reason == CustodyPlanReason.Unknown)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.FieldUnavailable("custody") };
        }

        var identityCtx = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await native.ReadEmergencyAsync(identityCtx, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: emergency context", e);
        }

        if (emergency.Context is not { } emergencyContext || emergencyContext.Tick.GetValueOrDefault() < review.Tick.Value)
        {
            throw new ControlException("step: emergency read older than the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "colonists_complete") };
        }

        var ids = new List<string>(emergency.Facts.Colonists.Count + 1);
        foreach (var pawn in emergency.Facts.Colonists)
        {
            if (new PawnId(pawn.Id.Value) == choice.Pawn)
            {
                continue; // the custody target is never its own performer
            }

            ids.Add(pawn.Id.Value);
        }

        ids.Add(choice.Pawn.Value);
        var (reply, _) = await native.ReadCombatPawnsAsync(identityCtx, ids, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed ?? throw new ControlException("step: observed == nil");
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: observed context", e);
        }

        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("step: pawn count differs from the requested ids");
        }

        var needed = (await DraftNeeds.PlannedDraftsAsync(p.Journal, call).ConfigureAwait(false)).Select(id => new Domain.PawnId(id.Value)).ToHashSet();
        var arms = await BuildingRuntimePackage.ReadArmamentAsync(native, identityCtx, call).ConfigureAwait(false);
        var catalog = arms.Catalog;
        var squad = new List<ShrineDefenderFacts>();
        var performers = new List<RescuerFacts>();
        var profiles = new List<PawnProfile>();
        O.PawnState? patient = null;
        var seen = new HashSet<string>();
        foreach (var row in observed.Pawns)
        {
            if (row is null || row.Pawn is null || !seen.Add((row.Pawn.Id ?? "")))
            {
                throw new ControlException("step: row missing or repeated");
            }

            var pawn = new PawnId((row.Pawn.Id ?? ""));
            if (pawn == choice.Pawn)
            {
                patient = row;
                continue;
            }

            var ranged = BuildingRuntimePackage.RangedWeaponEquipped(row.Equipment, arms);
            squad.Add((ShrineDefenderFacts)BuildingRuntimePackage.SquadDefenderFactsFrom(new ShrineDefenderFacts(), row, needed, ranged));
            performers.Add(RescueFacts.NewRescuerFacts(pawn, row, ""));
            var work = ObservationPackage.WorkPawnRow(row, catalog, arms.Things);
            profiles.Add(PolicyFunctions.BuildProfile(work));
        }

        if (patient is null)
        {
            throw new ControlException("step: patient == nil");
        }

        if (arrestTarget.Value != "")
        {
            return CommitArrest(p, state, started, goal, read.Projection.Facts, squad, arrestTarget, arbiter, call, epoch);
        }

        switch (choice.Decision)
        {
            case CustodyDecision.Rescue:
            {
                var patients = new List<RescuePatientFacts> { RescueFacts.NewRescuePatientFacts(choice.Pawn, patient, "") };
                var (performer, target, ok) = PolicyFunctions.SelectRescue(performers, patients);
                if (ok && !arbiter.TryClaim([performer, target]))
                {
                    ok = false;
                }

                if (!ok)
                {
                    return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "rescue_claim") };
                }

                var value = new Domain.Rescue(new Domain.PawnId(performer.Value), new Domain.PawnId(target.Value));
                var prefix = $"population-rescue-{target}-";
                var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
                if (!admitted)
                {
                    return new RoundsPopulationCustodyResult { Verdict = verdict };
                }

                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewRescueAction(new ActionId($"{id}-0"), value);
                return Commit(p, state, started, goal, method, id, action, call, epoch);
            }

            case CustodyDecision.Capture:
            {
                var patients = new List<CapturePatientFacts> { CaptureFacts.NewCapturePatientFacts(choice.Pawn, patient, "") };
                var warden = PolicyFunctions.WardenFor(profiles, false) ?? default;
                if (PolicyFunctions.SelectCapture(performers, patients, warden) is not { } pair || !arbiter.TryClaim([pair.Capturer, pair.Patient]))
                {
                    return new RoundsPopulationCustodyResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "capture_claim") };
                }

                var (performer, target) = pair;

                var value = new Domain.Capture(new Domain.PawnId(performer.Value), new Domain.PawnId(target.Value));
                var prefix = $"population-capture-{target}-";
                var (method, verdict, admitted) = BuildingRuntimePackage.AdmitStandardMethod(p.Journal, goal, prefix, state.Snapshot, call);
                if (!admitted)
                {
                    return new RoundsPopulationCustodyResult { Verdict = verdict };
                }

                var id = PlanIds.MintPlanId();
                var action = DomainAction.NewCaptureAction(new ActionId($"{id}-0"), value);
                return Commit(p, state, started, goal, method, id, action, call, epoch);
            }

            default:
                throw new ControlException("step: unknown custody decision");
        }
    }

    internal RoundsPopulationCustodyResult Commit(Player p, ControlState state, DateTimeOffset started, StandardState goal, MethodId method, PlanId id, DomainAction action, CancellationToken call, CancellationToken epoch)
    {
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("commit: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsPopulationCustodyResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}

public readonly record struct RoundsPopulationCustodyResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
