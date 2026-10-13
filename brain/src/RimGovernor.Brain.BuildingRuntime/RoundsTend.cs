namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsTendSource
{
    (Bridge.EmergencyObservation Observation, BridgeResult Result) ReadEmergency(CancellationToken ct, CommonPb.Identity identity);

    (ObservationsPb.ListPawnsReply Reply, BridgeResult Result) ReadTendPawns(CancellationToken ct, CommonPb.Identity identity, IReadOnlyList<string> ids);
}

public sealed class RoundsTendResult
{
    public Verdict Verdict { get; init; } = new();
    public PlanId Plan { get; init; }

    /// <summary>A bounded window the step may lend when the CriticalMedical deficit stands but no tend method can run (MedicalWaitTicks).</summary>
    public uint NativeWorkTicks { get; init; }
}

public sealed class RoundsTendPlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsTendSource _native;

    private RoundsTendPlanner(Rounder reviewer, IRoundsTendSource native)
    {
        _reviewer = reviewer;
        _native = native;
    }

    public static RoundsTendPlanner Create(Rounder? reviewer, IRoundsTendSource? native)
    {
        if (reviewer is null || native is null)
        {
            throw new ControlException("NewRoundsTendPlanner: reviewer == nil || native == nil");
        }
        return new RoundsTendPlanner(reviewer, native);
    }

    internal RoundsTendResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsTendResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid())
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsTendResult { Verdict = BuildingReasonNoReview };
        }
        var (incident, found) = IncidentDeficit(call, p.Journal, review, CriticalMedicine);
        if (!found)
        {
            return new RoundsTendResult { Verdict = BuildingReasonNoDeficit };
        }
        if (IncidentOpenWork(call, p.Journal, incident!))
        {
            return new RoundsTendResult { Verdict = BuildingReasonExistingWork };
        }
        var started = _reviewer.Clock.Now();
        var identity = BoundaryApi.Identity(state.Snapshot);
        var (emergency, _) = _native.ReadEmergency(call, identity);
        try
        {
            BoundaryApi.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (emergency.Context.Tick < review.Tick.Value)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsTendResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "colonist_census") };
        }
        var ids = emergency.Facts.Colonists.Select(pawn => pawn.Id.Value).ToList();
        var (reply, _) = _native.ReadTendPawns(call, identity, ids);
        var observed = reply.Observed;
        if (observed is null)
        {
            throw new ControlException("step: observed == nil");
        }
        try
        {
            BoundaryApi.Context(observed.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: err != nil");
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException("step: len(observed.Pawns) != len(ids)");
        }
        var threatened = ThreatenedPatients(emergency.Facts);
        var doctors = new List<Pol.TendDoctorFacts>();
        var patients = new List<Pol.TendPatientFacts>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in observed.Pawns)
        {
            if (row is null || row.Pawn is null || !seen.Add(row.Pawn.Id))
            {
                throw new ControlException("step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]");
            }
            var pawn = new PawnId(row.Pawn.Id);
            doctors.Add(Tend.TendFacts.NewTendDoctorFacts(pawn, row, ""));
            var patientFacts = Tend.TendFacts.NewTendPatientFacts(pawn, row, "") with { Threatened = threatened.GetValueOrDefault(pawn) };
            patients.Add(patientFacts);
        }
        var (doctor, patient, ok) = SelectTend(doctors, patients, Tend.TendFacts.TendReachability(observed.Pawns));
        if (ok && !arbiter.TryClaim([doctor, patient]))
        {
            ok = false;
        }
        if (!ok)
        {
            // No pair to order: the patient is up and out of bed, or every doctor is ineligible, busy or walled
            // off from the patients. Only game time changes that, so the step lends a window rather than
            // reporting no work.
            return new RoundsTendResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "medical_pair"), NativeWorkTicks = MedicalWaitTicks };
        }
        var tend = new Domain.Tend(doctor, patient);
        // Keyed by patient and attempt count, not doctor: a fresh attempt after an interrupted or failed try
        // picks whichever doctor is currently best, which is how doctor replacement happens across cycles.
        var prefix = $"tend-{patient.Value}-";
        var (verdict, admitted) = AdmitSubject(call, p.Journal, prefix, IncidentPlans(incident!.Methods, prefix), state.Snapshot);
        if (!admitted)
        {
            // Native refused this patient's treatment and the refusal still stands; the deficit stays visible
            // and the clock must still advance under it.
            return new RoundsTendResult { Verdict = verdict, NativeWorkTicks = MedicalWaitTicks };
        }
        var method = NextMethodId(prefix, IncidentMethodIds(incident.Methods));
        var id = PlanIds.MintPlanId();
        var action = DomainAction.NewTendAction(new ActionId($"{id}-0"), tend);
        var plan = new PlanSpec(id, new PlanRevision(1), [action]);
        p.Current(call, epoch);
        var elapsed = _reviewer.Clock.Now() - started;
        if (p.Session.State() != state || elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("step: p.session.State() != state || elapsed < 0 || elapsed > r.reviewer.maxAge");
        }
        p.Journal.CommitIncidentMethod(call, incident.Incident.Id, method, "", plan);
        return new RoundsTendResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
