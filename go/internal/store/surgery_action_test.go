package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A surgery action persists its patient, recipe, part and
// violation acknowledgment and surgeon, including a whole-body recipe's absent part.
func TestSurgeryActionRoundTrips(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	var actions []domain.Action
	for i, want := range []struct {
		part int
		ack  bool
	}{{5, false}, {domain.NoSurgeryPart, true}} {
		value, err := domain.NewSurgery("Human7", "InstallPegLeg", want.part, want.ack)
		if err != nil {
			t.Fatal(err)
		}
		if i == 0 {
			if value, err = value.WithSurgeon("Human8"); err != nil { // #1253
				t.Fatal(err)
			}
		}
		a, err := domain.NewSurgeryAction(domain.ActionID([]string{"leg", "harvest"}[i]), value)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	p, err := domain.NewPlan("surgery-plan", 1, actions)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "restore", p); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadPlan(ctx, "surgery-plan")
	if err != nil {
		t.Fatal(err)
	}
	got := loaded.Spec.Actions()
	if len(got) != 2 {
		t.Fatal(got)
	}
	for i, a := range got {
		v, ok := a.Surgery()
		want, _ := actions[i].Surgery()
		if !ok || v != want {
			t.Fatal(i, v, want)
		}
	}
}
