namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsRepairSource : IColonySource
{
    (EmergencyObservation Emergency, BridgeResult Result) ReadEmergency(CancellationToken ct, C.Identity identity);

    (O.ListPawnsReply Reply, BridgeResult Result) ReadTendPawns(CancellationToken ct, C.Identity identity, IReadOnlyList<string> ids);
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

    internal RoundsRepairResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid || state.Snapshot.Native == 0)
        {
            throw new ControlException("step: observation unknown, snapshot invalid or native unset");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || !review.Snapshot.Matches(state.Snapshot))
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (goal, workable) = p.Journal.Workable(call, review, ConcernId.MaintainEssentialRepairs);
        BuildingRuntimePackage.CancelSettledRepairMethods(call, p.Journal, goal);
        if (!workable)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        foreach (var method0 in goal.Methods)
        {
            var open = p.Journal.LoadPlan(call, method0.Plan);
            if (StorePackage.PlanOpen(open))
            {
                return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
            }
        }

        var (identity, _) = native.Identity(call);
        var expected = ObservationPackage.DecodeIdentity(identity);
        if (!BuildingRuntimePackage.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: boundary moved");
        }

        var started = reviewer.Clock.Now();
        var reading = reviewer.ObserveColony(call, native, expected, null);
        var upkeepReview = PolicyFunctions.ReviewUpkeep(reading.Projection.Facts.Upkeep, new UpkeepHistory(), null);
        IReadOnlyList<string> targetIds = [];
        foreach (var need in upkeepReview.Needs)
        {
            if (need.Concern == ConcernId.MaintainEssentialRepairs && need.Targets.TryGetValue(out var targets))
            {
                targetIds = targets;
            }
        }

        if (targetIds.Count == 0)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "essential_repairs") };
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
        var (emergency, _) = native.ReadEmergency(call, identityRef);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: emergency context", e);
        }

        if (emergency.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("step: emergency read older than the review");
        }

        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "colonist_census") };
        }

        var ids = emergency.Facts.Colonists.Select(c => c.Id).ToList();
        var (reply, _) = native.ReadTendPawns(call, identityRef, ids);
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
            return new RoundsRepairResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "repair_pairing") };
        }

        var repair = Repair.NewRepair(pawn, structure.Id, structure.Cell);
        var prefix = $"repair-{structure.Id}-";
        var (verdict, admitted) = BuildingRuntimePackage.AdmitSubject(call, p.Journal, prefix, BuildingRuntimePackage.StandardMethodPlans(goal.History, goal.Standard.Episode, prefix), state.Snapshot);
        if (!admitted)
        {
            return new RoundsRepairResult { Verdict = verdict };
        }

        var method = BuildingRuntimePackage.NextMethodId(prefix, BuildingRuntimePackage.HistoryMethodIds(goal.History, goal.Standard.Episode));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRepairAction(new ActionId($"{id}-0"), repair);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitMethod(call, goal.Standard.Id, goal.Revision, method, plan);
        return new RoundsRepairResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }

    internal static RepairCandidateFacts RepairCandidateFactsOf(PawnId pawn, O.PawnState row)
    {
        var facts = new RepairCandidateFacts
        {
            Pawn = pawn,
            Dead = Boundary.FactBool(row.Dead),
            Downed = Boundary.FactBool(row.Downed),
            Drafted = Boundary.FactBool(row.Drafted),
            MentalState = Boundary.FactPresence(row.MentalState, row.Issues, "mental_state"),
        };
        if (row.Job is not null && !Boundary.IssueField(row.Job.Issues, "player_forced"))
        {
            facts = facts with { PlayerForced = Boundary.FactBool(row.Job.PlayerForced) };
        }

        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health"))
        {
            facts = facts with { NeedsTend = Boundary.FactBool(health.NeedsTend), Bleeding = Boundary.FactBool(health.Bleeding) };
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

            if (w.Disabled is null || w.Priority is null)
            {
                return Unknown<bool>();
            }

            return Known(!w.Disabled.Value && w.Priority.Value > 0);
        }

        return Unknown<bool>();
    }
}

public readonly record struct RoundsRepairResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}
