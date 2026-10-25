namespace RimGovernor.Brain.Policy;

/// <summary>
/// The doctors, rescuers and patients one family read observed together for the critical Needs (#3258): the tend
/// candidates with their reachability and the rescue candidates. A missing census fails the observation boundary.
/// </summary>
public sealed record CriticalCare
{
    public IReadOnlyList<TendDoctorFacts> Doctors { get; init; } = [];

    public IReadOnlyList<TendPatientFacts> TendPatients { get; init; } = [];

    public TendReachability Reach { get; init; } = TendReachability.None;

    public IReadOnlyList<RescuerFacts> Rescuers { get; init; } = [];

    public IReadOnlyList<RescuePatientFacts> RescuePatients { get; init; } = [];

    /// <summary>The doctors, rescuers and patients an open tend or rescue of a critical Need already holds; no new pair takes them.</summary>
    public IReadOnlyList<PawnId> Busy { get; init; } = [];
}

/// <summary>
/// What the medical family reads (#3258). For the critical Needs: the living patients the emergency census counts
/// (downed, bleeding or needing a tend, and a colonist a life-saving amputation would save) and, while there are
/// any, the doctors and rescuers observed for them. For care: whether the sick are resting and in care
/// (<see cref="MedicalCareHistory.Recovered"/>). For reserves: the medicine stock against the target bar. For
/// surgery: whether an owed operation, an elective upgrade, an organ harvest or a part recovery
/// stands.
/// </summary>
public sealed record MedicalFacts
{
    /// <summary>The critical patients by id from the current emergency census.</summary>
    public EquatableArray<PawnId> Critical { get; init; }

    /// <summary>The care candidates read for the critical patients; null when there are none or nothing was read.</summary>
    public CriticalCare? Care { get; init; }

    /// <summary>The colony's care census: no sick colonist resting or in bad condition. Unresolved tracked patients keep recovery false.</summary>
    public bool CareRecovered { get; init; }

    /// <summary>
    /// The hosted medical beds fall below a Hospital goal's bed floor (#3496). The hospital step runs only while the care
    /// Need is unmet, and with no patient sick the census alone would leave it met, so a short ward keeps it unmet.
    /// The goal reader supplies zero when no bed floor is requested.
    /// </summary>
    public bool HospitalShort { get; init; }

    /// <summary>The medicine stock reaches the target bar (no hysteresis).</summary>
    public bool ReservesCovered { get; init; }

    /// <summary>The usable medicine stock and its target bar, for evidence.</summary>
    public long MedicineStock { get; init; }

    public long MedicineTarget { get; init; }

    /// <summary>No operation the surgery work serves stands on a living patient.</summary>
    public bool SurgeryRecovered { get; init; }

    /// <summary>
    /// The facts a review already holds. <paramref name="medicineActive"/> is the review's medicine latch the silver
    /// gap reads (the economy's trade need), which only the organ sale harvest consults. The critical patients are
    /// the family read's own (<see cref="EmergencyNeedsRules.CriticalPatientIds"/>).
    /// </summary>
    public static MedicalFacts From(RoundsFacts f, RoundsPolicy policy, bool medicineActive)
    {
        var p = policy.Seasonal(f.Calendar, f.DisasterConditions);
        var reserve = MedicalRules.MedicalReserveAtTarget(f.MedicalReserve with { Colonists = f.Colonists }, p.MedicalReserve);
        return new()
        {
            CareRecovered = f.MedicalCareRecovered,
            ReservesCovered = !reserve.Active,
            MedicineStock = reserve.Stock,
            MedicineTarget = reserve.Target,
            SurgeryRecovered = MedicalRules.SurgeryNeedRecovered(f, policy, medicineActive),
        };
    }
}

/// <summary>
/// The medical family (#3258): the colony's patients, its care, its medicine and its operations.
/// <list type="bullet">
/// <item><c>medical/critical/&lt;pawn&gt;</c> (SeriousHarm, one per patient) is unmet while the pawn is a critical
/// patient. An unread census raises <see cref="RequiredFactException"/>. It runs in the
/// immediate pass on the emergency and pawn reads alone. Its work is the patient's tend and rescue, and in the
/// ordinary pass a life-saving amputation, each the runtime's: they pair the patient with a doctor or rescuer under
/// the step's arbiter.</item>
/// <item><c>medical/care</c> is met while no sick colonist rests or is in bad condition. Its work is the hospital
/// bed, built or converted.</item>
/// <item><c>medical/reserves</c> is met while the medicine stock reaches the target bar. Its medicine bill is the
/// work ledger's; its stalled-source withdrawals and herb harvests are the runtime's.</item>
/// <item><c>medical/surgery</c> is met while no operation the surgery work serves stands. Its work is the restore,
/// and harvest plans; the surgery part bill is the work ledger's.</item>
/// </list>
/// Care and reserves are independent: the medicine stock never waited on the sick resting, and the hospital is
/// care's own work, so no precondition orders them.
/// </summary>
public sealed class MedicalFamily(bool critical, bool care, bool reserves, bool surgery) : IFamily<MedicalFacts>
{
    public string Id => MedicalRules.MedicalFamilyId;

