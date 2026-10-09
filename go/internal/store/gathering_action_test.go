package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A gathering persists its def and organizer and is a supported routine kind.
func TestGatheringActionRoundTripsAndIsRoutineSupported(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	want, err := domain.NewGathering("Party", "Human12")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewGatheringAction("gather1", want)
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewPlan("gather-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = roundsActionsSupported(p); err != nil {
		t.Fatal("routine-execution allowlist omits gathering:", err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "gather-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 1 {
		t.Fatal(got)
	}
	if v, ok := got[0].Gathering(); !ok || v != want {
		t.Fatal(v, want)
	}
}
