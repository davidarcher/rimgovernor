package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestHoldPersistsAndSurvivesRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path := fixture(t)
	if _, err := s.Hold(ctx, "p", "a", []domain.HeldReason{domain.HeldUnsafeThreat, domain.HeldCriticalMedical}, 10); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	view := state.Progress[0].View()
	if view.Stage != domain.Pending || view.Action != "a" {
		t.Fatal("hold changed stage or targeted wrong action", view)
	}
	reasons, ok := view.FreshHeldReason()
	if !ok {
		t.Fatal("held reason not surfaced")
	}
	got := map[domain.HeldReason]bool{}
	for _, r := range reasons {
		got[r] = true
	}
	if !got[domain.HeldUnsafeThreat] || !got[domain.HeldCriticalMedical] || len(got) != 2 {
		t.Fatal("wrong held reasons", reasons)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	after, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if reasons, ok := after.Progress[0].View().FreshHeldReason(); !ok || len(reasons) != 2 {
		t.Fatal("held reason lost across restart", reasons, ok)
	}
}

// A hold recorded at one tick must never be served once the action has moved
// on to a later durable state: Prepare (a later, successful transition)
// clears it, so LoadPlan can never surface the earlier tick's reason.
func TestHoldClearsOnceActionAdvances(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	if _, err := s.Hold(ctx, "p", "a", []domain.HeldReason{domain.HeldStaleFacts}, 10); err != nil {
		t.Fatal(err)
	}
	prepare(t, s, "a")
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := state.Progress[0].View().FreshHeldReason(); ok {
		t.Fatal("hold reason survived a later successful Prepare")
	}
}

func TestHoldRejectsUnresolvedOrDispatchedAction(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Hold(ctx, "p", "a", []domain.HeldReason{domain.HeldUnsafeThreat}, 10); err == nil {
		t.Fatal("held a dispatched, unresolved action")
	}
}

func TestHoldRejectsStaleOrInvalidReasons(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Hold(ctx, "p", "a", []domain.HeldReason{domain.HeldStaleFacts}, 9); err == nil {
		t.Fatal("accepted a hold tick older than the prepared progress")
	}
	if _, err := s.Hold(ctx, "p", "a", nil, 10); err == nil {
		t.Fatal("accepted an empty hold reason set")
	}
	if _, err := s.Hold(ctx, "p", "a", []domain.HeldReason{"bogus"}, 10); err == nil {
		t.Fatal("accepted an invalid hold reason")
	}
	if _, err := s.Hold(ctx, "p", "missing", []domain.HeldReason{domain.HeldStaleFacts}, 10); err == nil {
		t.Fatal("held a nonexistent action")
	}
}
