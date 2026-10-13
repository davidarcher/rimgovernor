namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Composes RecoverDisasterServices: sheltering changes first, then a repair, breakdown or refuel service.</summary>
public sealed partial class RoundsRecoveryPlanner
{
    private readonly Rounder reviewer;

    private RoundsRecoveryPlanner(Rounder reviewer) => this.reviewer = reviewer;

    public static RoundsRecoveryPlanner NewRoundsRecoveryPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsRecoveryPlanner: reviewer == null");
        }

        return new RoundsRecoveryPlanner(reviewer);
    }

    internal static Domain.RecoveryMethod? RecoveryServiceMethodOf(RecoveryMethod method) => method switch
    {
        RecoveryMethod.Repair => Domain.RecoveryMethod.Repair,
        RecoveryMethod.Breakdown => Domain.RecoveryMethod.Breakdown,
        RecoveryMethod.Refuel => Domain.RecoveryMethod.Refuel,
        _ => null,
    };

    internal RoundsRecoveryResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (incident, found) = BuildingRuntimePackage.IncidentDeficit(call, p.Journal, review, ConcernId.RecoverDisasterServices);
        if (!found)
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var open = new List<PlanState>();
        var applied = new List<RecoveryService>();
        foreach (var method0 in incident.Methods)
        {
            var plan0 = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(plan0))
            {
                open.Add(plan0);
            }

            for (var i = 0; i < plan0.Spec.Actions.Count; i++)
            {
                if (plan0.Spec.Actions[i].TryRecoveryService(out var svc) && i < plan0.Progress.Count && plan0.Progress[i].View().Stage == ProgressStage.Completed)
                {
                    applied.Add(svc);
                }
            }
        }

        var expected = BuildingRuntimePackage.StepScope(call, reviewer.Native);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = reviewer.ObserveOwned(call, reviewer.Native, expected, Unknown<IReadOnlyList<ConstructionClaim>>());
        var facts = read.Projection.Facts;
        var emergency = PolicyFunctions.NewEmergencySnapshot(state.Snapshot, expected.Tick, read.Emergency);
        facts = facts with { Hostiles = PolicyFunctions.EmergencyNeeds(emergency, state.Snapshot, expected.Tick).Hostiles };
        facts = facts with { DangerSeeds = BuildingRuntimePackage.DangerSeedFact(facts.Hostiles, read.Emergency.Threats) };
        facts.DangerSeeds.TryGetValue(out var seeds);
        facts = facts with
        {
            DangerWindow = reviewer.SafeArea.DangerWindow(BuildingRuntimePackage.StockpileWorld(state.Snapshot), facts.Hostiles, seeds ?? [], expected.Tick),
            DangerHaulers = PolicyFunctions.DangerHaulers(read.Projection.WorkPawns),
            Tick = expected.Tick,
            Dash = Known(review.Latches.Dash),
        };
        var world = new StoreWorld(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map);
        facts = facts with { ShelterCombatants = ShelterCombatants(call, p.Journal, world) };
        var changes = PolicyFunctions.PlanSheltering(facts);
        read.Projection.WorkPawns.TryGetValue(out var workers);
        workers ??= [];
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: session state changed");
        }

        foreach (var plan1 in open)
        {
            BuildingRuntimePackage.CancelStaleAreaActions(call, p.Journal, plan1, changes, workers);
            var updated = p.Journal.LoadPlan(call, plan1.Spec.Id);
            if (RecoveryWorkBlocksProtection(updated.Progress, review.Immediate))
            {
                return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        if (changes.Count > 0)
        {
            return CommitAreaChange(call, epoch, arbiter, state.Snapshot, incident, changes, workers, started);
        }

        if (review.Immediate)
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var service in applied)
        {
            if (!PolicyFunctions.RecoveryWorkContinuing(service, facts.RecoveryBuildings, read.Projection.WorkPawns).TryGetValue(out var continuing))
            {
                return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.FieldUnavailable("recovery_work") };
            }

            if (continuing)
            {
                return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var planning = new RecoveryPlanning { Safety = facts.RecoverySafety, Workers = facts.RecoveryWorkers, Buildings = facts.RecoveryBuildings };
        var seen = incident.Methods.Select(m => m.Method).ToList();
        var selection = PolicyFunctions.SelectRecoveryMethods(planning, review.Disaster, seen, review.Tick);
        selection.Validate();
        RecoveryCandidate? chosen = null;
        foreach (var candidate in selection.Candidates)
        {
            if (candidate.Kind == RecoveryCandidateKind.ServiceProposal)
            {
                chosen = candidate;
                break;
            }
        }

        if (chosen is null)
        {
            return new RoundsRecoveryResult { Verdict = RecoverySelectionVerdict(selection.Reason) };
        }

        if (!arbiter.TryClaim([new PawnId(chosen.Pawn)]))
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "recovery_pawn_claim") };
        }

        var id = PlanIds.MintPlanId();
        var serviceMethod = RecoveryServiceMethodOf(chosen.Method) ?? throw new ControlException("step: unknown recovery method");
        var recovery = RecoveryService.NewRecoveryService(new PawnId(chosen.Pawn), chosen.Building, serviceMethod);
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{id}-0"), recovery);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitIncidentMethod(call, incident.Incident.Id, chosen.Id, "", plan);
        return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal static Verdict RecoverySelectionVerdict(RecoverySelectionReason reason) => reason switch
    {
        RecoverySelectionReason.FactsUnknown => BuildingRuntimePackage.FieldUnavailable("recovery_observations"),
        RecoverySelectionReason.NoWorker => BuildingRuntimePackage.NoWorker("recovery_service"),
        RecoverySelectionReason.TrackedMissing => BuildingRuntimePackage.SiteBlocked("recovery_infrastructure", reason.ToString()),
        RecoverySelectionReason.NoWork => BuildingRuntimePackage.BuildingReasonNoDeficit,
        _ => BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "recovery_service_proposal"),
    };

    internal static Fact<IReadOnlyList<PawnId>> ShelterCombatants(CancellationToken ct, StoreJournal journal, StoreWorld world)
    {
        var fights = journal.OpenCombatFights(ct);
        var output = new List<PawnId>();
        foreach (var fight in fights)
        {
            if (fight.World != world)
            {
                continue;
            }

            output.AddRange(fight.Roster.Keys.Select(k => new PawnId(k)));
        }

        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return Known<IReadOnlyList<PawnId>>(output);
    }

    internal static bool RecoveryWorkBlocksProtection(IReadOnlyList<Progress> progress, bool immediate)
    {
        if (!immediate)
        {
            return DomainPackage.StandardWorkOpen(progress);
        }

        foreach (var p in progress)
        {
            var a = p.Action;
            var assignment = a.TryWorkAssignment(out var work);
            var husbandry = a.TryHusbandry(out var animal);
            if (((assignment && work!.HasArea) || (husbandry && animal!.Method == HusbandryMethod.AllowedArea)) && DomainPackage.StandardWorkOpen([p]))
            {
                return true;
            }
        }

        return false;
    }
}

public readonly record struct RoundsRecoveryResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
