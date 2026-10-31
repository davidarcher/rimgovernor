namespace RimGovernor.Brain.Policy;

/// <summary>How a candidate doctor's Doctor work type stands, read from the pawn's work settings.</summary>
public enum DoctorWork
{
    /// <summary>The Doctor work type is enabled with a priority above zero.</summary>
    Assigned,

    /// <summary>The pawn never works, or its Doctor work type is disabled or at priority zero: native never sends it to tend.</summary>
    Unassigned,

    /// <summary>The pawn works but its work tracker is not initialized yet, so no priority exists to read; it is not a doctor until it is.</summary>
    Uninitialized,
}

/// <summary>Whether the patient's medical care policy admits a tend.</summary>
public enum TendCare
{
    /// <summary>The care policy allows medical care.</summary>
    Provided,

    /// <summary>The care policy is NoCare: native refuses to tend the patient.</summary>
    Withheld,

    /// <summary>The pawn has no player settings, so no care policy admits a tend.</summary>
    Unmanaged,
}

/// <summary>
/// Describes one undrafted candidate doctor. Self-tend (doctor equals patient) is out of scope here: native AI
/// already self-tends a pawn with no eligible doctor, so the controller has nothing bounded left to order.
/// </summary>
public sealed record TendDoctorFacts
{
    public required PawnId Pawn { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool Drafted { get; init; }

    public required bool MentalState { get; init; }

    public required bool PlayerForced { get; init; }

    public required uint QueuedJobs { get; init; }

    /// <summary>The def of the job the pawn runs; null when it runs none.</summary>
    public required string? ExistingJobDef { get; init; }

    public required int MedicineSkill { get; init; }

    public required bool MedicineSkillDisabled { get; init; }

    public required DoctorWork Work { get; init; }

    /// <summary>Native control gate; it and TendCapacities (WorkGiver_Tend) must both admit the doctor.</summary>
    public required bool ControlEligible { get; init; }

    public required bool TendCapacities { get; init; }
}

/// <summary>
/// Answers CanReach for the doctor/patient candidates observed together in one read. Only a doctor whose reach was
/// read is a candidate (one whose reach budget ran out is none), so a doctor with no row reaches nothing.
/// </summary>
public sealed class TendReachability
{
    private readonly Dictionary<PawnId, HashSet<PawnId>> _reach;

    private TendReachability(Dictionary<PawnId, HashSet<PawnId>> reach) => _reach = reach;

    /// <summary>The nothing-observed value.</summary>
    public static TendReachability None { get; } = new([]);

    /// <summary>Records, per doctor, the candidate patients native reported as reachable.</summary>
    public static TendReachability Observe(IReadOnlyDictionary<PawnId, IReadOnlyList<PawnId>> reachable)
    {
        var output = new Dictionary<PawnId, HashSet<PawnId>>(reachable.Count);
        foreach (var (doctor, patients) in reachable)
        {
            output[doctor] = [.. patients];
        }
        return new TendReachability(output);
    }

    /// <summary>Whether the doctor's observed reachable set holds the patient.</summary>
    public bool Reaches(PawnId doctor, PawnId patient) => _reach.TryGetValue(doctor, out var row) && row.Contains(patient);
}

/// <summary>
/// Describes one candidate patient. BleedOut ranks the urgent first: a patient bleeding toward a finite death
/// goes by its hours, any other after them.
/// </summary>
public sealed record TendPatientFacts
{
    public required PawnId Pawn { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    /// <summary>
    /// With Downed decides whether the patient can be tended at all: WorkGiver_Tend tends a humanlike only in a
    /// bed and the ground tend needs a downed pawn, so an up patient out of bed is refused natively however
    /// urgent.
    /// </summary>
    public required bool InBed { get; init; }

    public required bool NeedsTend { get; init; }

    public required TendCare Care { get; init; }

    public required BleedOut BleedOut { get; init; }

    public required bool LifeThreatening { get; init; }

    /// <summary>The def of the job the pawn runs; null when it runs none.</summary>
    public required string? ExistingJobDef { get; init; }

    /// <summary>
    /// A live hostile pawn standing within TendThreatCells of the patient (ThreatenedPatients): a doctor sent
    /// now walks into it alone, so the combat response answers first and the patient waits.
    /// </summary>
    public required bool Threatened { get; init; }
}

public static partial class TendRules
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
        static bool EligibleDoctor(TendDoctorFacts d, bool allowDrafted) =>
            !d.Dead && !d.Downed && (allowDrafted || !d.Drafted) && !d.MentalState && d.ExistingJobDef != "TendPatient"
            && d.Work == DoctorWork.Assigned && !d.MedicineSkillDisabled && d.MedicineSkill >= 0 && d.ControlEligible && d.TendCapacities;

        static bool EligiblePatient(TendPatientFacts p) =>
            !p.Dead && p.NeedsTend && p.Care == TendCare.Provided && !p.Threatened && p.ExistingJobDef != "TendPatient" && Tendable(p);

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
        doctorPool = [.. doctorPool.OrderBy(d => OrderedWorkRules.OrderedWorkCost(d.PlayerForced, d.QueuedJobs)).ThenByDescending(d => d.MedicineSkill).ThenBy(d => d.Pawn)];
        patientPool = [.. patientPool.OrderBy(Hours).ThenByDescending(p => p.LifeThreatening).ThenByDescending(p => p.Downed).ThenBy(p => p.Pawn)];
        // Reachability is the one pairwise gate, so the pair is the first patient in urgency order some ranked
        // doctor can actually reach. With every pair reachable this is the head of each pool, the ranking above.
        foreach (var patient in patientPool)
        {
            foreach (var doctor in doctorPool)
            {
                if (reach.Reaches(doctor.Pawn, patient.Pawn))
                {
                    return (doctor.Pawn, patient.Pawn, true);
                }
            }
        }
        return (default, default, false);
    }

    /// <summary>Hours to the finite death a bleeding patient faces; the patients bleeding toward none rank after every one that does.</summary>
    private static double Hours(TendPatientFacts p) => p.BleedOut is BleedOut.Within within ? within.Hours : double.PositiveInfinity;

    private static bool AnyReachable(IReadOnlyList<TendDoctorFacts> doctors, IReadOnlyList<TendPatientFacts> patients, TendReachability reach)
    {
        foreach (var d in doctors)
        {
            foreach (var p in patients)
            {
                if (reach.Reaches(d.Pawn, p.Pawn))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether the patient's tend is an observed emergency an exact order may answer (RG-LABOR-004, task 5.5 of
    /// <c>native-led-labor-execution</c>): the patient is downed, bleeding toward a finite death or carrying a life-threatening
    /// condition. A patient who only needs tend (in bed, not bleeding out, nothing life-threatening) is ordinary native Doctor
    /// work, which work settings and the native medical-care setting run.</summary>
    public static bool Emergency(TendPatientFacts p) => p.Downed || p.BleedOut is BleedOut.Within || p.LifeThreatening;

    /// <summary>Whether a doctor can tend the patient where they are: in a bed, or downed on the ground.</summary>
    private static bool Tendable(TendPatientFacts p) => p.Downed || p.InBed;
}