    /// <summary>The colony census and the room census the hospital stands in, the pawns the care and surgery census read, and the emergency census the patients are counted from.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms, FactFamily.Emergency];

    /// <summary>The emergency census and the doctors' and patients' pawn rows, when the critical Needs are composed.</summary>
    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = critical ? [FactFamily.Emergency, FactFamily.Pawns] : [];

    public IReadOnlyList<Need> Derive(MedicalFacts facts) =>
    [
        .. DeriveSeriousHarm(facts),
        .. care ? [MedicalRules.MedicalCareNeedOf(facts)] : Array.Empty<Need>(),
        .. reserves ? [MedicalRules.MedicalReservesNeedOf(facts)] : Array.Empty<Need>(),
        .. surgery ? [MedicalRules.SurgeryNeedOf(facts)] : Array.Empty<Need>(),
    ];

    /// <summary>The critical Needs, when composed.</summary>
    public IReadOnlyList<Need> DeriveSeriousHarm(MedicalFacts facts) => critical ? MedicalRules.CriticalNeedsOf(facts) : [];

    /// <summary>None: every medical Method is runtime work (pairs under the step's arbiter, native reads, ledger bills).</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, MedicalFacts facts) => [];
}

public static partial class MedicalRules
{
    public const string MedicalFamilyId = "medical";

    /// <summary>The critical census as one Need (#3258), for a reader that counts the patients without naming them.</summary>
    public static readonly NeedId CriticalCensusNeed = NeedId.Of(MedicalFamilyId, "critical");

    /// <summary>The medical family's care Need (#3258).</summary>
    public static readonly NeedId MedicalCareNeed = NeedId.Of(MedicalFamilyId, "care");

    /// <summary>The medical family's medicine reserves Need (#3258).</summary>
    public static readonly NeedId MedicalReservesNeed = NeedId.Of(MedicalFamilyId, "reserves");

    /// <summary>The medical family's surgery Need (#3258).</summary>
    public static readonly NeedId SurgeryNeed = NeedId.Of(MedicalFamilyId, "surgery");

    /// <summary>The critical Need of one patient (#3258).</summary>
    public static NeedId CriticalNeed(PawnId patient) => NeedId.Of(MedicalFamilyId, "critical", patient.Value);

