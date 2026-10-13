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

    internal async Task<RoundsRecoveryResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
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

        var (incident, found) = BuildingRuntimePackage.IncidentDeficit(p.Journal, review, PolicyFunctions.RecoverDisasterServices, call);
        if (!found)
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        var open = new List<PlanState>();
        var applied = new List<RecoveryService>();
        foreach (var method0 in incident.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(plan0))
            {
                open.Add(plan0);
            }

            for (var i = 0; i < plan0.Spec.Actions.Count; i++)
            {
                if (plan0.Spec.Actions[i].RecoveryService is { } svc && i < plan0.Progress.Count && plan0.Progress[i].View.Stage == Stage.Completed)
                {
                    applied.Add(svc);
                }
            }
        }

        var expected = await BuildingRuntimePackage.StepScopeAsync(reviewer.Native, call).ConfigureAwait(false);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var read = await reviewer.ObserveOwnedAsync(reviewer.Native, expected, Unknown<EquatableArray<ConstructionClaim>>(), call).ConfigureAwait(false);
        var facts = read.Projection.Facts;
        var emergency = EmergencySnapshot.New(state.Snapshot, expected.Tick, read.Emergency);
        facts = facts with { Hostiles = PolicyFunctions.EmergencyNeeds(emergency, state.Snapshot, expected.Tick).Hostiles };
        facts = facts with { DangerSeeds = BuildingRuntimePackage.DangerSeedFact(facts.Hostiles, read.Emergency.Threats) };
        facts.DangerSeeds.TryGetValue(out var seeds);
        facts = facts with
        {
            DangerWindow = reviewer.SafeArea.DangerWindow(BuildingRuntimePackage.StockpileWorld(state.Snapshot), facts.Hostiles, seeds, expected.Tick),
            DangerHaulers = PolicyFunctions.DangerHaulers(read.Projection.WorkPawns),
            Tick = expected.Tick,
            Dash = Known(review.Latches.Dash),
        };
        var world = new StoreWorld(state.Snapshot.Colony, state.Snapshot.Load, state.Snapshot.Map);
        facts = facts with { ShelterCombatants = ShelterCombatants(p.Journal, world, call) };
        var changes = PolicyFunctions.PlanSheltering(facts);
        read.Projection.WorkPawns.TryGetValue(out var workers);

        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: session state changed");
        }

        foreach (var plan1 in open)
        {
            BuildingRuntimePackage.CancelStaleAreaActions(p.Journal, plan1, changes, workers, call);
            var updated = p.Journal.LoadPlan(plan1.Spec.Id, call);
            if (RecoveryWorkBlocksProtection(updated.Progress, review.Immediate))
            {
                return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        if (changes.Count > 0)
        {
            return CommitAreaChange(arbiter, state.Snapshot, incident, changes, workers, started, call, epoch);
        }

        if (review.Immediate)
        {
            return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var service in applied)
        {
            if (!PolicyFunctions.RecoveryWorkContinuing(service, facts.RecoveryBuildings, read.Projection.WorkPawns).TryGetValue(out var continuing))
            {
                return new RoundsRecoveryResult { Verdict = Verdict.FieldUnavailable("recovery_work") };
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
            if (candidate.Kind == RecoveryProposalKind.ServiceWork)
            {
                chosen = candidate;
                break;
            }
        }

        if (chosen is null)
        {
            return new RoundsRecoveryResult { Verdict = RecoverySelectionVerdict(selection.Reason) };
        }

        if (!arbiter.TryClaim([chosen.Pawn]))
        {
            return new RoundsRecoveryResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "recovery_pawn_claim") };
        }

        var id = PlanIds.MintPlanId();
        var serviceMethod = RecoveryServiceMethodOf(chosen.Method) ?? throw new ControlException("step: unknown recovery method");
        var recovery = new RecoveryService((Domain.PawnId)chosen.Pawn, chosen.Building, serviceMethod);
        var action = DomainAction.NewRecoveryServiceAction(new ActionId($"{id}-0"), recovery);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitIncidentMethod(incident.Incident.Id, chosen.ID, "", plan, call);
        return new RoundsRecoveryResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal static Verdict RecoverySelectionVerdict(RecoverySelectionReason reason) => reason switch
    {
        RecoverySelectionReason.FactsUnknown => Verdict.FieldUnavailable("recovery_observations"),
        RecoverySelectionReason.NoWorker => Verdict.NoWorker("recovery_service"),
        RecoverySelectionReason.TrackedMissing => Verdict.SiteBlocked("recovery_infrastructure", reason.ToString()),
        RecoverySelectionReason.NoWork => BuildingRuntimePackage.BuildingReasonNoDeficit,
        _ => Verdict.WaitFor(Cause.MethodUsed, "recovery_service_proposal"),
    };

    internal static Fact<EquatableArray<PawnId>> ShelterCombatants(StoreJournal journal, StoreWorld world, CancellationToken ct)
    {
        var fights = journal.OpenCombatFights(ct);
        var output = new List<PawnId>();
        foreach (var (_, fight) in fights)
        {
            if (fight.World != world)
            {
                continue;
            }

            output.AddRange(fight.Roster.Select(k => (PawnId)k));
        }

        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return Known<EquatableArray<PawnId>>([.. output]);
    }

    internal static bool RecoveryWorkBlocksProtection(IReadOnlyList<Progress> progress, bool immediate)
    {
        if (!immediate)
        {
            return Standard.WorkOpen(progress);
        }

        foreach (var p in progress)
        {
            var a = p.Action;
            var work = a.WorkAssignment;
            var animal = a.Husbandry;
            if (((work is { HasArea: true }) || (animal is { Method: HusbandryMethod.AllowedArea })) && Standard.WorkOpen([p]))

            {
                return true;
            }
        }

        return false;
    }
}

public readonly record struct RoundsRecoveryResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
