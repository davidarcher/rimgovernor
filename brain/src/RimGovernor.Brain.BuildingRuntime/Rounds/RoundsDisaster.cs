using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the disaster family (#3304). The read is the owned census with its emergency read (the
/// hostiles, the danger window and its haulers, the squad's draft set) in both passes, and the review's disaster
/// history. The disaster/sheltering step cancels the stale moves of its open plans and commits each owed
/// allowed-area move as its own Method; the disaster/services step (full pass only) waits on a running service and
/// otherwise commits the selection's first repair, breakdown or refuel service. It replaced the recovery planner.
/// </summary>
public sealed partial class DisasterRounds
{
    private readonly Rounder _reviewer;

    public DisasterRounds(Rounder reviewer)
    {
        if (reviewer is null || reviewer.Native is null)
        {
            throw new ControlException("DisasterRounds: reviewer == nil || reviewer.native == nil");
        }
        _reviewer = reviewer;
    }

    /// <summary>
    /// A critical entry, as the recovery planner was, woken by its area moves' and services' outcomes. The immediate
    /// pass watches only the sheltering Need's plans, so only the area moves are protection.
    /// </summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Critical,
        Kinds = [ActionKind.RecoveryService, ActionKind.WorkAssignment, ActionKind.Husbandry],
    };

    /// <summary>The family's scheduler entry.</summary>
    public FamilyRunner Runner() => new FamilyRunner<DisasterFacts>(new DisasterFamily(), ReadAsync, Schedule, WorkAsync);

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world, the same read in both passes: sheltering is a
    /// SeriousHarm Need and judged on a fresh census. Throws when the scope is another world or older than the review.
    /// </summary>
    internal async Task<DisasterFacts> ReadAsync(FamilyReadScope scope, CancellationToken call)
    {
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(call).ConfigureAwait(false);
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
        {
            throw new ControlException("disaster read: the scope is another world or older than the review");
        }
        var read = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var f = read.Projection.Facts;
        var emergency = EmergencySnapshot.New(scope.World, expected.Tick, read.Emergency);
        f = f with { Hostiles = EmergencyNeedsRules.EmergencyNeeds(emergency, scope.World, expected.Tick).Hostiles };
        f = f with { DangerSeeds = SafeAreaMemory.DangerSeedFact(f.Hostiles, read.Emergency.Threats) };
        f.DangerSeeds.TryGetValue(out var seeds);
        f = f with
        {
            DangerWindow = _reviewer.SafeArea.DangerWindow(RoundsStockpilePlanner.StockpileWorld(scope.World), f.Hostiles, seeds, expected.Tick),
            DangerHaulers = ShelterRules.DangerHaulers(read.Projection.WorkPawns),
            Tick = expected.Tick,
            Dash = Known(review.Latches.Dash),
            ShelterCombatants = ShelterCombatants(journal, new StoreWorld(scope.World.Colony, scope.World.Load, scope.World.Map), call),
        };
        return DisasterFacts.From(f, review.Disaster) with { Workers = read.Projection.WorkPawns };
    }

    /// <summary>The work step of a ready Need: the area moves, or (full pass only) a service.</summary>
    internal Task<Verdict> WorkAsync(NeedWork work, DisasterFacts facts, CancellationToken call)
    {
        if (work.Need.Id == DisasterFamily.Sheltering)
        {
            return Task.FromResult(ShelteringWork(work, facts, call));
        }
        if (work.Need.Id == DisasterFamily.Services && !work.Immediate)
        {
            return Task.FromResult(ServicesWork(work, facts, call));
        }
        return Task.FromResult(AwaitingPlan(work.Need.Id.Value, "no_method"));
    }

    /// <summary>
    /// Cancels the pending moves of the Need's open plans the policy no longer wants, then commits each owed move as
    /// its own Method (<see cref="DisasterFamily.AreaMethod"/>): an open move continues, and a move whose area claim
    /// another plan holds waits on it.
    /// </summary>
    internal Verdict ShelteringWork(NeedWork work, DisasterFacts facts, CancellationToken call)
    {
        if (!facts.Changes.TryGetValue(out var changes))
        {
            return FieldUnavailable("shelter_trigger");
        }
        var p = _reviewer.Player;
        facts.Workers.TryGetValue(out var workers);
        foreach (var method in work.Owner.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (StorePackage.PlanOpen(plan))
            {
                CancelStaleAreaActions(p.Journal, plan, changes, workers, call);
            }
        }
        p.Current(call, work.Epoch);
        if (p.Session.State() != work.State)
        {
            throw new ControlException("sheltering: the session moved");
        }
        var outcomes = new List<(ClaimOutcome Outcome, ClaimConflict? Conflict)>();
        Verdict? blocked = null;
        foreach (var change in changes)
        {
            if (!change.Animal && !workers.Any(worker => worker.Id == change.Pawn))
            {
                blocked ??= NoWorker("area_pawn");
                continue;
            }
            if (work.Arbiter is { } arbiter && !arbiter.TryClaim([change.Pawn]))
            {
                blocked ??= WaitFor(Cause.MethodUsed, "area_change_pawn_claim");
                continue;
            }
            var id = PlanIds.MintPlanId();
            var actionId = new ActionId($"{id}-0");
            var action = change.Animal
                ? DomainAction.NewHusbandryAction(actionId, new Husbandry(change.Pawn, HusbandryMethod.AllowedArea, change.Area))
                : DomainAction.NewWorkAssignmentAction(actionId, WorkAssignment.NewAreaAssignment(change.Pawn, change.Area == "", change.Area));
            var reason = "shelter: " + change.Pawn.Value + " to " + (change.Area.Length == 0 ? "unrestricted" : change.Area);
            var commit = p.Journal.CommitClaimedMethod(work.Need.Id, work.Owner.Snapshot, DisasterFamily.AreaMethod(change), reason, new PlanSpec(id, new PlanRevision(1), [action]), call);
            outcomes.Add((commit.Outcome, commit.Conflict));
        }
        return AdminRounds.OutcomeVerdict(outcomes) ?? blocked ?? BuildingReasonNoDeficit;
    }

    /// <summary>
    /// The disaster/services step: open or still-running service work waits; otherwise the selection over the census
    /// (skipping the Need's unretired MethodIds, as the review's readback does) gives the next service, committed
    /// under its candidate's MethodId and claiming its building.
    /// </summary>
    internal Verdict ServicesWork(NeedWork work, DisasterFacts facts, CancellationToken call)
    {
        var p = _reviewer.Player;
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != work.State.Snapshot || review.Immediate)
        {
            return BuildingReasonNoReview;
        }
        var applied = new List<RecoveryService>();
        foreach (var method in work.Owner.Methods)
        {
            var plan = p.Journal.LoadPlan(method.Plan, call);
            if (WorkProgress.Open(plan.Progress))
            {
                return BuildingReasonExistingWork;
            }
            for (var i = 0; i < plan.Spec.Actions.Count && i < plan.Progress.Count; i++)
            {
                if (plan.Spec.Actions[i].RecoveryService is { } service && plan.Progress[i].View.Stage == Stage.Completed)
                {
                    applied.Add(service);
                }
            }
        }
        foreach (var service in applied)
        {
            if (!RecoveryRules.RecoveryWorkContinuing(service, facts.Planning.Buildings, facts.Workers).TryGetValue(out var continuing))
            {
                return FieldUnavailable("recovery_work");
            }
            if (continuing)
            {
                return BuildingReasonExistingWork;
            }
        }
        var selection = RecoveryRules.SelectRecoveryMethods(facts.Planning, review.Disaster, StoreJournal.RecoveryMethodsUsed(work.Owner), review.Tick);
        selection.Validate();
        var chosen = selection.Candidates.FirstOrDefault(c => c.Kind == RecoveryProposalKind.ServiceWork);
        if (chosen is null)
        {
            return RecoverySelectionVerdict(selection.Reason);
        }
        if (work.Arbiter is { } arbiter && !arbiter.TryClaim([chosen.Pawn]))
        {
            return WaitFor(Cause.MethodUsed, "recovery_pawn_claim");
        }
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{id}-0"), new RecoveryService((Domain.PawnId)chosen.Pawn, chosen.Building, chosen.Method));
        p.Current(call, work.Epoch);
        if (p.Session.State() != work.State)
        {
            throw new ControlException("recovery service: the session moved");
        }
        var reason = "recovery: " + Wire.Name(chosen.Method) + " " + chosen.Building;
        var commit = p.Journal.CommitClaimedMethod(work.Need.Id, work.Owner.Snapshot, chosen.ID, reason, new PlanSpec(id, new PlanRevision(1), [action]), call);
        return AdminRounds.OutcomeVerdict([(commit.Outcome, commit.Conflict)]) ?? BuildingReasonAdmitted;
    }

    /// <summary>Why no service was selected, as the Need's verdict.</summary>
    internal static Verdict RecoverySelectionVerdict(RecoverySelectionReason reason) => reason switch
    {
        RecoverySelectionReason.FactsUnknown => FieldUnavailable("recovery_observations"),
        RecoverySelectionReason.NoWorker => NoWorker("recovery_service"),
        RecoverySelectionReason.TrackedMissing => SiteBlocked("recovery_infrastructure", reason.ToString()),
        RecoverySelectionReason.NoWork => BuildingReasonNoDeficit,
        _ => WaitFor(Cause.MethodUsed, "recovery_service_proposal"),
    };

    /// <summary>The colonists an open fight in <paramref name="world"/> has drafted: a threat shelters none of them.</summary>
    internal static Fact<EquatableArray<PawnId>> ShelterCombatants(StoreJournal journal, StoreWorld world, CancellationToken ct)
    {
        var output = new List<PawnId>();
        foreach (var (_, fight) in journal.OpenCombatFights(ct))
        {
            if (fight.World == world)
            {
                output.AddRange(fight.Roster.Select(k => (PawnId)k));
            }
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return Known<EquatableArray<PawnId>>([.. output]);
    }
}

