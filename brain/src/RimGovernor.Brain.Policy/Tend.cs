namespace RimGovernor.Brain.Policy;

/// <summary>
/// Describes one undrafted candidate doctor. Self-tend (doctor equals patient) is out of scope here: native AI
/// already self-tends a pawn with no eligible doctor, so the controller has nothing bounded left to order.
/// </summary>
public sealed record TendDoctorFacts
{
    public PawnId Pawn { get; init; }

    public string SnapshotToken { get; init; } = "";

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> PlayerForced { get; init; }

    public Fact<uint> QueuedJobs { get; init; }

    public Fact<string> ExistingJobDef { get; init; }

    public Fact<int> MedicineSkill { get; init; }

    public Fact<bool> MedicineSkillDisabled { get; init; }

    public Fact<bool> DoctorWorkEnabled { get; init; }

    public Fact<bool> DoctorWorkOverrideDisabled { get; init; }

    /// <summary>Native control gate; it and TendCapacities (WorkGiver_Tend) must both admit the doctor.</summary>
    public Fact<bool> ControlEligible { get; init; }

    public Fact<bool> TendCapacities { get; init; }

    /// <summary>CanReach(ClosestTouch, Deadly) for this pair; multi-candidate selection uses TendReachability.</summary>
    public Fact<bool> ReachesPatient { get; init; }
}

/// <summary>
/// Answers CanReach for the doctor/patient candidates observed together in one read. A doctor with no observed
/// row reaches nothing known, so SelectTend never proposes it: an unreachable doctor is refused natively.
/// </summary>
public sealed class TendReachability
{
    private readonly Dictionary<PawnId, HashSet<PawnId>> _reach;

    private TendReachability(Dictionary<PawnId, HashSet<PawnId>> reach) => _reach = reach;

    /// <summary>The nothing-observed value (Go's zero <c>TendReachability</c>).</summary>
    public static TendReachability None { get; } = new([]);

    /// <summary>Records, per doctor, the candidate patients native reported as reachable. A doctor absent from reachable has no fact at all.</summary>
    public static TendReachability Observe(IReadOnlyDictionary<PawnId, IReadOnlyList<PawnId>> reachable)
    {
        var output = new Dictionary<PawnId, HashSet<PawnId>>(reachable.Count);
        foreach (var (doctor, patients) in reachable)
        {
            output[doctor] = [.. patients];
        }
        return new TendReachability(output);
    }

    /// <summary>Unknown when the doctor was never observed, and a known false when it was observed and the patient is not in its reachable set.</summary>
    public Fact<bool> Reaches(PawnId doctor, PawnId patient) =>
        _reach.TryGetValue(doctor, out var row) ? Known(row.Contains(patient)) : Unknown<bool>();
}

/// <summary>
/// Describes one candidate patient. HoursUntilDeathFromBloodLoss is only meaningful when Bleeding is known
/// true; callers must not treat an unknown/absent value as "not urgent" without checking Bleeding first.
/// </summary>
public sealed record TendPatientFacts
{
    public PawnId Pawn { get; init; }

    public string SnapshotToken { get; init; } = "";

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    /// <summary>
    /// With Downed decides whether the patient can be tended at all: WorkGiver_Tend tends a humanlike only in a
    /// bed and the ground tend needs a downed pawn, so an up patient out of bed is refused natively however
    /// urgent.
    /// </summary>
    public Fact<bool> InBed { get; init; }

    public Fact<bool> NeedsTend { get; init; }

    public Fact<bool> NoCare { get; init; }

    public Fact<bool> Bleeding { get; init; }

    public Fact<bool> LifeThreatening { get; init; }

    public Fact<double> HoursUntilDeathFromBloodLoss { get; init; }

    public Fact<string> ExistingJobDef { get; init; }

