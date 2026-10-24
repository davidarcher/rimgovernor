namespace RimGovernor.Brain.BuildingRuntime;

// The pawn-order family boundaries (rescue and tend) that only decode pawn rows into policy facts.

public static class RescueFacts
{
    public static RescuerFacts NewRescuerFacts(PawnId pawn, PbObservations.PawnState row)
    {
        var facts = new RescuerFacts
        {
            Pawn = pawn,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            Drafted = Fact.Known(row.Drafted),
            MentalState = row.MentalState is not null,
            PlayerForced = Known(row.Job.PlayerForced),
            QueuedJobs = Known(row.Job.QueuedJobs),
        };
        if (row.Job.DefName is { } jobDef)
        {
            facts = facts with { ExistingJobDef = Known(jobDef) };
        }
        return facts;
    }

    public static RescuePatientFacts NewRescuePatientFacts(PawnId pawn, PbObservations.PawnState row)
    {
        var facts = new RescuePatientFacts
        {
            Pawn = pawn,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            InBed = Fact.Known(row.InBed),
        };
        if (row.Job.DefName is { } jobDef)
        {
            facts = facts with { ExistingJobDef = Known(jobDef) };
        }
        return facts;
    }
}

public static class TendFacts
{
    private static (Fact<int> Level, Fact<bool> Disabled) MedicineSkillLevel(IEnumerable<PbObservations.Skill> skills)
    {
        foreach (var s in skills)
        {
            if (s.DefName == "Medicine")
            {
                return (Known(s.Level), Known(s.Disabled));
            }
        }
        return (Unknown<int>(), Unknown<bool>());
    }

    private static (Fact<bool> Enabled, Fact<bool> OverrideDisabled) DoctorWorkFacts(IEnumerable<PbObservations.WorkSetting> work)
    {
        foreach (var w in work)
        {
            if (w.DefName == "Doctor")
            {
                return (Known(!w.Disabled), Known(w.Priority == 0));
            }
        }
        return (Unknown<bool>(), Unknown<bool>());
    }

    public static TendDoctorFacts NewTendDoctorFacts(PawnId pawn, PbObservations.PawnState row, PbObservations.TendDoctor doctor)
    {
        var job = row.Job;
        var (level, disabled) = MedicineSkillLevel(row.Biography.Skills);
        var facts = new TendDoctorFacts
        {
            Pawn = pawn,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            Drafted = Fact.Known(row.Drafted),
            MentalState = row.MentalState is not null,
            PlayerForced = Known(job.PlayerForced),
            QueuedJobs = Known(job.QueuedJobs),
            MedicineSkill = level,
            MedicineSkillDisabled = disabled,
            ControlEligible = Known(doctor.ControlEligible),
            TendCapacities = Known(doctor.CapacitiesOk),
        };
        if (job.DefName is { } jobDef)
        {
            facts = facts with { ExistingJobDef = Known(jobDef) };
        }
        if (row.Settings.Work is PbObservations.PawnWork.Works works)
        {
            var (enabled, overrideDisabled) = DoctorWorkFacts(works.Rows);
            facts = facts with { DoctorWorkEnabled = enabled, DoctorWorkOverrideDisabled = overrideDisabled };
        }
        return facts;
    }

    /// <summary>
    /// Collects the pairwise CanReach facts one tend table carries. A doctor whose reach was skipped (budget spent)
    /// contributes no fact: the doctor is then never proposed.
    /// </summary>
    public static TendReachability TendReachabilityOf(IEnumerable<PbObservations.TendDoctor> doctors)
    {
        var reachable = new Dictionary<PawnId, IReadOnlyList<PawnId>>();
        foreach (var doctor in doctors)
        {
            if (doctor.Reach is PbObservations.TendReach.Read read)
            {
                reachable[new PawnId(doctor.PawnId)] = [.. read.PawnIds.Select(id => new PawnId(id))];
            }
        }
        return TendReachability.Observe(reachable);
    }

    public static TendPatientFacts NewTendPatientFacts(PawnId pawn, PbObservations.PawnState row)
    {
        var health = row.Health;
        var facts = new TendPatientFacts
        {
            Pawn = pawn,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            InBed = Fact.Known(row.InBed),
            NeedsTend = Known(health.NeedsTend),
            Bleeding = Known(health.Bleeding),
            LifeThreatening = Known(health.LifeThreatening),
        };
        if (row.Job.DefName is { } jobDef)
        {
            facts = facts with { ExistingJobDef = Known(jobDef) };
        }
        if (health.BleedOut is PbObservations.BleedOut.Within bleedOut)
        {
            facts = facts with { HoursUntilDeathFromBloodLoss = Known(bleedOut.Hours) };
        }
        if (row.Settings.Care is PbObservations.PawnCare.Configured care)
        {
            facts = facts with { NoCare = Known(care.MedicalCare == PbOperations.MedicalCare.NoCare) };
        }
        return facts;
    }
}
