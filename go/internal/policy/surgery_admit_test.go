package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func surgeryRequest(t *testing.T) SurgeryRequest {
	t.Helper()
	surgery, _ := domain.NewSurgery("patient", "RemoveBodyPart", 3)
	a, _ := domain.NewSurgeryAction("surgery", surgery)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	patient := SurgeryPatientFacts{Patient: "patient", SnapshotToken: "patient-cas", Dead: domain.Known(false), Downed: domain.Known(false)}
	facts := SurgeryFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Patient: patient, HealthToken: "health-cas", Care: domain.MedicalCareNormalOrWorse, NativeCanTry: domain.Known(true)}
	return SurgeryRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: facts}
}

func TestSurgeryAdmission(t *testing.T) {
	r := surgeryRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateSurgery(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestSurgeryDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*SurgeryRequest)
	}{
		{"zero action", func(r *SurgeryRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *SurgeryRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *SurgeryRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *SurgeryRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *SurgeryRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *SurgeryRequest) { r.Facts.PreviewTick = 11 }},
		{"zero generation", func(r *SurgeryRequest) { r.Current.Native = 0 }},
		{"native", func(r *SurgeryRequest) { r.Current.Native++ }},
		{"direction", func(r *SurgeryRequest) { r.Current.Direction++ }},
		{"colony", func(r *SurgeryRequest) { r.Current.Colony = "other" }},
		{"load", func(r *SurgeryRequest) { r.Current.Load = "other" }},
		{"map", func(r *SurgeryRequest) { r.Current.Map++ }},
		{"plan", func(r *SurgeryRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *SurgeryRequest) { r.Current.Revision++ }},
		{"patient CAS", func(r *SurgeryRequest) { r.Facts.Patient.SnapshotToken = "" }},
		{"health CAS", func(r *SurgeryRequest) { r.Facts.HealthToken = "" }},
		{"wrong patient", func(r *SurgeryRequest) { r.Facts.Patient.Patient = "other" }},
		{"invalid care", func(r *SurgeryRequest) { r.Facts.Care = domain.MedicalCare("bogus") }},
		{"patient dead", func(r *SurgeryRequest) { r.Facts.Patient.Dead = domain.Known(true) }},
		{"patient downed", func(r *SurgeryRequest) { r.Facts.Patient.Downed = domain.Known(true) }},
		{"patient unknown dead", func(r *SurgeryRequest) { r.Facts.Patient.Dead = domain.Unknown[bool]() }},
		{"patient unknown downed", func(r *SurgeryRequest) { r.Facts.Patient.Downed = domain.Unknown[bool]() }},
		{"preview refusal", func(r *SurgeryRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"unknown preview", func(r *SurgeryRequest) { r.Facts.NativeCanTry = domain.Unknown[bool]() }},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := surgeryRequest(t)
			c.change(&r)
			d := EvaluateSurgery(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}
