package executor

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCausalSameTickAndUnsuccessfulBoundaryEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			f := newFixture(t)
			if _, err := f.run(); err != nil {
				t.Fatal(err)
			}
			complete, causal := false, false
			f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
				e := f.env.evidence(p, g, effect, complete)
				e.Observation.Tick = p.Tick
				if causal {
					e.Observation.Causality = domain.AfterDispatch
				}
				if effect == domain.EffectUnsuccessful {
					e.Observation.UnsuccessfulReason = domain.NativeFailure
				}
				return e
			}
			if _, err := f.run(); err == nil {
				t.Fatal("incomplete terminal inspection accepted")
			}
			complete = true
			if _, err := f.run(); err == nil {
				t.Fatal("same-tick inspection without causality accepted")
			}
			causal = true
			got, err := f.run()
			if err != nil {
				t.Fatal(err)
			}
			want := domain.Completed
			if effect == domain.EffectUnsuccessful {
				want = domain.Unsuccessful
			}
			if got.Progress.View().Stage != want || got.Progress.View().Unresolved {
				t.Fatal(got)
			}
			_, before, _ := f.env.counts()
			if _, err := f.run(); err != nil {
				t.Fatal(err)
			}
			_, after, _ := f.env.counts()
			if before != after {
				t.Fatal("terminal native write repeated")
			}
		})
	}
}