partial class DisasterRounds
{
    /// <summary>Cancels the pending area actions of a plan whose change the policy no longer wants.</summary>
    internal static void CancelStaleAreaActions(StoreJournal journal, PlanState plan, IReadOnlyList<AllowedAreaChange> changes, IReadOnlyList<WorkPawn> workers, CancellationToken ct)
    {
        var actions = plan.Spec.Actions;
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var stage = plan.Progress[i].View.Stage;
            if (stage != Stage.Pending && stage != Stage.Prepared)
            {
                continue;
            }
            AllowedAreaChange candidate;
            if (action.WorkAssignment is { HasArea: true } work)
            {
                candidate = new AllowedAreaChange { Pawn = work.Pawn, Area = work.Area };
                var current = workers.Any(worker => worker.Id == candidate.Pawn);
                if (!current)
                {
                    candidate = candidate with { Pawn = default };
                }
            }
            else if (action.Husbandry is { Method: HusbandryMethod.AllowedArea } husbandry)
            {
                candidate = new AllowedAreaChange { Pawn = husbandry.Animal, Animal = true, Area = husbandry.Argument };
            }
            else
            {
                continue;
            }
            if (!changes.Contains(candidate))
            {
                journal.Cancel(plan.Spec.Id, action.Id, ct);
            }
        }
    }
}
