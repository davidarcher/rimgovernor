namespace RimGovernor.Brain.Runtime;

// The pawn-order family boundaries (rescue and tend) that only decode pawn rows into policy facts.

public static class RescueFacts
{
    public static RescuerFacts NewRescuerFacts(PawnId pawn, RimGovernor.Brain.GameFacts.Observations.PawnState row) => new()
    {
        Pawn = pawn,
        Dead = row.Dead,
        Downed = row.Downed,
        Drafted = row.Drafted,
        MentalState = row.MentalState is not null,
        PlayerForced = row.Job.PlayerForced,
        QueuedJobs = row.Job.QueuedJobs,
        ExistingJobDef = row.Job.DefName,
    };

    public static RescuePatientFacts NewRescuePatientFacts(PawnId pawn, RimGovernor.Brain.GameFacts.Observations.PawnState row) => new()
    {
        Pawn = pawn,
        Dead = row.Dead,
        Downed = row.Downed,
        InBed = row.InBed,
        ExistingJobDef = row.Job.DefName,
    };
}

public static class TendFacts
{
    /// <summary>A colonist always carries a Medicine skill row; one without it is a broken read.</summary>
    private static RimGovernor.Brain.GameFacts.Observations.Skill MedicineSkillOf(PawnId pawn, IEnumerable<RimGovernor.Brain.GameFacts.Observations.Skill> skills)
    {
        foreach (var s in skills)
        {
            if (s.DefName == "Medicine")
            {
                return s;
            }
        }
        throw new RequiredFactException("tend_doctor_medicine_skill:" + pawn.Value);
    }

    /// <summary>The Doctor work type of a pawn's settings: never-working pawns have none, a tracker not initialized yet has no priority to read.</summary>
    private static DoctorWork DoctorWorkOf(PawnId pawn, RimGovernor.Brain.GameFacts.Observations.PawnWork work)
    {
        if (work is RimGovernor.Brain.GameFacts.Observations.PawnWork.NeverWorks)
        {
            return DoctorWork.Unassigned;
        }
        if (work is RimGovernor.Brain.GameFacts.Observations.PawnWork.Uninitialized)
        {
            return DoctorWork.Uninitialized;
        }
        if (work is RimGovernor.Brain.GameFacts.Observations.PawnWork.Works works)
        {
            foreach (var w in works.Rows)
            {
                if (w.DefName == "Doctor")
                {
                    return !w.Disabled && w.Priority != 0 ? DoctorWork.Assigned : DoctorWork.Unassigned;
                }
            }
        }
        throw new RequiredFactException("tend_doctor_work:" + pawn.Value);
    }

    /// <summary>The patient's care policy: a configured pawn carries one, a pawn without player settings none.</summary>
    private static TendCare TendCareOf(PawnId pawn, RimGovernor.Brain.GameFacts.Observations.PawnCare care)
    {
        if (care is RimGovernor.Brain.GameFacts.Observations.PawnCare.Configured configured)
        {
            return configured.MedicalCare == RimGovernor.Brain.GameFacts.Operations.MedicalCare.NoCare ? TendCare.Withheld : TendCare.Provided;
        }
        if (care is RimGovernor.Brain.GameFacts.Observations.PawnCare.None)
        {
            return TendCare.Unmanaged;
        }
        throw new RequiredFactException("tend_patient_care:" + pawn.Value);
    }

    public static TendDoctorFacts NewTendDoctorFacts(PawnId pawn, RimGovernor.Brain.GameFacts.Observations.PawnState row, RimGovernor.Brain.GameFacts.Observations.TendDoctor doctor)
    {
        var skill = MedicineSkillOf(pawn, row.Biography.Skills);
        return new TendDoctorFacts
        {
            Pawn = pawn,
            Dead = row.Dead,
            Downed = row.Downed,
            Drafted = row.Drafted,
            MentalState = row.MentalState is not null,
            PlayerForced = row.Job.PlayerForced,
            QueuedJobs = row.Job.QueuedJobs,
            ExistingJobDef = row.Job.DefName,
            MedicineSkill = skill.Level,
            MedicineSkillDisabled = skill.Disabled,
            Work = DoctorWorkOf(pawn, row.Settings.Work),
            ControlEligible = doctor.ControlEligible,
            TendCapacities = doctor.CapacitiesOk,
        };
    }

    /// <summary>
    /// Collects the pairwise CanReach facts one tend table carries. A doctor whose reach was skipped (budget spent)
    /// has no row: <see cref="Reached"/> is false for it and it is no candidate.
    /// </summary>
    public static TendReachability TendReachabilityOf(IEnumerable<RimGovernor.Brain.GameFacts.Observations.TendDoctor> doctors)
    {
        var reachable = new Dictionary<PawnId, IReadOnlyList<PawnId>>();
        foreach (var doctor in doctors)
        {
            if (doctor.Reach is RimGovernor.Brain.GameFacts.Observations.TendReach.Read read)
            {
                reachable[new PawnId(doctor.PawnId)] = [.. read.PawnIds.Select(id => new PawnId(id))];
            }
        }
        return TendReachability.Observe(reachable);
    }

    /// <summary>Whether the doctor's reach was read; one whose reach budget ran out is skipped.</summary>
    public static bool Reached(RimGovernor.Brain.GameFacts.Observations.TendDoctor doctor) => doctor.Reach is RimGovernor.Brain.GameFacts.Observations.TendReach.Read;

    public static TendPatientFacts NewTendPatientFacts(PawnId pawn, RimGovernor.Brain.GameFacts.Observations.PawnState row, bool threatened) => new()
    {
        Pawn = pawn,
        Dead = row.Dead,
        Downed = row.Downed,
        InBed = row.InBed,
        NeedsTend = row.Health.NeedsTend,
        Care = TendCareOf(pawn, row.Settings.Care),
        BleedOut = row.Health.BleedOut,
        LifeThreatening = row.Health.LifeThreatening,
        ExistingJobDef = row.Job.DefName,
        Threatened = threatened,
    };
}
