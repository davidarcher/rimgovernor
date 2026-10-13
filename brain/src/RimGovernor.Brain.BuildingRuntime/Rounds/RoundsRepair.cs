namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsRepairSource : IColonySource
{
    Task<(EmergencyObservation Emergency, BridgeResult Result)> ReadEmergencyAsync(C.Identity identity, CancellationToken ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);
}

/// <summary>Composes MaintainEssentialRepairs: pairs a worn structure with a colonist who can repair it.</summary>
public sealed class RoundsRepairPlanner
{
    private readonly Rounder reviewer;
    private readonly IRoundsRepairSource native;

    private RoundsRepairPlanner(Rounder reviewer, IRoundsRepairSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsRepairPlanner NewRoundsRepairPlanner(Rounder reviewer, IRoundsRepairSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsRepairPlanner: reviewer == null || native == null");
        }

        return new RoundsRepairPlanner(reviewer, native);
    }

    internal async Task<RoundsRepairResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native.Value == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native unset");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(review, PolicyFunctions.MaintainEssentialRepairs, call);
        if (goal is not null)
        {
            BuildingRuntimePackage.CancelSettledRepairMethods(p.Journal, goal, call);
        }

        if (!workable || goal is null)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(method0.Plan, call);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var (identity, _) = await native.IdentityAsync(call).ConfigureAwait(false);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var reading = await reviewer.ObserveColonyAsync(native, expected, null, call).ConfigureAwait(false);
        var upkeepReview = PolicyFunctions.ReviewUpkeep(reading.Projection.Facts.Upkeep, new UpkeepHistory(), null);
        EquatableArray<string> targetIds = [];
        foreach (var need in upkeepReview.Needs)
        {
            if (need.Concern == PolicyFunctions.MaintainEssentialRepairs && need.Targets.TryGetValue(out var targets))
            {
                targetIds = targets;
            }
        }

        if (targetIds.Count == 0)
        {
            return new RoundsRepairResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "essential_repairs") };
        }

        var byId = new Dictionary<string, UpkeepStructure>();
        if (reading.Projection.Facts.Upkeep.Structures.TryGetValue(out var rows))
        {
            foreach (var row in rows)
            {
                byId[row.Id] = row;
            }
        }

        var structures = new List<UpkeepStructure>();
        foreach (var id0 in targetIds)
        {
            if (byId.TryGetValue(id0, out var structure0))
            {
                structures.Add(structure0);
            }
        }

        if (structures.Count > 8)
        {
            structures = structures.GetRange(0, 8);
        }

        var identityRef = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await native.ReadEmergencyAsync(identityRef, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: emergency context", e);
        }

        if (emergency.Context is not { } emergencyContext || emergencyContext.Tick < review.Tick.Value)
        {
            throw new ControlException("step: emergency read older than the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsRepairResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "colonist_census") };
        }

        var ids = emergency.Facts.Colonists.Select(c => c.Id.Value).ToList();
        var (reply, _) = await native.ReadTendPawnsAsync(identityRef, ids, call).ConfigureAwait(false);
        var observed = reply.Observed ?? throw new ControlException("step: observed == null");
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
            throw new ControlException("step: observed pawn count differs from the request");
        }

        var pawns = new List<RepairCandidateFacts>();
        var seen = new HashSet<string>();
        foreach (var row in observed.Pawns)
        {
            if (row?.Pawn is null || !seen.Add(row.Pawn.Id))
            {
                throw new ControlException("step: missing or duplicate pawn row");
            }

            pawns.Add(RepairCandidateFactsOf(new PawnId(row.Pawn.Id), row));
        }

        var (structure, pawn, ok) = PolicyFunctions.SelectRepair(structures, pawns);
        if (ok && !arbiter.TryClaim([pawn], "repair-structure:" + structure.Id))
        {
            ok = false;
        }

        if (!ok)
        {
            return new RoundsRepairResult { Verdict = Verdict.WaitFor(Cause.MethodUsed, "repair_pairing") };
        }

        var repair = new Repair((Domain.PawnId)pawn, structure.Id, structure.Cell);
        var prefix = $"repair-{structure.Id}-";
        var (verdict, admitted) = BuildingRuntimePackage.AdmitSubject(p.Journal, prefix, BuildingRuntimePackage.StandardMethodPlans(goal.History, goal.Standard.Episode, prefix), state.Snapshot, call);
        if (!admitted)
        {
            return new RoundsRepairResult { Verdict = verdict };
        }

        var method = BuildingRuntimePackage.NextMethodId(prefix, BuildingRuntimePackage.HistoryMethodIds(goal.History, goal.Standard.Episode));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRepairAction(new ActionId($"{id}-0"), repair);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal static RepairCandidateFacts RepairCandidateFactsOf(PawnId pawn, O.PawnState row)
    {
        var facts = new RepairCandidateFacts
        {
            Pawn = pawn,
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Drafted = Boundary.FactBool(row.HasDrafted, row.Drafted),
            MentalState = Boundary.FactPresence(row.HasMentalState, row.Issues, "mental_state"),
        };
        if (row.Job is not null && !Boundary.IssueField(row.Job.Issues, "player_forced"))
        {
            facts = facts with { PlayerForced = Boundary.FactBool(row.Job.HasPlayerForced, row.Job.PlayerForced) };
        }

        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health"))
        {
            facts = facts with { NeedsTend = Boundary.FactBool(health.HasNeedsTend, health.NeedsTend), Bleeding = Boundary.FactBool(health.HasBleeding, health.Bleeding) };
        }

        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "work"))
        {
            facts = facts with { ConstructionEnabled = ConstructionWorkEnabled(settings.Work) };
        }

        return facts;
    }

    internal static Fact<bool> ConstructionWorkEnabled(IReadOnlyList<O.WorkSetting> work)
    {
        foreach (var w in work)
        {
            if (w is null || w.DefName != "Construction")
            {
                continue;
            }

            if (!w.HasDisabled || !w.HasPriority)
            {
                return Unknown<bool>();
            }

            return Known(!w.Disabled && w.Priority > 0);

        }

        return Unknown<bool>();
    }
}

public readonly record struct RoundsRepairResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