    /// <summary>Whether <paramref name="need"/> is a critical patient Need, and whose.</summary>
    public static bool IsCriticalNeed(NeedId need, out PawnId patient)
    {
        const string Prefix = MedicalFamilyId + "/critical/";
        patient = default;
        if (!need.Value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        patient = new PawnId(need.Value[Prefix.Length..]);
        return true;
    }

    /// <summary>
    /// The critical Needs: one SeriousHarm Need per patient, unmet while the pawn is a critical patient (a Need that
    /// is met no longer exists: the patient left the census). An unread census raises <see cref="RequiredFactException"/>.
    /// </summary>
    public static IReadOnlyList<Need> CriticalNeedsOf(MedicalFacts f)
    {
        var patients = f.Critical;
        return [.. patients.Select(p => new Need
        {
            Id = CriticalNeed(p),
            Tier = NeedTier.SeriousHarm,
            Satisfaction = Satisfaction.Unmet,
            Deficit = 1.0,
        })];
    }

    /// <summary>
    /// The critical census as one Need, for a reader that counts the patients without naming them (the essential
    /// services review): met with none, unmet with any.
    /// </summary>
    public static Need CriticalCensusNeedOf(long patients) =>
        FoodRules.WholeNeed(CriticalCensusNeed, NeedTier.SeriousHarm, patients == 0);

    /// <summary>
    /// The care Need, a plain threshold over the care census: unmet while a sick colonist rests or is in bad condition
    /// or the hospital is short. Unresolved tracked patients keep the Need unmet.
    /// </summary>
    public static Need MedicalCareNeedOf(MedicalFacts f)
    {
        var wardShort = f.HospitalShort;
        return FoodRules.WholeNeed(MedicalCareNeed, NeedTier.Wellbeing,
            !wardShort && f.CareRecovered);
    }

    /// <summary>
    /// The reserves Need, a plain threshold at the target bar (decision 16, no entry bar): unmet while the usable
    /// medicine stock is below the colonists' target. The
    /// deficit is the missing share of the target.
    /// </summary>
    public static Need MedicalReservesNeedOf(MedicalFacts f)
    {
        var covered = f.ReservesCovered;
        return new Need
        {
            Id = MedicalReservesNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = covered ? Satisfaction.Met : Satisfaction.Unmet,
            Deficit = covered ? 0.0
                : f.MedicineTarget > 0
                    ? Math.Clamp((f.MedicineTarget - f.MedicineStock) / (double)f.MedicineTarget, 0, 1) : 1.0,
        };
    }

    /// <summary>The surgery Need, a plain threshold: unmet while an operation the surgery work serves stands.</summary>
    public static Need SurgeryNeedOf(MedicalFacts f) =>
        FoodRules.WholeNeed(SurgeryNeed, NeedTier.Wellbeing, f.SurgeryRecovered);

    /// <summary>
    /// Whether no operation the surgery work serves stands: no owed restore, no actionable elective upgrade, and no
    /// organ sale harvest (while the silver runway is short) or part recovery wanted.
    /// </summary>
    public static bool SurgeryNeedRecovered(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        var recovered = SurgeryRules.SurgeryRecovered(f.MedicalPawns) && !SurgeryRules.ElectiveSurgeryOwed(f.MedicalPawns, f.SurgeryContextOf(), f.FabricableParts);
        if (OrganHarvestRules.SaleHarvestWanted(f, SilverGapRules.RoundsSilverShort(f, p, medicineActive)) || OrganHarvestRules.PartRecoveryWanted(f))
        {
            return false;
        }
        return recovered;
    }

    /// <summary>The medicine reserve held to its target bar (decision 16): the review an unlatched restock reads. Invalid facts or thresholds raise a contract error.</summary>
    public static MedicalReserveReview MedicalReserveAtTarget(MedicalReserveObservation v, MedicalReservePolicy p) =>
        ReviewMedicalReserve(v, true, p);

    /// <summary>
    /// The tend pairs for every patient at once: the most urgent patient a ranked doctor reaches first
    /// (<see cref="SelectTend"/>), then the next among the doctors and patients left. A patient is never another's
    /// doctor, and a doctor serves one patient.
    /// </summary>
    public static IReadOnlyList<(PawnId Doctor, PawnId Patient)> TendPairs(IReadOnlyList<TendDoctorFacts> doctors, IReadOnlyList<TendPatientFacts> patients, TendReachability reach)
    {
        var d = doctors.ToList();
        var p = patients.ToList();
        var pairs = new List<(PawnId, PawnId)>();
        while (true)
        {
            var (doctor, patient, ok) = TendRules.SelectTend(d, p, reach);
            if (!ok)
            {
                return pairs;
            }
            pairs.Add((doctor, patient));
            d.RemoveAll(x => x.Pawn == doctor || x.Pawn == patient);
            p.RemoveAll(x => x.Pawn == patient || x.Pawn == doctor);
        }
    }

    /// <summary>
    /// The doctor <paramref name="patient"/> is paired with among the tend pairs of every critical patient
    /// (<see cref="TendPairs"/>), leaving out the pawns an open tend or rescue holds; false when it gets none. The
    /// pairing is the same for every patient's Need, so two patients' Needs never pick one doctor.
    /// </summary>
    public static (PawnId Doctor, bool Ok) TendDoctorFor(CriticalCare care, PawnId patient)
    {
        var busy = care.Busy.ToHashSet();
        foreach (var (doctor, paired) in TendPairs([.. care.Doctors.Where(d => !busy.Contains(d.Pawn))], [.. care.TendPatients.Where(p => !busy.Contains(p.Pawn))], care.Reach))
        {
            if (paired == patient)
            {
                return (doctor, true);
            }
        }
        return (default, false);
    }

    /// <summary>The rescuer <paramref name="patient"/> is paired with among every downed patient's rescue pairs (<see cref="RescuePairs"/>), leaving out the pawns an open tend or rescue holds.</summary>
    public static (PawnId Rescuer, bool Ok) RescuerFor(CriticalCare care, PawnId patient)
    {
        var busy = care.Busy.ToHashSet();
        foreach (var (rescuer, paired) in RescuePairs([.. care.Rescuers.Where(r => !busy.Contains(r.Pawn))], [.. care.RescuePatients.Where(p => !busy.Contains(p.Pawn))]))
        {
            if (paired == patient)
            {
                return (rescuer, true);
            }
        }
        return (default, false);
    }

    /// <summary>The rescue pairs for every downed patient at once, in <see cref="SelectRescue"/>'s order; a rescuer carries one patient.</summary>
    public static IReadOnlyList<(PawnId Rescuer, PawnId Patient)> RescuePairs(IReadOnlyList<RescuerFacts> rescuers, IReadOnlyList<RescuePatientFacts> patients)
    {
        var r = rescuers.ToList();
        var p = patients.ToList();
        var pairs = new List<(PawnId, PawnId)>();
        while (true)
        {
            var (rescuer, patient, ok) = RescueRules.SelectRescue(r, p);
            if (!ok)
            {
                return pairs;
            }
            pairs.Add((rescuer, patient));
            r.RemoveAll(x => x.Pawn == rescuer || x.Pawn == patient);
            p.RemoveAll(x => x.Pawn == patient || x.Pawn == rescuer);
        }
    }
}
