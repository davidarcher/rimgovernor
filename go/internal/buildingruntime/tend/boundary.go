package tend

import (
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func medicineSkillLevel(skills []*n.Skill) (level domain.Fact[int32], disabled domain.Fact[bool]) {
	for _, s := range skills {
		if s == nil || s.Definition == nil {
			continue
		}
		if s.Definition.GetDefName() == "Medicine" {
			if s.Disabled != nil {
				disabled = domain.Known(s.GetDisabled())
			}
			if s.Level != nil {
				level = domain.Known(s.GetLevel())
			}
			return level, disabled
		}
	}
	return domain.Unknown[int32](), domain.Unknown[bool]()
}
func doctorWorkFacts(work []*n.WorkSetting) (enabled domain.Fact[bool], overrideDisabled domain.Fact[bool]) {
	for _, w := range work {
		if w == nil {
			continue
		}
		if w.GetDefName() == "Doctor" {
			if w.Disabled == nil || w.Priority == nil {
				return domain.Unknown[bool](), domain.Unknown[bool]()
			}
			return domain.Known(!w.GetDisabled()), domain.Known(w.GetPriority() == 0)
		}
	}
	return domain.Unknown[bool](), domain.Unknown[bool]()
}

func NewTendDoctorFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.TendDoctorFacts {
	facts := policy.TendDoctorFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") && !boundary.IssueField(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	if biography := row.Biography; biography != nil && !boundary.IssueField(biography.Issues, "skills") {
		facts.MedicineSkill, facts.MedicineSkillDisabled = medicineSkillLevel(biography.Skills)
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "work") {
		facts.DoctorWorkEnabled, facts.DoctorWorkOverrideDisabled = doctorWorkFacts(settings.Work)
	}
	if doctor := row.TendDoctor; doctor != nil {
		facts.ControlEligible = boundary.FactBool(doctor.ControlEligible)
		facts.TendCapacities = boundary.FactBool(doctor.CapacitiesOk)
	}
	return facts
}

// TendReachability collects the pairwise CanReach facts one list_pawns reply
// carries (#657). A row without the tend detail, or whose reachability read
// carried an issue, contributes no fact: the doctor is then never proposed.
func TendReachability(rows []*n.PawnState) policy.TendReachability {
	reachable := map[domain.PawnID][]domain.PawnID{}
	for _, row := range rows {
		doctor := row.GetTendDoctor()
		if row.Pawn == nil || doctor == nil || boundary.IssueField(doctor.Issues, "reachable_pawn_ids") {
			continue
		}
		patients := make([]domain.PawnID, 0, len(doctor.ReachablePawnIds))
		for _, id := range doctor.ReachablePawnIds {
			patients = append(patients, domain.PawnID(id))
		}
		reachable[domain.PawnID(row.Pawn.GetId())] = patients
	}
	return policy.ObserveTendReachability(reachable)
}
func NewTendPatientFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.TendPatientFacts {
	facts := policy.TendPatientFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), InBed: boundary.FactBool(row.InBed)}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") && row.Job.DefName != nil {
		facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
	}
	if health := row.Health; health != nil && !boundary.IssueField(health.Issues, "health") {
		facts.NeedsTend, facts.Bleeding, facts.LifeThreatening = boundary.FactBool(health.NeedsTend), boundary.FactBool(health.Bleeding), boundary.FactBool(health.LifeThreatening)
		if health.HoursUntilDeathFromBloodLoss != nil {
			facts.HoursUntilDeathFromBloodLoss = domain.Known(health.GetHoursUntilDeathFromBloodLoss())
		}
	}
	if settings := row.Settings; settings != nil && !boundary.IssueField(settings.Issues, "medical_care") && settings.MedicalCare != nil {
		facts.NoCare = domain.Known(settings.GetMedicalCare() == op.MedicalCare_MEDICAL_CARE_NO_CARE)
	}
	return facts
}