    /// <summary>
    /// A live hostile pawn standing within TendThreatCells of the patient (ThreatenedPatients): a doctor sent
    /// now walks into it alone, so the combat response answers first and the patient waits.
    /// </summary>
    public bool Threatened { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Ports medical_triage.treatment_pairs: rank eligible doctors by interruption cost, then descending
    /// Medicine skill (tie-break by ID), and patients by ascending bleed-out urgency, then life-threatening,
    /// then downed, then ID, and pair the first available doctor with the first available patient. This is a
    /// proposal only; EvaluateTend re-validates the chosen pair against fresh facts.
    /// </summary>
    public static (PawnId Doctor, PawnId Patient, bool Ok) SelectTend(
        IReadOnlyList<TendDoctorFacts> doctors,
        IReadOnlyList<TendPatientFacts> patients,
        TendReachability reach
    )
    {
        // allowDrafted is only tried once no undrafted doctor is eligible at all -- giving up a drafted
        // colonist's current order costs more than an idle undrafted one, so it is strictly a fallback, never
        // a first choice.
        static bool EligibleDoctor(TendDoctorFacts d, bool allowDrafted)
        {
            var dk = d.Dead.TryGetValue(out var dead);
            var wk = d.Downed.TryGetValue(out var downed);
            var tk = d.Drafted.TryGetValue(out var drafted);
            var mk = d.MentalState.TryGetValue(out var mental);
            var ek = d.ExistingJobDef.TryGetValue(out var existing);
            var sk = d.MedicineSkill.TryGetValue(out var skill);
            var sdk = d.MedicineSkillDisabled.TryGetValue(out var skillDisabled);
            var wek = d.DoctorWorkEnabled.TryGetValue(out var enabled);
            var odk = d.DoctorWorkOverrideDisabled.TryGetValue(out var overrideDisabled);
            var cek = d.ControlEligible.TryGetValue(out var controlEligible);
            var cak = d.TendCapacities.TryGetValue(out var capacities);
            if (!dk || !wk || !tk || !mk || !ek || !sk || !sdk || !wek || !odk || !cek || !cak)
            {
                return false;
            }
            return !dead && !downed && (allowDrafted || !drafted) && !mental && existing != "TendPatient" && enabled && !overrideDisabled
                && !skillDisabled && skill >= 0 && controlEligible && capacities;
        }

        static bool EligiblePatient(TendPatientFacts p)
        {
            var dk = p.Dead.TryGetValue(out var dead);
            var nk = p.NeedsTend.TryGetValue(out var needsTend);
            var ck = p.NoCare.TryGetValue(out var noCare);
            var ek = p.ExistingJobDef.TryGetValue(out var existing);
            if (!dk || !nk || !ck || !ek)
            {
                return false;
            }
            return !dead && needsTend && !noCare && !p.Threatened && existing != "TendPatient" && Tendable(p);
        }

        var patientPool = new List<TendPatientFacts>();
        var needsTendItself = new HashSet<PawnId>();
        foreach (var p in patients)
        {
            if (EligiblePatient(p))
            {
                patientPool.Add(p);
                needsTendItself.Add(p.Pawn);
            }
        }
        // A pawn that itself needs tend is never picked as someone else's doctor here -- Tend requires distinct
        // doctor/patient identities, and TendDoctorFacts' own doc comment already puts self-tend out of scope
        // (native AI self-tends a pawn with no eligible doctor on its own). Without this exclusion, a mildly ill
        // but otherwise eligible pawn (not dead, downed or mentally broken) can rank first in both pools --
        // e.g. three colonists each with a mild Flu/withdrawal condition and equal Medicine skill sort to the
        // same lowest-ID pawn in both lists -- which Tend then refuses, stalling the routine clock step on
        // repeat.
        var doctorPool = doctors.Where(d => !needsTendItself.Contains(d.Pawn) && EligibleDoctor(d, false)).ToList();
        if (patientPool.Count == 0)
        {
            return (default, default, false);
        }
        // The drafted fallback is reachability-aware too: an undrafted pool whose every member is walled off
        // from the patients is no better than an empty one, so it falls through to the drafted candidates
        // rather than reporting no pair.
        if (!AnyReachable(doctorPool, patientPool, reach))
        {
            doctorPool = [.. doctors.Where(d => !needsTendItself.Contains(d.Pawn) && EligibleDoctor(d, true))];
        }
        if (doctorPool.Count == 0)
        {
            return (default, default, false);
        }
        doctorPool =
        [
            .. doctorPool
                .OrderBy(d => OrderedWorkCost(d.PlayerForced, d.QueuedJobs))
                .ThenByDescending(d =>
                {
                    d.MedicineSkill.TryGetValue(out var skill);
                    return skill;
                })
                .ThenBy(d => d.Pawn),
        ];
        static double Hours(TendPatientFacts p)
        {
            var bleedKnown = p.Bleeding.TryGetValue(out var bleeding);
            var hoursKnown = p.HoursUntilDeathFromBloodLoss.TryGetValue(out var hours);
            return bleedKnown && bleeding && hoursKnown ? hours : double.PositiveInfinity;
        }

        static bool Life(TendPatientFacts p)
        {
            p.LifeThreatening.TryGetValue(out var life);
            return life;
        }

        static bool Down(TendPatientFacts p)
        {
            p.Downed.TryGetValue(out var down);
            return down;
        }

        patientPool = [.. patientPool.OrderBy(Hours).ThenByDescending(Life).ThenByDescending(Down).ThenBy(p => p.Pawn)];
        // Reachability is the one pairwise gate, so the pair is the first patient in urgency order some ranked
        // doctor can actually reach. With every pair reachable this is the head of each pool, the ranking above.
        foreach (var patient in patientPool)
        {
            foreach (var doctor in doctorPool)
            {
                if (reach.Reaches(doctor.Pawn, patient.Pawn).TryGetValue(out var reaches) && reaches)
                {
                    return (doctor.Pawn, patient.Pawn, true);
                }
            }
        }
        return (default, default, false);
    }

    private static bool AnyReachable(IReadOnlyList<TendDoctorFacts> doctors, IReadOnlyList<TendPatientFacts> patients, TendReachability reach)
    {
        foreach (var d in doctors)
        {
            foreach (var p in patients)
            {
                if (reach.Reaches(d.Pawn, p.Pawn).TryGetValue(out var reaches) && reaches)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether a doctor can tend the patient where they are: in a bed, or downed on the ground. Both facts must be known.</summary>
    private static bool Tendable(TendPatientFacts p)
    {
        var dk = p.Downed.TryGetValue(out var downed);
        var bk = p.InBed.TryGetValue(out var inBed);
        return dk && bk && (downed || inBed);
    }
}
