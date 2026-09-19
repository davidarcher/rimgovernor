package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A hold plan drafts two defenders and moves each; one defender's move
// cancelled (its dispatch timed out) must not release the other's draft
// while that pawn's own move is still open (#318).
func TestWorkerPlanHoldsDraftPerDefender(t *testing.T) {
	t.Parallel()
	mk := func(id string) (domain.Action, domain.Action) {
		draft, err := domain.NewOwnedDraft(domain.PawnID("pawn-" + id))
		if err != nil {
			t.Fatal(err)
		}
		d, err := domain.NewOwnedDraftAction(domain.ActionID("draft-"+id), draft)
		if err != nil {
			t.Fatal(err)
		}
		move, err := domain.NewMovement(draft.Pawn(), domain.Cell{X: 1, Z: 1}, d.ID())
		if err != nil {
			t.Fatal(err)
		}
		m, err := domain.NewMovementAction(domain.ActionID("move-"+id), move)
		if err != nil {
			t.Fatal(err)
		}
		return d, m
	}
	dA, mA := mk("a")
	dB, mB := mk("b")
	plan, err := domain.NewPlan("hold", 1, []domain.Action{dA, mA, dB, mB})
	if err != nil {
		t.Fatal(err)
	}
	s := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: plan.ID(), Revision: 1, Native: 1}
	completeDraft := func(a domain.Action) domain.Progress {
		p, _ := domain.NewProgress(plan, a.ID())
		if p, err = p.Prepare(s, 10); err != nil {
			t.Fatal(err)
		}
		if p, err = p.MarkDispatched(s, 10); err != nil {
			t.Fatal(err)
		}
		draft, _ := a.OwnedDraft()
		claim := domain.DraftClaim{Action: a.ID(), Attempt: 1, Pawn: draft.Pawn(), Claim: "claim", Session: "session", Origin: s}
		if p, err = p.RecordDraftReceipt(1, domain.ReceiptAccepted, domain.Known(claim)); err != nil {
			t.Fatal(err)
		}
		if p, err = p.ObserveDraft(domain.Observation{Action: a.ID(), Attempt: 1, Snapshot: s, Tick: 11, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, s, domain.Known(claim)); err != nil {
			t.Fatal(err)
		}
		return p
	}
	pdA, pdB := completeDraft(dA), completeDraft(dB)
	pmA, _ := domain.NewProgress(plan, mA.ID())
	pmB, _ := domain.NewProgress(plan, mB.ID())
	state := store.PlanState{Spec: plan, Progress: []domain.Progress{pdA, pmA, pdB, pmB}}
	if !workerPlanHoldsDraft(state, pdA.View()) || !workerPlanHoldsDraft(state, pdB.View()) {
		t.Fatal("open moves do not hold their drafts")
	}
	if pmA, err = pmA.Cancel(); err != nil {
		t.Fatal(err)
	}
	state.Progress = []domain.Progress{pdA, pmA, pdB, pmB}
	if workerPlanHoldsDraft(state, pdA.View()) {
		t.Fatal("a cancelled move still holds its draft")
	}
	if !workerPlanHoldsDraft(state, pdB.View()) {
		t.Fatal("another defender's cancelled move released this draft")
	}
	// A draft still awaiting its observation when the hold lands (the
	// second defender in turrets6) is held too: the resume sweep must not
	// release a claim the executor is about to observe complete.
	inflight, _ := domain.NewProgress(plan, dB.ID())
	if inflight, err = inflight.Prepare(s, 10); err != nil {
		t.Fatal(err)
	}
	if inflight, err = inflight.MarkDispatched(s, 10); err != nil {
		t.Fatal(err)
	}
	draftB, _ := dB.OwnedDraft()
	claimB := domain.DraftClaim{Action: dB.ID(), Attempt: 1, Pawn: draftB.Pawn(), Claim: "claim-b", Session: "session", Origin: s}
	if inflight, err = inflight.RecordDraftReceipt(1, domain.ReceiptAccepted, domain.Known(claimB)); err != nil {
		t.Fatal(err)
	}
	if inflight.View().Stage != domain.AwaitingObservation {
		t.Fatal(inflight.View().Stage)
	}
	state.Progress = []domain.Progress{pdA, pmA, inflight, pmB}
	if !workerPlanHoldsDraft(state, inflight.View()) {
		t.Fatal("an in-flight draft is not held")
	}
	// A recovered goal cancels the plan's unissued orders (here defender
	// A's move) while defender B's move is still executing natively
	// (turrets7): B stays drafted until that move closes.
	if pmB, err = pmB.Prepare(s, 12); err != nil {
		t.Fatal(err)
	}
	if pmB, err = pmB.MarkDispatched(s, 12); err != nil {
		t.Fatal(err)
	}
	if pmB, err = pmB.RecordReceipt(1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	state.Progress = []domain.Progress{pdA, pmA, pdB, pmB}
	if !workerPlanHoldsDraft(state, pdB.View()) {
		t.Fatal("a cancelled sibling released a draft whose move is executing")
	}
	if workerPlanHoldsDraft(state, pdA.View()) {
		t.Fatal("a cancelled move still holds its draft")
	}
}
