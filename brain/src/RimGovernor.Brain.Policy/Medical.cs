namespace RimGovernor.Brain.Policy;

/// <summary>
/// The doctors, rescuers and patients one family read observed together for the critical Needs (#3258): the tend
/// candidates with their reachability and the rescue candidates. <see cref="Wait"/> names an incomplete census
/// (<c>colonist_census</c>, <c>dependent_patient_census</c>); nothing is paired then.
/// </summary>
public sealed record CriticalCare
{
    public string Wait { get; init; } = "";

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
/// surgery: whether an owed operation, an elective upgrade, an organ harvest, a part recovery or a peg-leg step
/// stands.
/// </summary>
public sealed record MedicalFacts
{
    /// <summary>The critical patients, by id; unknown while the emergency census is incomplete or stale.</summary>
    public Fact<EquatableArray<PawnId>> Critical { get; init; }

    /// <summary>The care candidates read for the critical patients; null when there are none or nothing was read.</summary>
    public CriticalCare? Care { get; init; }

    /// <summary>The colony's care census: no sick colonist resting or in bad condition. Unknown while a tracked pawn is unread.</summary>
    public Fact<bool> CareRecovered { get; init; }

    /// <summary>The medicine stock reaches the target bar (no hysteresis); unknown while the stock or the catalog is unread.</summary>
    public Fact<bool> ReservesCovered { get; init; }

    /// <summary>The usable medicine stock and its target bar, for evidence.</summary>
    public Fact<long> MedicineStock { get; init; }

    public Fact<long> MedicineTarget { get; init; }

    /// <summary>No operation the surgery work serves stands on a living patient; unknown while the care census is unread.</summary>
    public Fact<bool> SurgeryRecovered { get; init; }

    /// <summary>
    /// The facts a review already holds. <paramref name="medicineActive"/> is the review's medicine latch the silver
    /// gap reads (the economy's trade need), which only the organ sale harvest consults. The critical patients are
    /// the family read's own (<see cref="PolicyFunctions.CriticalPatientIds"/>).
    /// </summary>
    public static MedicalFacts From(RoundsFacts f, RoundsPolicy policy, bool medicineActive)
    {
        var p = policy.Seasonal(f.Calendar, f.DisasterConditions);
        var reserve = PolicyFunctions.MedicalReserveAtTarget(f.MedicalReserve with { Colonists = f.Colonists }, p.MedicalReserve);
        return new()
        {
            CareRecovered = f.MedicalCareRecovered,
            ReservesCovered = reserve is { } r && r.Stock.IsKnown ? Fact.Known(!r.Active) : Fact.Unknown<bool>(),
            MedicineStock = reserve?.Stock ?? default,
            MedicineTarget = reserve?.Target ?? default,
            SurgeryRecovered = PolicyFunctions.SurgeryNeedRecovered(f, policy, medicineActive),
        };
    }
}

/// <summary>
/// The medical family (#3258): the colony's patients, its care, its medicine and its operations.
/// <list type="bullet">
/// <item><c>medical/critical/&lt;pawn&gt;</c> (SeriousHarm, one per patient) is unmet while the pawn is a critical
/// patient. With the census unknown the family derives one <c>medical/critical</c> Need, Unknown. It runs in the
/// immediate pass on the emergency and pawn reads alone. Its work is the patient's tend and rescue, and in the
/// ordinary pass a life-saving amputation, each the runtime's: they pair the patient with a doctor or rescuer under
/// the step's arbiter.</item>
/// <item><c>medical/care</c> is met while no sick colonist rests or is in bad condition. Its work is the hospital
/// bed, built or converted.</item>
/// <item><c>medical/reserves</c> is met while the medicine stock reaches the target bar. Its medicine bill is the
/// work ledger's; its stalled-source withdrawals and herb harvests are the runtime's.</item>
/// <item><c>medical/surgery</c> is met while no operation the surgery work serves stands. Its work is the restore,
/// harvest and peg-leg plans; the surgery part bill is the work ledger's.</item>
/// </list>
/// Care and reserves are independent: the medicine stock never waited on the sick resting, and the hospital is
/// care's own work, so no precondition orders them.
/// </summary>
public sealed class MedicalFamily(bool critical, bool care, bool reserves, bool surgery) : IFamily<MedicalFacts>
{
    public string Id => PolicyFunctions.MedicalFamilyId;

    /// <summary>The colony census and the room census the hospital stands in, the pawns the care and surgery census read, and the emergency census the patients are counted from.</summary>
    public IReadOnlyList<FactFamily> Reads { get; } = [FactFamily.Colony, FactFamily.Pawns, FactFamily.Rooms, FactFamily.Emergency];

    /// <summary>The emergency census and the doctors' and patients' pawn rows, when the critical Needs are composed.</summary>
    public IReadOnlyList<FactFamily> SeriousHarmReads { get; } = critical ? [FactFamily.Emergency, FactFamily.Pawns] : [];

    public IReadOnlyList<Need> Derive(MedicalFacts facts) =>
    [
        .. critical ? PolicyFunctions.CriticalNeedsOf(facts) : Array.Empty<Need>(),
        .. care ? [PolicyFunctions.MedicalCareNeedOf(facts)] : Array.Empty<Need>(),
        .. reserves ? [PolicyFunctions.MedicalReservesNeedOf(facts)] : Array.Empty<Need>(),
        .. surgery ? [PolicyFunctions.SurgeryNeedOf(facts)] : Array.Empty<Need>(),
    ];

    /// <summary>None: every medical Method is runtime work (pairs under the step's arbiter, native reads, ledger bills).</summary>
    public IReadOnlyList<NeedMethod> Propose(IReadOnlyList<Need> ready, MedicalFacts facts) => [];
}

public static partial class PolicyFunctions
{
    public const string MedicalFamilyId = "medical";

