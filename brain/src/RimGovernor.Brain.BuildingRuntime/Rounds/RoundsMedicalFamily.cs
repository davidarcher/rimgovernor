using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The runtime half of the medical family (#3258). Its read: the emergency census the critical patients are counted
/// from and, while there are any, the doctors' and rescuers' pawn rows (the immediate pass reads only these), plus
/// in the ordinary pass the owned colony census the care, reserves and surgery Needs and the amputations read. Its
/// work, each committed under the Need and admitted by claims:
/// <list type="bullet">
/// <item>a critical patient's tend, else its rescue, paired under the step's arbiter, and in the ordinary pass a
/// life-saving amputation;</item>
/// <item>care's hospital bed, built through the shared building planner or converted;</item>
/// <item>reserves' stalled-source withdrawals and herb harvest (its medicine bill is the work ledger's);</item>
/// <item>surgery's restore and harvest plans.</item>
/// </list>
/// </summary>
public sealed class MedicalRounds
{
    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient? _tend;
    private readonly FacadeNativeClient? _rescue;
    private readonly RoundsMedicalPlanner? _medical;
    private readonly RoundsHospitalPlanner? _hospital;
    private readonly RoundsSurgeryPlanner? _surgery;

    /// <summary>
    /// <paramref name="tend"/> and <paramref name="rescue"/> compose the critical Needs' care orders and
    /// <paramref name="medical"/> their amputations with the reserves and surgery Needs; <paramref name="hospital"/>
    /// composes the care Need. The critical Needs exist when any of their work is composed.
    /// </summary>
    public MedicalRounds(Rounder reviewer, object native, bool tend, bool rescue, bool medical, bool hospital)
    {
        _reviewer = reviewer ?? throw new ControlException("MedicalRounds: reviewer is missing");
        ArgumentNullException.ThrowIfNull(native);
        if (!tend && !rescue && !medical && !hospital)
        {
            throw new ControlException("MedicalRounds: no medical Need is composed");
        }
        if (tend)
        {
            _tend = native as FacadeNativeClient ?? throw new ControlException("MedicalRounds: native cannot read tend pawns");
        }
        if (rescue)
        {
            _rescue = native as FacadeNativeClient ?? throw new ControlException("MedicalRounds: native cannot read rescue pawns");
        }
        if (medical)
        {
            _medical = RoundsMedicalPlanner.NewRoundsMedicalPlanner(reviewer, native as FacadeNativeClient ?? throw new ControlException("MedicalRounds: native cannot read medicine"));
            _surgery = RoundsSurgeryPlanner.Create(reviewer);
        }
        if (hospital)
        {
            _hospital = RoundsHospitalPlanner.NewRoundsHospitalPlanner(reviewer, native as IRoundsBuildingSource ?? throw new ControlException("MedicalRounds: native cannot build"));
        }
        if (Critical && _tend is null && _rescue is null && native is not FacadeNativeClient)
        {
            throw new ControlException("MedicalRounds: native cannot read the emergency census");
        }
        _native = native;
    }

    private readonly object _native;

    /// <summary>The emergency census, through the client.</summary>
    private async Task<EmergencyObservation> ReadEmergencyAsync(C.Identity identity, CancellationToken ct) => _native is FacadeNativeClient client
        ? await client.ReadEmergencyAsync(identity, ct).ConfigureAwait(false)
        : throw new ControlException("medical read: native cannot read the emergency census");

    private bool Critical => _tend is not null || _rescue is not null || _medical is not null;

    /// <summary>The family's scheduler entry: it runs in the immediate pass for its critical Needs and wakes on the outcomes of every kind its Methods carry.</summary>
    public FamilyRunner Runner() =>
        new FamilyRunner<MedicalFacts>(new MedicalFamily(Critical, _hospital is not null, _medical is not null, _surgery is not null), ReadAsync, Schedule, WorkAsync);

    /// <summary>
    /// A critical entry. Its protection is the critical Needs' open tends and rescues, as the tend and rescue
    /// planners' was; an amputation bill is ordinary work and not watched.
    /// </summary>
    public static FamilySchedule Schedule { get; } = new()
    {
        Class = PlannerClass.Critical,
        Kinds = [ActionKind.Tend, ActionKind.Rescue, ActionKind.Surgery, ActionKind.Building, ActionKind.BedUse, ActionKind.Acquisition, ActionKind.AcquisitionWithdraw],
        ProtectionKinds = [ActionKind.Tend, ActionKind.Rescue],
    };

    /// <summary>
    /// Reads the family's facts in <paramref name="scope"/>'s world. The immediate pass reads the emergency census and
    /// the care candidates alone; the ordinary pass reads the owned colony census first and counts the critical
    /// patients from its emergency rows. Throws when a read is from another world, older than the review, or slower
    /// than the review's age bound.
    /// </summary>
    internal async Task<MedicalFacts> ReadAsync(FamilyReadScope scope, CancellationToken ct)
    {
        var started = _reviewer.Clock.Now();
        var journal = _reviewer.Player.Journal;
        var review = await journal.LoadRoundsAsync(ct).ConfigureAwait(false);
        var facts = new MedicalFacts();
        EmergencySnapshot? emergency = null;
        var emergencyTick = default(Tick);
        var emergencyFacts = new EmergencyFacts();
        EquatableArray<CarePawn> medicalPawns = [];
        if (!scope.Immediate)
        {
            var expected = await StepScope.StepScopeAsync(scope, _reviewer.Native, ct).ConfigureAwait(false);
            if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, scope.World, review.Tick))
            {
                throw new ControlException("medical read: the scope is another world or older than the review");
            }
            var reading = await _reviewer.ObserveOwnedAsync(_reviewer.Native, expected, ct).ConfigureAwait(false);
            var f = reading.Reading.Facts;
            f = f with { MedicalCareRecovered = MedicalRules.ReviewMedicalCare(f.MedicalPawns, review.MedicalCare).Recovered() };
            facts = MedicalFacts.From(f, _reviewer.Policy, review.Latches.MedicalReserve) with
            {
                HospitalShort = HospitalRules.BedsShort(f.GoalBuild.HospitalBeds, reading.Reading.Facts.Upkeep.Rooms, f.Sleeping, SurgeryRules.SurgeryBedShortPatients(f.MedicalPawns).Count > 0),
            };
            medicalPawns = f.MedicalPawns;
            emergency = EmergencySnapshot.New(scope.World, expected.Tick, reading.Emergency);
            emergencyTick = expected.Tick;
            emergencyFacts = reading.Emergency;
        }
        if (Critical)
        {
            if (emergency is null)
            {
                var observed = await ReadEmergencyAsync(Boundary.Identity(scope.World), ct).ConfigureAwait(false);
                emergencyTick = new Tick(EmergencyTick(observed, scope.World, review.Tick));
                emergencyFacts = observed.Facts;
                emergency = EmergencySnapshot.New(scope.World, emergencyTick, emergencyFacts);
            }
            var critical = EmergencyNeedsRules.CriticalPatientIds(emergency, scope.World, emergencyTick, medicalPawns);
            facts = facts with { Critical = critical };
            if (critical.Count > 0 && (_tend is not null || _rescue is not null))
            {
                facts = facts with { Care = await CareAsync(scope.World, emergencyFacts, critical, ct).ConfigureAwait(false) };
            }
        }
        var elapsed = _reviewer.Clock.Now() - started;
        if (elapsed < TimeSpan.Zero || elapsed > _reviewer.MaxAge)
        {
            throw new ControlException("medical read: the read took too long");
        }
        return facts;
    }

    /// <summary>The emergency read's tick once its context is checked against the world and the review.</summary>
    private static long EmergencyTick(EmergencyObservation observed, GenerationSnapshot world, Tick reviewed)
    {
        try
        {
            Boundary.Context(observed.Context, world);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("medical read: emergency context", e);
        }
        if (observed.Context is not { } context || context.Tick < reviewed.Value)
        {
            throw new ControlException("medical read: emergency read older than the review");
        }
        return context.Tick;
    }

    /// <summary>
    /// The doctors, rescuers and patients for the critical patients: the colonist and dependent censuses must be
    /// complete (else the care names the census it waits on), then the tend and rescue pawn rows are read, and the
    /// pawns the critical Needs' open tends and rescues hold are set aside.
    /// </summary>
    private async Task<CriticalCare> CareAsync(GenerationSnapshot world, EmergencyFacts emergency, IReadOnlyList<PawnId> patients, CancellationToken ct)
    {

        var identity = Boundary.Identity(world);
        var workers = emergency.Colonists.Select(pawn => pawn.Id).ToHashSet();
        var care = new CriticalCare { Busy = CareHeld(world, patients, ct) };
        if (_tend is not null)
        {
            var eligible = EmergencyPatientsRules.EmergencyTendPatients(emergency).ToHashSet();
            var ids = workers.Union(eligible).Select(pawn => pawn.Value).Order(StringComparer.Ordinal).ToList();
            var reply = await _tend.ReadPawnsAsync(identity, ids, ct).ConfigureAwait(false);
            var observed = PawnRows(reply, world, ids, "tend");
            var tend = TendRows(await _tend.ReadTendDoctorsAsync(identity, ids, ct).ConfigureAwait(false), world, ids);
            var threatened = TendRules.ThreatenedPatients(emergency);
            var doctors = new List<TendDoctorFacts>();
            var tendPatients = new List<TendPatientFacts>();
            foreach (var row in observed.Pawns)
            {
                var pawn = new PawnId(row.Pawn.Id ?? "");
                // A doctor whose reach budget ran out (TendReach.Skipped) is no candidate this read.
                if (workers.Contains(pawn) && TendFacts.Reached(tend[pawn.Value]))
                {
                    doctors.Add(TendFacts.NewTendDoctorFacts(pawn, row, tend[pawn.Value]));
                }
                if (eligible.Contains(pawn))
                {
                    tendPatients.Add(TendFacts.NewTendPatientFacts(pawn, row, threatened.Contains(pawn)));
                }
            }
            care = care with { Doctors = doctors, TendPatients = tendPatients, Reach = TendFacts.TendReachabilityOf(tend.Values) };
        }
        if (_rescue is not null)
        {
            var eligible = EmergencyPatientsRules.EmergencyRescuePatients(emergency).ToHashSet();
            var ids = workers.Union(eligible).Select(pawn => pawn.Value).Order(StringComparer.Ordinal).ToList();
            var reply = await _rescue.ReadPawnsAsync(identity, ids, ct).ConfigureAwait(false);
            var observed = PawnRows(reply, world, ids, "rescue");
            var rescuers = new List<RescuerFacts>();
            var rescuePatients = new List<RescuePatientFacts>();
            foreach (var row in observed.Pawns)
            {
                var pawn = new PawnId(row.Pawn.Id ?? "");
                if (workers.Contains(pawn))
                {
                    rescuers.Add(RescueFacts.NewRescuerFacts(pawn, row));
                }
                if (eligible.Contains(pawn))
                {
                    rescuePatients.Add(RescueFacts.NewRescuePatientFacts(pawn, row));
                }
            }
            care = care with { Rescuers = rescuers, RescuePatients = rescuePatients };
        }
        return care;
    }

    /// <summary>The pawn rows of a care read, checked: in the world, one row per requested pawn and no other.</summary>
    private static O.PawnSnapshot PawnRows(O.PawnSnapshot observed, GenerationSnapshot world, List<string> ids, string read)
    {
        try
        {
            Boundary.Context(observed.Context, world);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException($"medical read: {read} pawn context", e);
        }
        if (observed.Pawns.Count != ids.Count)
        {
            throw new ControlException($"medical read: {read} pawn count differs from the request");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in observed.Pawns)
        {
            if (row is null || !ids.Contains(row.Pawn.Id ?? "") || !seen.Add(row.Pawn.Id ?? ""))
            {
                throw new ControlException($"medical read: missing or duplicate {read} pawn row");
            }
        }
        return observed;
    }

    /// <summary>The tend table of a care read, checked: in the world, one row per requested pawn and no other.</summary>
    private static Dictionary<string, O.TendDoctor> TendRows(O.TendDoctorSnapshot observed, GenerationSnapshot world, List<string> ids)
    {
        try
        {
            Boundary.Context(observed.Context, world);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("medical read: tend table context", e);
        }
        var rows = new Dictionary<string, O.TendDoctor>(StringComparer.Ordinal);
        foreach (var row in observed.Doctors)
        {
            if (!ids.Contains(row.PawnId) || !rows.TryAdd(row.PawnId, row))
            {
                throw new ControlException("medical read: unrequested or duplicate tend row");
            }
        }
        if (rows.Count != ids.Count)
        {
            throw new ControlException("medical read: tend row count differs from the request");
        }
        return rows;
    }

    /// <summary>The doctors, rescuers and patients the critical Needs' open tends and rescues hold.</summary>
    private IReadOnlyList<PawnId> CareHeld(GenerationSnapshot world, IReadOnlyList<PawnId> patients, CancellationToken ct)
    {
        var journal = _reviewer.Player.Journal;
        var held = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var patient in patients)
        {
            foreach (var method in journal.LoadNeed(MedicalRules.CriticalNeed(patient), world, ct).Methods)
            {
                var plan = journal.LoadPlan(method.Plan, ct);
                if (plan.Retired)
                {
                    continue;
                }
                foreach (var progress in plan.Progress)
                {
                    if (!WorkProgress.Open([progress]))
                    {
                        continue;
                    }
                    if (progress.Action.Tend is { } tend)
                    {
                        held.Add(tend.Doctor.Value);
                        held.Add(tend.Patient.Value);
                    }
                    else if (progress.Action.Rescue is { } rescue)
                    {
                        held.Add(rescue.Rescuer.Value);
                        held.Add(rescue.Patient.Value);
                    }
                }
            }
        }
        return [.. held.Select(id => new PawnId(id))];
    }

    /// <summary>The work of a ready Need: a critical patient's care, the hospital, the medicine restock or the operations.</summary>
    internal async Task<Verdict> WorkAsync(NeedWork work, MedicalFacts facts, CancellationToken call)
    {
        if (MedicalRules.IsCriticalNeed(work.Need.Id, out var patient))
        {
            return await CriticalAsync(work, facts, patient, call).ConfigureAwait(false);
        }
        if (work.Immediate)
        {
            return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        if (work.Need.Id == MedicalRules.MedicalCareNeed && _hospital is not null)
        {
            return (await _hospital.StepAsync(work, call).ConfigureAwait(false)).Verdict;
        }
        if (work.Need.Id == MedicalRules.MedicalReservesNeed && _medical is not null)
        {
            return (await _medical.RestockAsync(work, call).ConfigureAwait(false)).Verdict;
        }
        if (work.Need.Id == MedicalRules.SurgeryNeed && _surgery is not null)
        {
            return (await _surgery.StepAsync(work, call).ConfigureAwait(false)).Verdict;
        }
        return Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
    }

    /// <summary>
    /// A critical patient's work. Its open tend or rescue continues. Otherwise the patient's tend pair is ordered,
    /// else its rescue pair; and in the ordinary pass a life-saving amputation goes beside them. With no pair the
    /// Need lends a window: only game time frees a doctor or brings the patient into reach.
    /// </summary>
    private async Task<Verdict> CriticalAsync(NeedWork work, MedicalFacts facts, PawnId patient, CancellationToken call)
    {
        var journal = _reviewer.Player.Journal;
        var amputation = !work.Immediate && _medical is not null
            ? await _medical.AmputateAsync(work, patient, call).ConfigureAwait(false)
            : null;
        if (amputation == BuildingReasonAdmitted)
        {
            return amputation.Value;
        }
        if (_tend is null && _rescue is null)
        {
            return amputation ?? Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
        }
        if (work.Owner.Methods.Any(m => IsCareMethod(m.Method) && WorkProgress.Open(journal.LoadPlan(m.Plan, call).Progress)))
        {
            return BuildingReasonExistingWork;
        }
        if (facts.Care is not { } care)
        {
            return Verdict.WaitFor(Cause.MethodUsed, "medical_pair");
        }

        if (_tend is not null)
        {
            var (doctor, ok) = MedicalRules.TendDoctorFor(care, patient);
            if (ok && work.Arbiter!.TryClaim([doctor, patient]))
            {
                var tend = new Domain.Tend(doctor, patient);
                return CommitCare(work, new MethodId("tend-" + patient.Value), id => DomainAction.NewTendAction(new ActionId($"{id}-0"), tend), call);
            }
        }
        if (_rescue is not null)
        {
            var (rescuer, ok) = MedicalRules.RescuerFor(care, patient);
            if (ok && work.Arbiter!.TryClaim([rescuer, patient]))
            {
                var rescue = new Rescue((Domain.PawnId)rescuer, (Domain.PawnId)patient);
                return CommitCare(work, new MethodId("rescue-" + patient.Value), id => DomainAction.NewRescueAction(new ActionId($"{id}-0"), rescue), call);
            }
        }
        // No pair to order: the patient is up and out of bed, or every doctor is ineligible, busy or walled off
        // from the patient. Only game time changes that.
        return Verdict.WaitFor(Cause.MethodUsed, _tend is not null ? "medical_pair" : "rescue_pairing");
    }

    /// <summary>Whether a Method of a critical Need is its care order (a tend or a rescue), not an amputation.</summary>
    internal static bool IsCareMethod(MethodId method) => method.Value.StartsWith("tend-", StringComparison.Ordinal) || method.Value.StartsWith("rescue-", StringComparison.Ordinal);

    /// <summary>
    /// Commits one care order as <paramref name="method"/> of the patient's Need. The MethodId is the patient's: a
    /// released attempt is ordered again through its refusal budget, and whichever doctor is best now takes it.
    /// </summary>
    private Verdict CommitCare(NeedWork work, MethodId method, Func<PlanId, DomainAction> action, CancellationToken call)
    {
        var p = _reviewer.Player;
        var attempts = p.Journal.OwnerMethodHistory(work.Owner, call).Where(m => m.Method == method).Select(m => m.Plan).ToList();
        var (refused, admitted) = RoundsRefusalBudget.AdmitSubject(p.Journal, method.Value, attempts, work.State.Snapshot, call);
        if (!admitted)
        {
            // Native refused this patient's care and the refusal still stands; the Need stays visible and the
            // clock still advances under it.
            return refused;
        }
        var id = PlanIds.MintPlanId();
        var plan = new PlanSpec(id, new PlanRevision(1), [action(id)]);
        p.Current(call, work.Epoch);
        if (p.Session.State() != work.State)
        {
            throw new ControlException("medical care: the session state changed");
        }
        return RoundsBuildingPlanner.OwnerCommitVerdict(p.Journal.CommitOwnerWork(work.Owner, method, "", plan, call));
    }
}
