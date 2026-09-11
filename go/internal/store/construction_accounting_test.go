package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestConstructionInspectionProofReplaysAndUnknownClearsIt(t *testing.T) {
	ctx := context.Background()
	s, path := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	observation := domain.Observation{Action: "a", Attempt: 1, Snapshot: scope(), Tick: 11, Effect: domain.EffectPending, Causality: domain.AfterDispatch, ConstructionObserved: true}
	for _, tick := range []domain.Tick{11, 12} {
		observation.Tick = tick
		p, err := s.Observe(ctx, "p", observation, scope())
		if err != nil {
			t.Fatal(err)
		}
		if got, k := p.View().ConstructionObserved.Value(); !k || got != 11 {
			t.Fatal(p.View())
		}
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if got, k := loaded.Progress[0].View().ConstructionObserved.Value(); !k || got != 11 {
		t.Fatal(loaded)
	}
	observation.Tick = 13
	observation.Effect = domain.EffectUnknown
	observation.ConstructionObserved = false
	p, err := s.Observe(ctx, "p", observation, scope())
	if err != nil {
		t.Fatal(err)
	}
	if _, k := p.View().ConstructionObserved.Value(); k {
		t.Fatal(p.View())
	}
}
