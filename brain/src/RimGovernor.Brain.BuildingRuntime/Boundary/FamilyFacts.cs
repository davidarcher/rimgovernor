namespace RimGovernor.Brain.BuildingRuntime;

// The pawn-order family boundaries (rescue and tend) that only decode pawn rows into policy facts.

public static class RescueFacts
{
    public static RescuerFacts NewRescuerFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new RescuerFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            Drafted = Fact.Known(row.Drafted),
            MentalState = row.MentalState is not null,
        };
        if (row.Job is { } job)
        {
            facts = facts with
            {
                PlayerForced = Known(job.PlayerForced),
                QueuedJobs = Known(job.QueuedJobs),
            };
            if (job.DefName is { } jobDef)
            {
                facts = facts with { ExistingJobDef = Known(jobDef) };
            }
        }
        return facts;
    }

    public static RescuePatientFacts NewRescuePatientFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new RescuePatientFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            InBed = Fact.Known(row.InBed),
        };
        if (row.Job?.DefName is { } jobDef)
        {
            facts = facts with { ExistingJobDef = Known(jobDef) };
        }
        if (row.Health?.BedId is { } bed)
        {
            facts = facts with { BedID = Known(bed) };
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

    public static TendDoctorFacts NewTendDoctorFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new TendDoctorFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            Drafted = Fact.Known(row.Drafted),
            MentalState = row.MentalState is not null,
        };
        if (row.Job is { } job)
        {
            facts = facts with
            {
                PlayerForced = Known(job.PlayerForced),
                QueuedJobs = Known(job.QueuedJobs),
            };
            if (job.DefName is { } jobDef)
            {
                facts = facts with { ExistingJobDef = Known(jobDef) };
            }
        }
        if (row.Biography is { } biography)
        {
            var (level, disabled) = MedicineSkillLevel(biography.Skills);
            facts = facts with { MedicineSkill = level, MedicineSkillDisabled = disabled };
        }
        if (row.Settings is { WorkApplies: true } settings)
        {
            var (enabled, overrideDisabled) = DoctorWorkFacts(settings.Work);
            facts = facts with { DoctorWorkEnabled = enabled, DoctorWorkOverrideDisabled = overrideDisabled };
        }
        if (row.TendDoctor is { } doctor)
        {
            facts = facts with
            {
                ControlEligible = Known(doctor.ControlEligible),
                TendCapacities = Known(doctor.CapacitiesOk),
            };
        }
        return facts;
    }

    /// <summary>
    /// Collects the pairwise CanReach facts one list_pawns reply carries. A row without the tend detail, or whose
    /// reach_skipped is set (budget spent), contributes no fact: the doctor is then
    /// never proposed.
    /// </summary>
    public static TendReachability TendReachabilityOf(IEnumerable<PbObservations.PawnState> rows)
    {
        var reachable = new Dictionary<PawnId, IReadOnlyList<PawnId>>();
        foreach (var row in rows)
        {
            var doctor = row.TendDoctor;
            if (doctor is null || doctor.ReachSkipped)
            {
                continue;
            }
            reachable[new PawnId((row.Pawn.Id ?? ""))] = [.. doctor.ReachablePawnIds.Select(id => new PawnId(id))];
        }
        return TendReachability.Observe(reachable);
    }

    public static TendPatientFacts NewTendPatientFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new TendPatientFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Fact.Known(row.Dead),
            Downed = Fact.Known(row.Downed),
            InBed = Fact.Known(row.InBed),
        };
        if (row.Job?.DefName is { } jobDef)
        {
            facts = facts with { ExistingJobDef = Known(jobDef) };
        }
        if (row.Health is { } health)
        {
            facts = facts with
            {
                NeedsTend = Known(health.NeedsTend),
                Bleeding = Known(health.Bleeding),
                LifeThreatening = Known(health.LifeThreatening),
            };
            if (health.HoursUntilDeathFromBloodLoss is not null)
            {
                facts = facts with { HoursUntilDeathFromBloodLoss = Known(health.HoursUntilDeathFromBloodLoss.GetValueOrDefault()) };
            }
        }
        if (row.Settings is { MedicalCare: not null } settings)
        {
            facts = facts with { NoCare = Known(settings.MedicalCare.GetValueOrDefault() == PbOperations.MedicalCare.NoCare) };
        }
        return facts;
    }
}
