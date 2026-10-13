namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsRescueSource
{
    (EmergencyObservation Emergency, BridgeResult Result) ReadEmergency(CancellationToken ct, C.Identity identity);

    (O.ListPawnsReply Reply, BridgeResult Result) ReadCombatPawns(CancellationToken ct, C.Identity identity, IReadOnlyList<string> ids);
}

/// <summary>Composes CriticalMedicine's rescue: pairs a downed colonist with a capable rescuer.</summary>
public sealed class RoundsRescuePlanner
{
    private readonly Rounder reviewer;
    private readonly IRoundsRescueSource native;

    private RoundsRescuePlanner(Rounder reviewer, IRoundsRescueSource native)
    {
        this.reviewer = reviewer;
        this.native = native;
    }

    public static RoundsRescuePlanner NewRoundsRescuePlanner(Rounder reviewer, IRoundsRescueSource native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsRescuePlanner: reviewer == null || native == null");
        }

        return new RoundsRescuePlanner(reviewer, native);
    }

    internal RoundsRescueResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsRescueResult { Verdict = BuildingRuntimePackage.BuildingReasonDisabled };
        }

        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: observation unknown or snapshot invalid");
        }

        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsRescueResult { Verdict = BuildingRuntimePackage.BuildingReasonNoReview };
        }

        var (incident, found) = BuildingRuntimePackage.IncidentDeficit(call, p.Journal, review, ConcernId.CriticalMedicine);
        if (!found)
        {
            return new RoundsRescueResult { Verdict = BuildingRuntimePackage.BuildingReasonNoDeficit };
        }

        if (BuildingRuntimePackage.IncidentOpenWork(call, p.Journal, incident))
        {
            return new RoundsRescueResult { Verdict = BuildingRuntimePackage.BuildingReasonExistingWork };
        }

        var started = reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var (emergency, _) = native.ReadEmergency(call, identity);
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
            return new RoundsRescueResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "colonist_census") };
        }

        var ids = emergency.Facts.Colonists.Select(c => c.Id).ToList();
        var (reply, _) = native.ReadCombatPawns(call, identity, ids);
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

        var rescuers = new List<RescuerFacts>();
        var patients = new List<RescuePatientFacts>();
        var seen = new HashSet<string>();
        foreach (var row in observed.Pawns)
        {
            if (row?.Pawn is null || !seen.Add(row.Pawn.Id))
            {
                throw new ControlException("step: missing or duplicate pawn row");
            }

            var pawn = new PawnId(row.Pawn.Id);
            rescuers.Add(RescueFacts.NewRescuerFacts(pawn, row, ""));
            patients.Add(RescueFacts.NewRescuePatientFacts(pawn, row, ""));
        }

        var (rescuer, patient, ok) = PolicyFunctions.SelectRescue(rescuers, patients);
        if (ok && !arbiter.TryClaim([rescuer, patient]))
        {
            ok = false;
        }

        if (!ok)
        {
            return new RoundsRescueResult { Verdict = BuildingRuntimePackage.WaitFor(Cause.MethodUsed, "rescue_pairing"), NativeWorkTicks = BuildingRuntimePackage.MedicalWaitTicks };
        }

        var rescue = Rescue.NewRescue(rescuer, patient);
        var prefix = $"rescue-{patient}-";
        var (verdict, admitted) = BuildingRuntimePackage.AdmitSubject(call, p.Journal, prefix, BuildingRuntimePackage.IncidentPlans(incident.Methods, prefix), state.Snapshot);
        if (!admitted)
        {
            return new RoundsRescueResult { Verdict = verdict, NativeWorkTicks = BuildingRuntimePackage.MedicalWaitTicks };
        }

        var method = BuildingRuntimePackage.NextMethodId(prefix, BuildingRuntimePackage.IncidentMethodIds(incident.Methods));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewRescueAction(new ActionId($"{id}-0"), rescue);
        var plan = PlanSpec.NewPlan(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > reviewer.MaxAge)
        {
            throw new ControlException("step: session state changed or the read took too long");
        }

        p.Journal.CommitIncidentMethod(call, incident.Incident.Id, method, "", plan);
        return new RoundsRescueResult { Verdict = BuildingRuntimePackage.BuildingReasonAdmitted, Plan = id };
    }
}

public readonly record struct RoundsRescueResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }

    public uint NativeWorkTicks { get; init; }
}
