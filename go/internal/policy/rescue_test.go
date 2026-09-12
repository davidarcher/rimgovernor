package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rescueRequest(t *testing.T) RescueRequest {
	t.Helper()
	rescue, _ := domain.NewRescue("rescuer", "patient")
	a, _ := domain.NewRescueAction("rescue", rescue)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: plan.ID(), Revision: 1, Native: 1}
	p, _ := domain.NewProgress(plan, a.ID())
	e, err := NewEmergencySnapshot(s, 13, EmergencyFacts{
		ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true),
		Colonists: []EmergencyPawn{
			{ID: "rescuer", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)},
			{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	rescuer := RescuerFacts{Pawn: "rescuer", SnapshotToken: "rescuer-cas", Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
	patient := RescuePatientFacts{Pawn: "patient", SnapshotToken: "patient-cas", Dead: domain.Known(false), Downed: domain.Known(true), InBed: domain.Known(false), BedID: domain.Unknown[string](), ExistingJobDef: domain.Known("")}
	return RescueRequest{Action: a, Progress: p, Current: s, MinimumTick: 11, Facts: RescueFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Rescuer: rescuer, Patient: patient}}
}

func TestRescueAdmission(t *testing.T) {
	r := rescueRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateRescue(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestRescueDefenseHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*RescueRequest)
	}{
		{"zero action", func(r *RescueRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *RescueRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *RescueRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *RescueRequest) { r.MinimumTick = 13 }},
		{"negative minimum", func(r *RescueRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *RescueRequest) { r.Facts.PreviewTick = 11 }},
		{"old emergency", func(r *RescueRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *RescueRequest) { r.Current.Native = 0 }},
		{"native", func(r *RescueRequest) { r.Current.Native++ }},
		{"direction", func(r *RescueRequest) { r.Current.Direction++ }},
		{"colony", func(r *RescueRequest) { r.Current.Colony = "other" }},
		{"load", func(r *RescueRequest) { r.Current.Load = "other" }},
		{"map", func(r *RescueRequest) { r.Current.Map++ }},
		{"plan", func(r *RescueRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *RescueRequest) { r.Current.Revision++ }},
		{"rescuer CAS", func(r *RescueRequest) { r.Facts.Rescuer.SnapshotToken = "" }},
		{"patient CAS", func(r *RescueRequest) { r.Facts.Patient.SnapshotToken = "" }},
		{"wrong rescuer", func(r *RescueRequest) { r.Facts.Rescuer.Pawn = "other" }},
		{"wrong patient", func(r *RescueRequest) { r.Facts.Patient.Pawn = "other" }},
		{"rescuer dead", func(r *RescueRequest) { r.Facts.Rescuer.Dead = domain.Known(true) }},
		{"rescuer downed", func(r *RescueRequest) { r.Facts.Rescuer.Downed = domain.Known(true) }},
		{"rescuer drafted", func(r *RescueRequest) { r.Facts.Rescuer.Drafted = domain.Known(true) }},
		{"rescuer mental state", func(r *RescueRequest) { r.Facts.Rescuer.MentalState = domain.Known(true) }},
		{"rescuer player forced", func(r *RescueRequest) { r.Facts.Rescuer.PlayerForced = domain.Known(true) }},
		{"rescuer queued", func(r *RescueRequest) { r.Facts.Rescuer.QueuedJobs = domain.Known(uint32(1)) }},
		{"rescuer unknown queue", func(r *RescueRequest) { r.Facts.Rescuer.QueuedJobs = domain.Unknown[uint32]() }},
		{"rescuer already rescuing", func(r *RescueRequest) { r.Facts.Rescuer.ExistingJobDef = domain.Known("Rescue") }},
		{"patient dead", func(r *RescueRequest) { r.Facts.Patient.Dead = domain.Known(true) }},
		{"patient not downed", func(r *RescueRequest) { r.Facts.Patient.Downed = domain.Known(false) }},
		{"patient already in bed", func(r *RescueRequest) { r.Facts.Patient.InBed = domain.Known(true) }},
		{"patient unknown in bed", func(r *RescueRequest) { r.Facts.Patient.InBed = domain.Unknown[bool]() }},
		{"patient already being rescued", func(r *RescueRequest) { r.Facts.Patient.ExistingJobDef = domain.Known("Rescue") }},
		{"preview refusal", func(r *RescueRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *RescueRequest) { r.Facts.Emergency.facts.ColonistsComplete = domain.Known(false) }},
		{"unsafe threat", func(r *RescueRequest) {
			r.Facts.Emergency.facts.Threats = append(r.Facts.Emergency.facts.Threats, EmergencyThreat{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)})
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := rescueRequest(t)
			c.change(&r)
			d := EvaluateRescue(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

// The patient's own downed/untended state must never refuse its own rescue
// action; that is precisely the emergency this action resolves.
func TestRescueIgnoresItsOwnCriticalMedicalHold(t *testing.T) {
	r := rescueRequest(t)
	if d := EvaluateRescue(r); !d.Admitted {
		t.Fatal("rescue refused by the emergency it exists to clear", d)
	}
}

func rescuerCandidate(id domain.PawnID) RescuerFacts {
	return RescuerFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(false), Drafted: domain.Known(false), MentalState: domain.Known(false), PlayerForced: domain.Known(false), QueuedJobs: domain.Known(uint32(0)), ExistingJobDef: domain.Known("")}
}
func rescuePatientCandidate(id domain.PawnID, downed, inBed bool) RescuePatientFacts {
	return RescuePatientFacts{Pawn: id, Dead: domain.Known(false), Downed: domain.Known(downed), InBed: domain.Known(inBed), ExistingJobDef: domain.Known("")}
}

func TestSelectRescueExcludesIneligibleCandidates(t *testing.T) {
	busy := rescuerCandidate("busy")
	busy.ExistingJobDef = domain.Known("Rescue")
	drafted := rescuerCandidate("drafted")
	drafted.Drafted = domain.Known(true)
	fine := rescuerCandidate("fine")
	standing := rescuePatientCandidate("standing", false, false)
	alreadyBedded := rescuePatientCandidate("bedded", true, true)
	downed := rescuePatientCandidate("downed", true, false)

	rescuer, patient, ok := SelectRescue([]RescuerFacts{busy, drafted, fine}, []RescuePatientFacts{standing, alreadyBedded, downed})
	if !ok || rescuer != "fine" || patient != "downed" {
		t.Fatal(rescuer, patient, ok)
	}
	if _, _, ok := SelectRescue([]RescuerFacts{busy, drafted}, []RescuePatientFacts{downed}); ok {
		t.Fatal("selected an ineligible rescuer")
	}
	if _, _, ok := SelectRescue([]RescuerFacts{fine}, []RescuePatientFacts{standing, alreadyBedded}); ok {
		t.Fatal("selected an ineligible patient")
	}
}
