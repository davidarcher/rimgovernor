namespace RimGovernor.Brain.BuildingRuntime;

// Ports of the pawn-order family boundaries that only decode pawn rows into policy facts:
// internal/buildingruntime/capture, rescue and tend (boundary.go each).

/// <summary>Go package <c>capture</c>.</summary>
public static class CaptureFacts
{
    /// <summary>Reuses Rescue's rescuer facts shape: capturer eligibility (undrafted, not mid-mental-break, no queued player work) is identical.</summary>
    public static RescuerFacts NewCapturerFacts(PawnId pawn, PbObservations.PawnState row, string token) => RescueFacts.NewRescuerFacts(pawn, row, token);

    public static CapturePatientFacts NewCapturePatientFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new CapturePatientFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
            Prisoner = Boundary.FactBool(row.Prisoner is not null, row.Prisoner.GetValueOrDefault()),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "def_name") && job.DefName is not null)
        {
            facts = facts with { ExistingJobDef = Known((job.DefName ?? "")) };
        }
        return facts;
    }
}

/// <summary>Go package <c>rescue</c>.</summary>
public static class RescueFacts
{
    public static RescuerFacts NewRescuerFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new RescuerFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
            Drafted = Boundary.FactBool(row.Drafted is not null, row.Drafted.GetValueOrDefault()),
            MentalState = Boundary.FactPresence(row.MentalState is not null, row.Issues, "mental_state"),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "player_forced") && !Boundary.IssueField(job.Issues, "queued_jobs") && !Boundary.IssueField(job.Issues, "def_name"))
        {
            facts = facts with
            {
                PlayerForced = Boundary.FactBool(job.PlayerForced is not null, job.PlayerForced.GetValueOrDefault()),
                QueuedJobs = Boundary.FactUint(job.QueuedJobs is not null, job.QueuedJobs.GetValueOrDefault()),
            };
            if (job.DefName is not null)
            {
                facts = facts with { ExistingJobDef = Known((job.DefName ?? "")) };
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
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
            InBed = Boundary.FactBool(row.InBed is not null, row.InBed.GetValueOrDefault()),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "def_name") && job.DefName is not null)
        {
            facts = facts with { ExistingJobDef = Known((job.DefName ?? "")) };
        }
        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health") && health.BedId is not null)
        {
            facts = facts with { BedID = Known((health.BedId ?? "")) };
        }
        return facts;
    }
}

/// <summary>Go package <c>tend</c>.</summary>
public static class TendFacts
{
    private static (Fact<int> Level, Fact<bool> Disabled) MedicineSkillLevel(IEnumerable<PbObservations.Skill> skills)
    {
        foreach (var s in skills)
        {
            if (s is null || s.DefName is null)
            {
                continue;
            }
            if ((s.DefName ?? "") == "Medicine")
            {
                var disabled = s.Disabled is not null ? Known(s.Disabled.GetValueOrDefault()) : Unknown<bool>();
                var level = s.Level is not null ? Known(s.Level.GetValueOrDefault()) : Unknown<int>();
                return (level, disabled);
            }
        }
        return (Unknown<int>(), Unknown<bool>());
    }

    private static (Fact<bool> Enabled, Fact<bool> OverrideDisabled) DoctorWorkFacts(IEnumerable<PbObservations.WorkSetting> work)
    {
        foreach (var w in work)
        {
            if (w is null)
            {
                continue;
            }
            if ((w.DefName ?? "") == "Doctor")
            {
                if (w.Disabled is null || w.Priority is null)
                {
                    return (Unknown<bool>(), Unknown<bool>());
                }
                return (Known(!w.Disabled.GetValueOrDefault()), Known(w.Priority.GetValueOrDefault() == 0));
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
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
            Drafted = Boundary.FactBool(row.Drafted is not null, row.Drafted.GetValueOrDefault()),
            MentalState = Boundary.FactPresence(row.MentalState is not null, row.Issues, "mental_state"),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "player_forced") && !Boundary.IssueField(job.Issues, "queued_jobs") && !Boundary.IssueField(job.Issues, "def_name"))
        {
            facts = facts with
            {
                PlayerForced = Boundary.FactBool(job.PlayerForced is not null, job.PlayerForced.GetValueOrDefault()),
                QueuedJobs = Boundary.FactUint(job.QueuedJobs is not null, job.QueuedJobs.GetValueOrDefault()),
            };
            if (job.DefName is not null)
            {
                facts = facts with { ExistingJobDef = Known((job.DefName ?? "")) };
            }
        }
        if (row.Biography is { } biography && !Boundary.IssueField(biography.Issues, "skills"))
        {
            var (level, disabled) = MedicineSkillLevel(biography.Skills);
            facts = facts with { MedicineSkill = level, MedicineSkillDisabled = disabled };
        }
        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "work"))
        {
            var (enabled, overrideDisabled) = DoctorWorkFacts(settings.Work);
            facts = facts with { DoctorWorkEnabled = enabled, DoctorWorkOverrideDisabled = overrideDisabled };
        }
        if (row.TendDoctor is { } doctor)
        {
            facts = facts with
            {
                ControlEligible = Boundary.FactBool(doctor.ControlEligible is not null, doctor.ControlEligible.GetValueOrDefault()),
                TendCapacities = Boundary.FactBool(doctor.CapacitiesOk is not null, doctor.CapacitiesOk.GetValueOrDefault()),
            };
        }
        return facts;
    }

    /// <summary>
    /// Collects the pairwise CanReach facts one list_pawns reply carries. A row without the tend detail, or whose
    /// reachability read carried an issue or reach_skipped (budget spent), contributes no fact: the doctor is then
    /// never proposed.
    /// </summary>
    public static TendReachability TendReachabilityOf(IEnumerable<PbObservations.PawnState> rows)
    {
        var reachable = new Dictionary<PawnId, IReadOnlyList<PawnId>>();
        foreach (var row in rows)
        {
            var doctor = row.TendDoctor;
            if (row.Pawn is null || doctor is null || doctor.ReachSkipped.GetValueOrDefault() || Boundary.IssueField(doctor.Issues, "reachable_pawn_ids"))
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
            Dead = Boundary.FactBool(row.Dead is not null, row.Dead.GetValueOrDefault()),
            Downed = Boundary.FactBool(row.Downed is not null, row.Downed.GetValueOrDefault()),
            InBed = Boundary.FactBool(row.InBed is not null, row.InBed.GetValueOrDefault()),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "def_name") && job.DefName is not null)
        {
            facts = facts with { ExistingJobDef = Known((job.DefName ?? "")) };
        }
        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health"))
        {
            facts = facts with
            {
                NeedsTend = Boundary.FactBool(health.NeedsTend is not null, health.NeedsTend.GetValueOrDefault()),
                Bleeding = Boundary.FactBool(health.Bleeding is not null, health.Bleeding.GetValueOrDefault()),
                LifeThreatening = Boundary.FactBool(health.LifeThreatening is not null, health.LifeThreatening.GetValueOrDefault()),
            };
            if (health.HoursUntilDeathFromBloodLoss is not null)
            {
                facts = facts with { HoursUntilDeathFromBloodLoss = Known(health.HoursUntilDeathFromBloodLoss.GetValueOrDefault()) };
            }
        }
        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "medical_care") && settings.MedicalCare is not null)
        {
            facts = facts with { NoCare = Known(settings.MedicalCare.GetValueOrDefault() == PbOperations.MedicalCare.NoCare) };
        }
        return facts;
    }
}
