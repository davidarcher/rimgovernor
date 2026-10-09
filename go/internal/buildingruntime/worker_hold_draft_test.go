package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A hold plan drafts two defenders and moves each; one defender's move
// cancelled (its dispatch timed out) must not release the other's draft
// while that pawn's own move is still open.
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
		if p, err = p.RecordReceipt(1, domain.ReceiptAccepted); err != nil {
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
	// A move's applied receipt is terminal: once a recovered goal cancelled
	// defender A's move the plan is settled, and B's draft is released
	// even though B's order was given.
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
	if pmB.View().Stage != domain.Completed {
		t.Fatal(pmB.View().Stage)
	}
	if workerPlanHoldsDraft(state, pdB.View()) {
		t.Fatal("a settled plan still holds a draft")
	}
	if workerPlanHoldsDraft(state, pdA.View()) {
		t.Fatal("a cancelled move still holds its draft")
	}
}
