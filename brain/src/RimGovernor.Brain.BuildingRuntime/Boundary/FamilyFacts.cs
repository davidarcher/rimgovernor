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
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Prisoner = Boundary.FactBool(row.HasPrisoner, row.Prisoner),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "def_name") && job.HasDefName)
        {
            facts = facts with { ExistingJobDef = Known(job.DefName) };
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
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Drafted = Boundary.FactBool(row.HasDrafted, row.Drafted),
            MentalState = Boundary.FactPresence(row.HasMentalState, row.Issues, "mental_state"),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "player_forced") && !Boundary.IssueField(job.Issues, "queued_jobs") && !Boundary.IssueField(job.Issues, "def_name"))
        {
            facts = facts with
            {
                PlayerForced = Boundary.FactBool(job.HasPlayerForced, job.PlayerForced),
                QueuedJobs = Boundary.FactUint(job.HasQueuedJobs, job.QueuedJobs),
            };
            if (job.HasDefName)
            {
                facts = facts with { ExistingJobDef = Known(job.DefName) };
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
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            InBed = Boundary.FactBool(row.HasInBed, row.InBed),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "def_name") && job.HasDefName)
        {
            facts = facts with { ExistingJobDef = Known(job.DefName) };
        }
        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health") && health.HasBedId)
        {
            facts = facts with { BedId = Known(health.BedId) };
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
            if (s is null || !s.HasDefName)
            {
                continue;
            }
            if (s.DefName == "Medicine")
            {
                var disabled = s.HasDisabled ? Known(s.Disabled) : Unknown<bool>();
                var level = s.HasLevel ? Known(s.Level) : Unknown<int>();
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
            if (w.DefName == "Doctor")
            {
                if (!w.HasDisabled || !w.HasPriority)
                {
                    return (Unknown<bool>(), Unknown<bool>());
                }
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
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            Drafted = Boundary.FactBool(row.HasDrafted, row.Drafted),
            MentalState = Boundary.FactPresence(row.HasMentalState, row.Issues, "mental_state"),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "player_forced") && !Boundary.IssueField(job.Issues, "queued_jobs") && !Boundary.IssueField(job.Issues, "def_name"))
        {
            facts = facts with
            {
                PlayerForced = Boundary.FactBool(job.HasPlayerForced, job.PlayerForced),
                QueuedJobs = Boundary.FactUint(job.HasQueuedJobs, job.QueuedJobs),
            };
            if (job.HasDefName)
            {
                facts = facts with { ExistingJobDef = Known(job.DefName) };
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
                ControlEligible = Boundary.FactBool(doctor.HasControlEligible, doctor.ControlEligible),
                TendCapacities = Boundary.FactBool(doctor.HasCapacitiesOk, doctor.CapacitiesOk),
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
            if (row.Pawn is null || doctor is null || doctor.ReachSkipped || Boundary.IssueField(doctor.Issues, "reachable_pawn_ids"))
            {
                continue;
            }
            reachable[new PawnId(row.Pawn.Id)] = [.. doctor.ReachablePawnIds.Select(id => new PawnId(id))];
        }
        return TendReachability.Observe(reachable);
    }

    public static TendPatientFacts NewTendPatientFacts(PawnId pawn, PbObservations.PawnState row, string token)
    {
        var facts = new TendPatientFacts
        {
            Pawn = pawn,
            SnapshotToken = token,
            Dead = Boundary.FactBool(row.HasDead, row.Dead),
            Downed = Boundary.FactBool(row.HasDowned, row.Downed),
            InBed = Boundary.FactBool(row.HasInBed, row.InBed),
        };
        if (row.Job is { } job && !Boundary.IssueField(job.Issues, "def_name") && job.HasDefName)
        {
            facts = facts with { ExistingJobDef = Known(job.DefName) };
        }
        if (row.Health is { } health && !Boundary.IssueField(health.Issues, "health"))
        {
            facts = facts with
            {
                NeedsTend = Boundary.FactBool(health.HasNeedsTend, health.NeedsTend),
                Bleeding = Boundary.FactBool(health.HasBleeding, health.Bleeding),
                LifeThreatening = Boundary.FactBool(health.HasLifeThreatening, health.LifeThreatening),
            };
            if (health.HasHoursUntilDeathFromBloodLoss)
            {
                facts = facts with { HoursUntilDeathFromBloodLoss = Known(health.HoursUntilDeathFromBloodLoss) };
            }
        }
        if (row.Settings is { } settings && !Boundary.IssueField(settings.Issues, "medical_care") && settings.HasMedicalCare)
        {
            facts = facts with { NoCare = Known(settings.MedicalCare == PbOperations.MedicalCare.NoCare) };
        }
        return facts;
    }
}
