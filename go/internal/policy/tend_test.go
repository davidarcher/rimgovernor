package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tendRequest(t *testing.T) TendRequest {
	t.Helper()
	tend, _ := domain.NewTend("doctor", "patient")
	a, _ := domain.NewTendAction("tend", tend)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	e, err := NewEmergencySnapshot(s, 13, EmergencyFacts{
		ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
		Colonists: []EmergencyPawn{
			{ID: "doctor", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
			{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	doctor := TendDoctorFacts{Pawn: "doctor", SnapshotToken: "doctor-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known(""), MedicineSkill: domain.Known(int32(8)), MedicineSkillDisabled: domain.Known(false), DoctorWorkEnabled: domain.Known(true), DoctorWorkOverrideDisabled: domain.Known(false)}
	patient := TendPatientFacts{Pawn: "patient", SnapshotToken: "patient-cas", Dead: domain.Known(false), Downed: domain.Known(false), NeedsTend: domain.Known(true), NoCare: domain.Known(false), Bleeding: domain.Known(true), LifeThreatening: domain.Known(false), HoursUntilDeathFromBloodLoss: domain.Known(6.0), ExistingJobDef: domain.Known("")}
	return TendRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: TendFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Doctor: doctor, Patient: patient}}
}

func TestTendAdmission(t *testing.T) {
	r := tendRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateTend(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestTendDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*TendRequest)
	}{
		{"zero action", func(r *TendRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *TendRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *TendRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *TendRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *TendRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *TendRequest) { r.Facts.PreviewTick = 11 }},
		{"old emergency", func(r *TendRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *TendRequest) { r.Current.Native = 0 }},
		{"native", func(r *TendRequest) { r.Current.Native++ }},
		{"direction", func(r *TendRequest) { r.Current.Direction++ }},
		{"colony", func(r *TendRequest) { r.Current.Colony = "other" }},
		{"load", func(r *TendRequest) { r.Current.Load = "other" }},
		{"map", func(r *TendRequest) { r.Current.Map++ }},
		{"plan", func(r *TendRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *TendRequest) { r.Current.Revision++ }},
		{"doctor CAS", func(r *TendRequest) { r.Facts.Doctor.SnapshotToken = "" }},
		{"patient CAS", func(r *TendRequest) { r.Facts.Patient.SnapshotToken = "" }},
		{"wrong doctor", func(r *TendRequest) { r.Facts.Doctor.Pawn = "other" }},
		{"wrong patient", func(r *TendRequest) { r.Facts.Patient.Pawn = "other" }},
		{"doctor dead", func(r *TendRequest) { r.Facts.Doctor.Dead = domain.Known(true) }},
		{"doctor downed", func(r *TendRequest) { r.Facts.Doctor.Downed = domain.Known(true) }},
		{"doctor drafted unknown", func(r *TendRequest) { r.Facts.Doctor.Drafted = domain.Unknown[bool]() }},
		{"doctor mental state", func(r *TendRequest) { r.Facts.Doctor.MentalState = domain.Known(true) }},
		{"doctor already tending", func(r *TendRequest) { r.Facts.Doctor.ExistingJobDef = domain.Known("TendPatient") }},
		{"doctor unknown skill", func(r *TendRequest) { r.Facts.Doctor.MedicineSkill = domain.Unknown[int32]() }},
		{"doctor skill disabled", func(r *TendRequest) { r.Facts.Doctor.MedicineSkillDisabled = domain.Known(true) }},
		{"doctor work disabled", func(r *TendRequest) { r.Facts.Doctor.DoctorWorkEnabled = domain.Known(false) }},
		{"doctor work override zero", func(r *TendRequest) { r.Facts.Doctor.DoctorWorkOverrideDisabled = domain.Known(true) }},
		{"patient dead", func(r *TendRequest) { r.Facts.Patient.Dead = domain.Known(true) }},
		{"patient recovered", func(r *TendRequest) { r.Facts.Patient.NeedsTend = domain.Known(false) }},
		{"patient unknown needs tend", func(r *TendRequest) { r.Facts.Patient.NeedsTend = domain.Unknown[bool]() }},
		{"patient no care", func(r *TendRequest) { r.Facts.Patient.NoCare = domain.Known(true) }},
		{"patient already tended", func(r *TendRequest) { r.Facts.Patient.ExistingJobDef = domain.Known("TendPatient") }},
		{"preview refusal", func(r *TendRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *TendRequest) { r.Facts.Emergency.facts.ColonistsComplete = domain.Known(false) }},
		{"unsafe threat", func(r *TendRequest) {
			r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := tendRequest(t)
			c.change(&r)
			d := EvaluateTend(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

// The patient's own untended/bleeding state must never refuse its own tend
// action; that is precisely the emergency this action resolves.
func TestTendIgnoresItsOwnCriticalMedicalHold(t *testing.T) {
	r := tendRequest(t)
	if d := EvaluateTend(r); !d.Admitted {
		t.Fatal("tend refused by the emergency it exists to clear", d)
	}
}

// A drafted doctor is the controller's own fallback for the deadlock the
// issue describes: every doctor drafted (e.g. for defense) while a hostile
// remains anywhere on the map. Native's own job-acceptance check (NativeCanTry)
// stays the live safety authority; RimGovernor's colony-wide hostile gate does
// not block this doctor the way it still blocks an undrafted one.
func TestTendAdmitsDraftedDoctorDespiteHostile(t *testing.T) {
	r := tendRequest(t)
	r.Facts.Doctor.Drafted = domain.Known(true)
	r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
	if d := EvaluateTend(r); !d.Admitted {
		t.Fatal("drafted doctor refused despite controller-owned fallback", d)
	}
}

// An undrafted doctor still cannot tend while a hostile remains -- only the
// drafted fallback exempts the colony-wide hostile gate.
func TestTendStillBlocksUndraftedDoctorDuringHostile(t *testing.T) {
	r := tendRequest(t)
	r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
	d := EvaluateTend(r)
	if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != UnsupportedThreat {
		t.Fatal(d)
	}
}

func tendDoctor(id domain.PawnID, skill int32) TendDoctorFacts {
	return TendDoctorFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known(""), MedicineSkill: domain.Known(skill), MedicineSkillDisabled: domain.Known(false), DoctorWorkEnabled: domain.Known(true), DoctorWorkOverrideDisabled: domain.Known(false)}
}
func tendPatient(id domain.PawnID, hours float64) TendPatientFacts {
	return TendPatientFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), NeedsTend: domain.Known(true), NoCare: domain.Known(false), Bleeding: domain.Known(true), LifeThreatening: domain.Known(false), HoursUntilDeathFromBloodLoss: domain.Known(hours), ExistingJobDef: domain.Known("")}
}

func TestSelectTendRanksBySkillAndUrgency(t *testing.T) {
	doctors := []TendDoctorFacts{tendDoctor("junior", 4), tendDoctor("senior", 12)}
	patients := []TendPatientFacts{tendPatient("stable", 40), tendPatient("critical", 2)}
	doctor, patient, ok := SelectTend(doctors, patients)
	if !ok || doctor != "senior" || patient != "critical" {
		t.Fatal(doctor, patient, ok)
	}
}
func TestSelectTendExcludesIneligibleCandidates(t *testing.T) {
	busy := tendDoctor("busy", 10)
	busy.ExistingJobDef = domain.Known("TendPatient")
	drafted := tendDoctor("drafted", 10)
	drafted.Drafted = domain.Known(true)
	fine := tendDoctor("fine", 6)
	recovered := tendPatient("recovered", 10)
	recovered.NeedsTend = domain.Known(false)
	noCare := tendPatient("nocare", 10)
	noCare.NoCare = domain.Known(true)
	sick := tendPatient("sick", 10)
	doctor, patient, ok := SelectTend([]TendDoctorFacts{busy, drafted, fine}, []TendPatientFacts{recovered, noCare, sick})
	if !ok || doctor != "fine" || patient != "sick" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := SelectTend([]TendDoctorFacts{fine}, []TendPatientFacts{recovered, noCare}); ok {
		t.Fatal("selected an ineligible patient")
	}
}

// With no undrafted doctor eligible at all, SelectTend falls back to a
// drafted one rather than leaving the patient untreated.
func TestSelectTendFallsBackToDraftedDoctor(t *testing.T) {
	busy := tendDoctor("busy", 10)
	busy.ExistingJobDef = domain.Known("TendPatient")
	drafted := tendDoctor("drafted", 10)
	drafted.Drafted = domain.Known(true)
	sick := tendPatient("sick", 10)
	doctor, patient, ok := SelectTend([]TendDoctorFacts{busy, drafted}, []TendPatientFacts{sick})
	if !ok || doctor != "drafted" || patient != "sick" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := SelectTend([]TendDoctorFacts{busy}, []TendPatientFacts{sick}); ok {
		t.Fatal("selected an ineligible doctor")
	}
}

// A mildly ill pawn (not dead, downed or mentally broken) is nominally an
// eligible doctor by TendDoctorFacts alone, but must never be selected as its
// own doctor: domain.NewTend refuses equal doctor/patient identities, and
// live acceptance evidence (three colonists all mildly ill with equal
// Medicine skill, so the same lowest-ID pawn topped both pools) showed this
// stalling the routine clock step on repeat rather than refusing cleanly.
func TestSelectTendNeverPicksAPatientAsItsOwnDoctor(t *testing.T) {
	self := tendDoctor("self", 10)
	other := tendDoctor("other", 10)
	selfAsPatient := tendPatient("self", 10)
	doctor, patient, ok := SelectTend([]TendDoctorFacts{self, other}, []TendPatientFacts{selfAsPatient})
	if !ok || doctor != "other" || patient != "self" {
		t.Fatal(doctor, patient, ok)
	}
	if _, _, ok := SelectTend([]TendDoctorFacts{self}, []TendPatientFacts{selfAsPatient}); ok {
		t.Fatal("selected a pawn as its own doctor")
	}
}