    /// <summary>The critical Need of an unknown patient census (#3258): one Unknown Need stands for the patients nobody can count.</summary>
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
    /// is met no longer exists: the patient left the census). An unknown census is one Unknown Need.
    /// </summary>
    public static IReadOnlyList<Need> CriticalNeedsOf(MedicalFacts f)
    {
        if (!f.Critical.TryGetValue(out var patients))
        {
            return [new Need { Id = CriticalCensusNeed, Tier = NeedTier.SeriousHarm, Satisfaction = Satisfaction.Unknown, Deficit = Unknown<double>(),
                Evidence = [new("patients", Unknown<string>())] }];
        }
        return [.. patients.Select(p => new Need
        {
            Id = CriticalNeed(p),
            Tier = NeedTier.SeriousHarm,
            Satisfaction = Satisfaction.Unmet,
            Deficit = Known(1.0),
            Evidence = [new("patient", Known(p.Value))],
        })];
    }

    /// <summary>
    /// The critical census as one Need, for a reader that counts the patients without naming them (the essential
    /// services review): met with none, unmet with any, unknown while the count is.
    /// </summary>
    public static Need CriticalCensusNeedOf(Fact<long> patients)
    {
        var satisfaction = patients.TryGetValue(out var n) ? n == 0 ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;
        return new Need
        {
            Id = CriticalCensusNeed,
            Tier = NeedTier.SeriousHarm,
            Satisfaction = satisfaction,
            Deficit = WholeDeficit(satisfaction),
            Evidence = [new("patients", patients.TryGetValue(out var count) ? Known(count.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Unknown<string>())],
        };
    }

    /// <summary>The care Need, a plain threshold over the care census: unmet while a sick colonist rests or is in bad condition, unknown while a tracked pawn is unread.</summary>
    public static Need MedicalCareNeedOf(MedicalFacts f)
    {
        var satisfaction = f.CareRecovered.TryGetValue(out var recovered) ? recovered ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;
        return new Need
        {
            Id = MedicalCareNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = WholeDeficit(satisfaction),
            Evidence = [new("sick", f.CareRecovered.TryGetValue(out var known) ? Known(known ? "none" : "in_care") : Unknown<string>())],
        };
    }

    /// <summary>
    /// The reserves Need, a plain threshold at the target bar (decision 16, no entry bar): unmet while the usable
    /// medicine stock is below the colonists' target, unknown while the stock is unread. The deficit is the missing
    /// share of the target.
    /// </summary>
    public static Need MedicalReservesNeedOf(MedicalFacts f)
    {
        var satisfaction = f.ReservesCovered.TryGetValue(out var covered) ? covered ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;
        var deficit = satisfaction switch
        {
            Satisfaction.Unknown => Unknown<double>(),
            Satisfaction.Met => Known(0.0),
            _ => f.MedicineStock.TryGetValue(out var stock) && f.MedicineTarget.TryGetValue(out var target) && target > 0
                ? Known(Math.Clamp((target - stock) / (double)target, 0, 1)) : Known(1.0),
        };
        static Fact<string> Count(Fact<long> n) => n.TryGetValue(out var v) ? Known(v.ToString(System.Globalization.CultureInfo.InvariantCulture)) : Unknown<string>();
        return new Need
        {
            Id = MedicalReservesNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = deficit,
            Evidence = [new("medicine_stock", Count(f.MedicineStock)), new("medicine_target", Count(f.MedicineTarget))],
        };
    }

    /// <summary>The surgery Need, a plain threshold: unmet while an operation the surgery work serves stands, unknown while the care census is unread.</summary>
    public static Need SurgeryNeedOf(MedicalFacts f)
    {
        var satisfaction = f.SurgeryRecovered.TryGetValue(out var recovered) ? recovered ? Satisfaction.Met : Satisfaction.Unmet : Satisfaction.Unknown;
        return new Need
        {
            Id = SurgeryNeed,
            Tier = NeedTier.Wellbeing,
            Satisfaction = satisfaction,
            Deficit = WholeDeficit(satisfaction),
            Evidence = [new("operations", f.SurgeryRecovered.TryGetValue(out var known) ? Known(known ? "none" : "owed") : Unknown<string>())],
        };
    }

    /// <summary>
    /// Whether no operation the surgery work serves stands: no owed restore, no actionable elective upgrade, and no
    /// organ sale harvest (while the silver runway is short), part recovery or peg-leg step wanted.
    /// </summary>
    public static Fact<bool> SurgeryNeedRecovered(RoundsFacts f, RoundsPolicy p, bool medicineActive)
    {
        var recovered = AllFacts(SurgeryRecovered(f.MedicalPawns), Measured(ElectiveSurgeryOwed(f.MedicalPawns, f.SurgeryContextOf(), f.FabricableParts), owed => !owed));
        if (SaleHarvestWanted(f, RoundsSilverShort(f, p, medicineActive)) || PartRecoveryWanted(f) || PegCycleWanted(f, p.Prisoners()))
        {
            return Known(false);
        }
        return recovered;
    }

    /// <summary>The medicine reserve held to its target bar (decision 16): the review an unlatched restock reads. Null when the facts or thresholds are invalid.</summary>
    public static MedicalReserveReview? MedicalReserveAtTarget(MedicalReserveObservation v, MedicalReservePolicy p)
    {
        try
        {
            return ReviewMedicalReserve(v, true, p);
        }
        catch (DomainException)
        {
            return null;
        }
    }

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
            var (doctor, patient, ok) = SelectTend(d, p, reach);
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
            var (rescuer, patient, ok) = SelectRescue(r, p);
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
