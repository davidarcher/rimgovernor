namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsTendSource
{
    Task<(EmergencyObservation Observation, BridgeResult Result)> ReadEmergencyAsync(C.Identity identity, CancellationToken ct);

    Task<(ObservationsPb.ListPawnsReply Reply, BridgeResult Result)> ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct);
}

public sealed class RoundsTendResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }
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

    internal async Task<RoundsTendResult> StepAsync(StepArbiter arbiter, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsTendResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsTendResult { Verdict = BuildingReasonNoReview };
        }
        var (incident, found) = IncidentDeficit(p.Journal, review, CriticalMedicine, call);
        if (!found)
        {
            return new RoundsTendResult { Verdict = BuildingReasonNoDeficit };
        }
        if (IncidentOpenWork(p.Journal, incident!, call))
        {
            return new RoundsTendResult { Verdict = BuildingReasonExistingWork };
        }
        var started = _reviewer.Clock.Now();
        var identity = Boundary.Identity(state.Snapshot);
        var (emergency, _) = await _native.ReadEmergencyAsync(identity, call).ConfigureAwait(false);
        try
        {
            Boundary.Context(emergency.Context, state.Snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if ((emergency.Context?.Tick ?? 0) < review.Tick.Value)
        {
            throw new ControlException("step: err != nil || emergency.Context.GetTick() < int64(review.Tick)");
        }
        if (!emergency.Facts.ColonistsComplete.TryGetValue(out var complete) || !complete || emergency.Facts.Colonists.Count == 0)
        {
            return new RoundsTendResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "colonist_census") };
        }
        var ids = emergency.Facts.Colonists.Select(pawn => pawn.Id.Value).ToList();
        var (reply, _) = await _native.ReadTendPawnsAsync(identity, ids, call).ConfigureAwait(false);
        var observed = reply.Outcome?.Observed;
        if (observed is null)
        {
            throw new ControlException("step: observed == nil");
        }
        try
        {
            Boundary.Context(observed.Context, state.Snapshot);
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
            if (row is null || row.Pawn is null || !seen.Add((row.Pawn.Id ?? "")))
            {
                throw new ControlException("step: row == nil || row.Pawn == nil || seen[row.Pawn.GetId()]");
            }
            var pawn = new PawnId((row.Pawn.Id ?? ""));
            doctors.Add(TendFacts.NewTendDoctorFacts(pawn, row, ""));
            var patientFacts = TendFacts.NewTendPatientFacts(pawn, row, "") with { Threatened = threatened.Contains(pawn) };
            patients.Add(patientFacts);
        }
        var (doctor, patient, ok) = SelectTend(doctors, patients, TendFacts.TendReachabilityOf(observed.Pawns));
        if (ok && !arbiter.TryClaim([doctor, patient]))
        {
            ok = false;
        }
        if (!ok)
        {
            // No pair to order: the patient is up and out of bed, or every doctor is ineligible, busy or walled
            // off from the patients. Only game time changes that, so the step lends a window rather than
            // reporting no work.
            return new RoundsTendResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "medical_pair"), NativeWorkTicks = checked((uint)MedicalWaitTicks) };
        }
        var tend = new Domain.Tend(new Domain.PawnId(doctor.Value), new Domain.PawnId(patient.Value));
        // Keyed by patient and attempt count, not doctor: a fresh attempt after an interrupted or failed try
        // picks whichever doctor is currently best, which is how doctor replacement happens across cycles.
        var prefix = $"tend-{patient.Value}-";
        var (verdict, admitted) = AdmitSubject(p.Journal, prefix, IncidentPlans(incident!.Methods, prefix), state.Snapshot, call);
        if (!admitted)
        {
            // Native refused this patient's treatment and the refusal still stands; the deficit stays visible
            // and the clock must still advance under it.
            return new RoundsTendResult { Verdict = verdict, NativeWorkTicks = checked((uint)MedicalWaitTicks) };
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
        p.Journal.CommitIncidentMethod(incident.Incident.Id, method, "", plan, call);
        return new RoundsTendResult { Verdict = BuildingReasonAdmitted, Plan = id };
    }
}
