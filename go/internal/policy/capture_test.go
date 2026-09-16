package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func captureRequest(t *testing.T) CaptureRequest {
	t.Helper()
	capture, _ := domain.NewCapture("capturer", "patient")
	a, _ := domain.NewCaptureAction("capture", capture)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	e, err := NewEmergencySnapshot(s, 13, EmergencyFacts{
		ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
		Colonists: []EmergencyPawn{
			{ID: "capturer", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	capturer := RescuerFacts{Pawn: "capturer", SnapshotToken: "capturer-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	patient := CapturePatientFacts{Pawn: "patient", SnapshotToken: "patient-cas", Dead: domain.Known(false), Downed: domain.Known(true), Prisoner: domain.Known(false), ExistingJobDef: domain.Known("")}
	return CaptureRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: CaptureFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Capturer: capturer, Patient: patient}}
}

func TestCaptureAdmission(t *testing.T) {
	r := captureRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateCapture(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestCaptureDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*CaptureRequest)
	}{
		{"zero action", func(r *CaptureRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *CaptureRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *CaptureRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *CaptureRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *CaptureRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *CaptureRequest) { r.Facts.PreviewTick = 11 }},
		{"old emergency", func(r *CaptureRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *CaptureRequest) { r.Current.Native = 0 }},
		{"native", func(r *CaptureRequest) { r.Current.Native++ }},
		{"colony", func(r *CaptureRequest) { r.Current.Colony = "other" }},
		{"load", func(r *CaptureRequest) { r.Current.Load = "other" }},
		{"map", func(r *CaptureRequest) { r.Current.Map++ }},
		{"plan", func(r *CaptureRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *CaptureRequest) { r.Current.Revision++ }},
		{"capturer CAS", func(r *CaptureRequest) { r.Facts.Capturer.SnapshotToken = "" }},
		{"patient CAS", func(r *CaptureRequest) { r.Facts.Patient.SnapshotToken = "" }},
		{"wrong capturer", func(r *CaptureRequest) { r.Facts.Capturer.Pawn = "other" }},
		{"wrong patient", func(r *CaptureRequest) { r.Facts.Patient.Pawn = "other" }},
		{"capturer dead", func(r *CaptureRequest) { r.Facts.Capturer.Dead = domain.Known(true) }},
		{"capturer downed", func(r *CaptureRequest) { r.Facts.Capturer.Downed = domain.Known(true) }},
		{"capturer drafted", func(r *CaptureRequest) { r.Facts.Capturer.Drafted = domain.Known(true) }},
		{"capturer mental state", func(r *CaptureRequest) { r.Facts.Capturer.MentalState = domain.Known(true) }},
		{"capturer already capturing", func(r *CaptureRequest) { r.Facts.Capturer.ExistingJobDef = domain.Known("Capture") }},
		{"patient dead", func(r *CaptureRequest) { r.Facts.Patient.Dead = domain.Known(true) }},
		{"patient not downed", func(r *CaptureRequest) { r.Facts.Patient.Downed = domain.Known(false) }},
		{"patient already a prisoner", func(r *CaptureRequest) { r.Facts.Patient.Prisoner = domain.Known(true) }},
		{"patient unknown prisoner", func(r *CaptureRequest) { r.Facts.Patient.Prisoner = domain.Unknown[bool]() }},
		{"patient already being captured", func(r *CaptureRequest) { r.Facts.Patient.ExistingJobDef = domain.Known("Capture") }},
		{"preview refusal", func(r *CaptureRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *CaptureRequest) { r.Facts.Emergency.facts.ColonistsComplete = domain.Known(false) }},
		{"unsafe threat", func(r *CaptureRequest) {
			r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := captureRequest(t)
			c.change(&r)
			d := EvaluateCapture(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

func capturerCandidate(id domain.PawnID) RescuerFacts {
	return RescuerFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
}
func capturePatientCandidate(id domain.PawnID, downed, prisoner bool) CapturePatientFacts {
	return CapturePatientFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(downed), Prisoner: domain.Known(prisoner), ExistingJobDef: domain.Known("")}
}

func TestSelectCaptureExcludesIneligibleCandidates(t *testing.T) {
	busy := capturerCandidate("busy")
	busy.ExistingJobDef = domain.Known("Capture")
	drafted := capturerCandidate("drafted")
	drafted.Drafted = domain.Known(true)
	fine := capturerCandidate("fine")
	standing := capturePatientCandidate("standing", false, false)
	alreadyPrisoner := capturePatientCandidate("prisoner", true, true)
	downed := capturePatientCandidate("downed", true, false)

	capturer, patient, ok := SelectCapture([]RescuerFacts{busy, drafted, fine}, []CapturePatientFacts{standing, alreadyPrisoner, downed})
	if !ok || capturer != "fine" || patient != "downed" {
		t.Fatal(capturer, patient, ok)
	}
	if _, _, ok := SelectCapture([]RescuerFacts{busy, drafted}, []CapturePatientFacts{downed}); ok {
		t.Fatal("selected an ineligible capturer")
	}
	if _, _, ok := SelectCapture([]RescuerFacts{fine}, []CapturePatientFacts{standing, alreadyPrisoner}); ok {
		t.Fatal("selected an ineligible patient")
	}
}
