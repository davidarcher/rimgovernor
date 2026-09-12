package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCompletedConstructionIdentitySurvivesReopen(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	prepare(t, s, "a")
	progress, err := s.Dispatch(ctx, "p", "a", scope(), 10)
	if err != nil {
		t.Fatal(err)
	}
	identity := domain.ConstructionIdentity{Origin: "blueprint", Current: "building"}
	observed := domain.Observation{Action: "a", Attempt: progress.View().Attempt, Snapshot: scope(), Tick: 11, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch, Construction: &identity}
	if _, err = s.Observe(ctx, "p", observed, scope()); err != nil {
		t.Fatal(err)
	}
	identity.Current = "changed-after-call"
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	state, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	proof, known := state.Progress[0].View().Construction.Value()
	if !known || proof.Origin != "blueprint" || proof.Current != "building" || state.Progress[0].View().Stage != domain.Completed {
		t.Fatal(state.Progress[0].View())
	}
}
