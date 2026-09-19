package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func movementRequest(t *testing.T) MovementRequest {
	t.Helper()
	draft, _ := domain.NewOwnedDraft("pawn")
	da, _ := domain.NewOwnedDraftAction("draft", draft)
	m, _ := domain.NewMovement("pawn", domain.Cell{X: 3, Z: 4}, da.ID())
	a, _ := domain.NewMovementAction("move", m)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{da, a})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	d, _ := domain.NewProgress(plan, da.ID())
	d, err = d.Prepare(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	d, err = d.MarkDispatched(s, 10)
	if err != nil {
		t.Fatal(err)
	}
	claim := domain.DraftClaim{Action: da.ID(), Attempt: 1, Pawn: "pawn", Claim: "claim", Session: "session", Origin: s}
	d, err = d.RecordDraftReceipt(1, domain.ReceiptAccepted, domain.Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	d, err = d.ObserveDraft(domain.Observation{Action: da.ID(), Attempt: 1, Snapshot: s, Tick: 11, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, s, domain.Known(claim))
	if err != nil {
		t.Fatal(err)
	}
	p, _ := domain.NewProgress(plan, a.ID())
	e, err := NewEmergencySnapshot(s, 13, EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true), Colonists: []EmergencyPawn{{ID: "pawn", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false)}}})
	if err != nil {
		t.Fatal(err)
	}
	return MovementRequest{Action: a, Progress: p, DraftProgress: d, Current: s, MinimumTick: 11, Facts: MovementFacts{Snapshot: s, PawnTick: 12, PreviewTick: 13, Emergency: e, NativeCanTry: domain.Known(true), Pawn: MovementPawnFacts{Pawn: "pawn", SnapshotToken: "pawn-cas", Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(false), NeedsTend: domain.Known(false), FreeColonist: domain.Known(true), Drafted: domain.Known(true), Owner: domain.Known(MovementDraftOwner{Claim: "claim", Session: "session"})}}}
}

func TestMovementAdmission(t *testing.T) {
	r := movementRequest(t)
	original := r.Progress
	for _, prepared := range []bool{false, true} {
		if prepared {
			var err error
			r.Progress, err = r.Progress.Prepare(r.Current, 11)
			if err != nil {
				t.Fatal(err)
			}
		}
		if d := EvaluateMovement(r); !d.Admitted || len(d.Refused) != 0 {
			t.Fatal(d)
		}
	}
	if original.View().Stage != domain.Pending {
		t.Fatal("mutated progress")
	}
}

func TestMovementHolds(t *testing.T) {
	cases := []struct {
		name   string
		change func(*MovementRequest)
	}{
		{"zero action", func(r *MovementRequest) { r.Action = domain.Action{} }},
		{"zero progress", func(r *MovementRequest) { r.Progress = domain.Progress{} }},
		{"cancelled", func(r *MovementRequest) { r.Progress, _ = r.Progress.Cancel() }},
		{"minimum", func(r *MovementRequest) { r.MinimumTick = 14 }},
		{"negative minimum", func(r *MovementRequest) { r.MinimumTick = -1 }},
		{"reversed interval", func(r *MovementRequest) { r.Facts.PreviewTick = 11 }},
		{"pawn row outrun", func(r *MovementRequest) { r.Facts.PreviewTick = 12 + domain.PlanningTickTolerance + 1 }},
		{"prepared past preview", func(r *MovementRequest) { r.Progress, _ = r.Progress.Prepare(r.Current, 14) }},
		{"old emergency", func(r *MovementRequest) { r.Facts.Emergency.tick = 12 }},
		{"zero generation", func(r *MovementRequest) { r.Current.Native = 0 }},
		{"native", func(r *MovementRequest) { r.Current.Native++ }},
		{"colony", func(r *MovementRequest) { r.Current.Colony = "other" }},
		{"load", func(r *MovementRequest) { r.Current.Load = "other" }},
		{"map", func(r *MovementRequest) { r.Current.Map++ }},
		{"plan", func(r *MovementRequest) { r.Current.Plan = "other" }},
		{"revision", func(r *MovementRequest) { r.Current.Revision++ }},
		{"draft missing", func(r *MovementRequest) { r.DraftProgress = domain.Progress{} }},
		{"claim unknown", func(r *MovementRequest) { r.Facts.Pawn.Owner = domain.Unknown[MovementDraftOwner]() }},
		{"claim replaced", func(r *MovementRequest) {
			r.Facts.Pawn.Owner = domain.Known(MovementDraftOwner{Claim: "other", Session: "session"})
		}},
		{"foreign session", func(r *MovementRequest) {
			r.Facts.Pawn.Owner = domain.Known(MovementDraftOwner{Claim: "claim", Session: "other"})
		}},
		{"wrong pawn", func(r *MovementRequest) { r.Facts.Pawn.Pawn = "other" }},
		{"bleeding", func(r *MovementRequest) { r.Facts.Pawn.Bleeding = domain.Known(true) }},
		{"not free", func(r *MovementRequest) { r.Facts.Pawn.FreeColonist = domain.Known(false) }},
		{"undrafted", func(r *MovementRequest) { r.Facts.Pawn.Drafted = domain.Known(false) }},
		{"preview refusal", func(r *MovementRequest) { r.Facts.NativeCanTry = domain.Known(false) }},
		{"census incomplete", func(r *MovementRequest) { r.Facts.Emergency.facts.ThreatsComplete = domain.Known(false) }},
		{"critical medical", func(r *MovementRequest) {
			r.Facts.Emergency.facts.Colonists[0].Bleeding = domain.Known(true)
		}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := movementRequest(t)
			c.change(&r)
			d := EvaluateMovement(r)
			if d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason == "" {
				t.Fatal(d)
			}
		})
	}
}

// TestMovementAdmitsCachedPawnRowBehindPreparedTick is the #306 shape (#323):
// the executor's second inspection under a running window is served the
// pawn row the first read (tick 12) cached, while the first preview (tick
// 13) prepared the action and raised the minimum; the second preview (tick
// 15) anchors the admission, so the older row is fresh evidence.
func TestMovementAdmitsCachedPawnRowBehindPreparedTick(t *testing.T) {
	r := movementRequest(t)
	var err error
	if r.Progress, err = r.Progress.Prepare(r.Current, 13); err != nil {
		t.Fatal(err)
	}
	r.MinimumTick, r.Facts.PawnTick, r.Facts.PreviewTick = 13, 12, 15
	r.Facts.Emergency.tick = r.Facts.PreviewTick
	if d := EvaluateMovement(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
}

// A draft the plan still holds across an authority hold and a same-world
// resume (#318, #228) carries the generation it was claimed under; the move
// it enables admits under the resumed generation as long as the world and
// plan are the same.
func TestMovementAdmitsDraftFromEarlierGeneration(t *testing.T) {
	r := movementRequest(t)
	r.Current.Native++
	r.Facts.Snapshot = r.Current
	e, err := NewEmergencySnapshot(r.Current, 13, r.Facts.Emergency.facts)
	if err != nil {
		t.Fatal(err)
	}
	r.Facts.Emergency = e
	if d := EvaluateMovement(r); !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d)
	}
	r.Current.Load = "other"
	r.Facts.Snapshot = r.Current
	if d := EvaluateMovement(r); d.Admitted {
		t.Fatal("admitted a draft from another world")
	}
}
