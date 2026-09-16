package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func draftPolicyRequest(t *testing.T) DraftRequest {
	t.Helper()
	intent, err := domain.NewOwnedDraft("pawn")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewOwnedDraftAction("draft", intent)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(plan, action.ID())
	if err != nil {
		t.Fatal(err)
	}
	current := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Direction: 1, Plan: "plan", Revision: 1, Native: 1}
	emergency, err := NewEmergencySnapshot(current, 11, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}})
	if err != nil {
		t.Fatal(err)
	}
	return DraftRequest{Action: action, Progress: progress, Current: current, Tick: 10, Emergency: emergency, Pawn: DraftPawnFacts{Pawn: "pawn", Drafted: domain.Known(false), Unowned: domain.Known(true), NativeCanTry: domain.Known(true)}}
}
func replaceDraftEmergency(t *testing.T, r *DraftRequest, change func(*EmergencyFacts)) {
	t.Helper()
	facts := r.Emergency.facts
	facts.Colonists = append([]EmergencyPawn(nil), facts.Colonists...)
	facts.Threats = append([]EmergencyThreat(nil), facts.Threats...)
	change(&facts)
	var err error
	r.Emergency, err = NewEmergencySnapshot(r.Current, 11, facts)
	if err != nil {
		t.Fatal(err)
	}
}
func TestOwnedDraftAdmitsOnlyHealthySelectedEmergencyPawn(t *testing.T) {
	r := draftPolicyRequest(t)
	replaceDraftEmergency(t, &r, func(f *EmergencyFacts) {
		f.Threats = []EmergencyThreat{{ID: "raider", Kind: Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
		f.Colonists = append(f.Colonists, EmergencyPawn{ID: "patient", Dead: domain.Known(false), Downed: domain.Known(true), Bleeding: domain.Known(true), NeedsTend: domain.Known(true)})
	})
	if EvaluateEmergency(r.Emergency, r.Current, r.Tick).Clear {
		t.Fatal("ordinary work unexpectedly clear")
	}
	if decision := EvaluateOwnedDraft(r); !decision.Admitted || len(decision.Refused) != 0 {
		t.Fatal(decision)
	}
	original := r.Progress
	next, err := r.Progress.Prepare(r.Current, 10)
	if err != nil {
		t.Fatal(err)
	}
	r.Progress = next
	if !EvaluateOwnedDraft(r).Admitted {
		t.Fatal("prepared revalidation refused")
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated input")
	}
}
func TestOwnedDraftRefusesUnknownUnsafeOrStaleFacts(t *testing.T) {
	tests := []struct {
		name   string
		reason Reason
		change func(*DraftRequest)
	}{
		{"draft unknown", UnknownFacts, func(r *DraftRequest) { r.Pawn.Drafted = domain.Unknown[bool]() }},
		{"ownership unknown", UnknownFacts, func(r *DraftRequest) { r.Pawn.Unowned = domain.Unknown[bool]() }},
		{"eligibility unknown", UnknownFacts, func(r *DraftRequest) { r.Pawn.NativeCanTry = domain.Unknown[bool]() }},
		{"player drafted", DraftOwnership, func(r *DraftRequest) { r.Pawn.Drafted = domain.Known(true) }},
		{"owned elsewhere", DraftOwnership, func(r *DraftRequest) { r.Pawn.Unowned = domain.Known(false) }},
		{"native refusal", NativeIneligible, func(r *DraftRequest) { r.Pawn.NativeCanTry = domain.Known(false) }},
		{"wrong pawn evidence", UnknownFacts, func(r *DraftRequest) { r.Pawn.Pawn = "other" }},
		{"missing selected pawn", UnknownFacts, func(r *DraftRequest) { replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.Colonists = nil }) }},
		{"unknown selected health", UnknownFacts, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.Colonists[0].Bleeding = domain.Unknown[bool]() })
		}},
		{"dead selected pawn", CriticalMedical, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.Colonists[0].Dead = domain.Known(true) })
		}},
		{"selected patient", CriticalMedical, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.Colonists[0].NeedsTend = domain.Known(true) })
		}},
		{"unknown other pawn", UnknownFacts, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.Colonists = append(f.Colonists, EmergencyPawn{ID: "other"}) })
		}},
		{"unknown threat", UnknownFacts, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.Threats = []EmergencyThreat{{ID: "raider", Kind: Hostile}} })
		}},
		{"incomplete census", UnknownFacts, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.ThreatsComplete = domain.Known(false) })
		}},
		{"missing census", UnknownFacts, func(r *DraftRequest) {
			replaceDraftEmergency(t, r, func(f *EmergencyFacts) { f.ColonistsComplete = domain.Unknown[bool]() })
		}},
		{"missing emergency", StaleFacts, func(r *DraftRequest) { r.Emergency = EmergencySnapshot{} }},
		{"future minimum tick", StaleFacts, func(r *DraftRequest) { r.Tick = 12 }},
		{"changed native generation", StaleFacts, func(r *DraftRequest) { r.Current.Native++ }},
		{"zero generation", StaleFacts, func(r *DraftRequest) { r.Current.Native = 0 }},
		{"zero direction", StaleFacts, func(r *DraftRequest) { r.Current.Direction = 0 }},
		{"changed world", StaleFacts, func(r *DraftRequest) { r.Current.Load = "other" }},
		{"wrong plan", NotReady, func(r *DraftRequest) { r.Current.Plan = "other" }},
		{"wrong revision", NotReady, func(r *DraftRequest) { r.Current.Revision++ }},
		{"same ID different intent", NotReady, func(r *DraftRequest) {
			d, _ := domain.NewOwnedDraft("other")
			r.Action, _ = domain.NewOwnedDraftAction(r.Action.ID(), d)
		}},
		{"building bypass", NotReady, func(r *DraftRequest) {
			b, _ := domain.NewBuilding("Wall", domain.Cell{}, domain.North, "")
			r.Action, _ = domain.NewBuildingAction(r.Action.ID(), b)
		}},
		{"zero progress", NotReady, func(r *DraftRequest) { r.Progress = domain.Progress{} }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			r := draftPolicyRequest(t)
			test.change(&r)
			decision := EvaluateOwnedDraft(r)
			if decision.Admitted || len(decision.Refused) != 1 || decision.Refused[0].Reason != test.reason {
				t.Fatal(decision)
			}
		})
	}
}
func TestOwnedDraftProgressAndOutstandingCleanup(t *testing.T) {
	for _, stage := range []string{"prepared stale", "dispatched", "cancelled", "completed", "absent with claim", "proven never acquired"} {
		t.Run(stage, func(t *testing.T) {
			r := draftPolicyRequest(t)
			p, err := r.Progress.Prepare(r.Current, 10)
			if err != nil {
				t.Fatal(err)
			}
			if stage == "prepared stale" {
				r.Current.Direction++
				r.Progress = p
				if EvaluateOwnedDraft(r).Admitted {
					t.Fatal("stale prepared authority")
				}
				return
			}
			p, err = p.MarkDispatched(r.Current, 10)
			if err != nil {
				t.Fatal(err)
			}
			claim := domain.DraftClaim{Action: r.Action.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: r.Current}
			switch stage {
			case "cancelled":
				p, err = p.Cancel()
			case "completed", "absent with claim":
				p, err = p.RecordDraftReceipt(1, domain.ReceiptAccepted, domain.Known(claim))
				if err == nil {
					effect := domain.EffectCompleted
					if stage == "absent with claim" {
						effect = domain.EffectAbsent
					}
					p, err = p.ObserveDraft(domain.Observation{Action: r.Action.ID(), Attempt: 1, Snapshot: r.Current, Tick: 11, Effect: effect, Causality: domain.AfterDispatch}, r.Current, domain.Known(claim))
				}
			case "proven never acquired":
				p, err = p.RecordDraftReceipt(1, domain.ReceiptRefused, domain.Unknown[domain.DraftClaim]())
			}
			if err != nil {
				t.Fatal(err)
			}
			r.Progress = p
			r.Tick = 11
			decision := EvaluateOwnedDraft(r)
			if decision.Admitted != (stage == "proven never acquired") {
				t.Fatal(stage, decision)
			}
			if stage == "proven never acquired" {
				r.Current.Load = "replacement"
				replaceDraftEmergency(t, &r, func(*EmergencyFacts) {})
				if EvaluateOwnedDraft(r).Admitted {
					t.Fatal("previous attempt retargeted replacement world")
				}
			}
		})
	}
}
