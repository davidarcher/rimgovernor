package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestObservedConstructionUsesFreshNetStockWithoutReleasingGeometry(t *testing.T) {
	for _, cancelled := range []bool{false, true} {
		old := issue(t, candidate(t, "old", 1, 100))
		var err error
		old.Progress, err = old.Progress.Observe(domain.Observation{Action: "old", Attempt: 1, Snapshot: current(), Tick: 15, Effect: domain.EffectPending, Causality: domain.AfterDispatch, ConstructionObserved: true}, current())
		if err != nil {
			t.Fatal(err)
		}
		if cancelled {
			old.Progress, err = old.Progress.Cancel()
			if err != nil {
				t.Fatal(err)
			}
		}
		r := request(candidate(t, "new", 2, 100))
		r.Held = []Reservation{hold(old)}
		reason(t, r, InsufficientStock)
		r.Stock.NativeConstruction = true
		d := decide(t, r)
		if len(d.Admitted) != 1 || len(d.Held) != 1 || d.Held[0].Costs[0].Count != 100 || !d.Held[0].Progress.View().Unresolved {
			t.Fatal(d)
		}
		r.Candidates = []Candidate{candidate(t, "new", 1, 1)}
		reason(t, r, GeometryBlocked)
		r.Candidates = []Candidate{candidate(t, "new", 2, 100)}
		r.Stock.Tick = 15
		r.CurrentTick = 15
		r.Candidates[0].Preview.Tick = 15
		reason(t, r, InsufficientStock)
		r.Stock.Tick = 20
		r.CurrentTick = 20
		r.Candidates[0].Preview.Tick = 20
		old.Progress, err = old.Progress.Observe(domain.Observation{Action: "old", Attempt: 1, Snapshot: current(), Tick: 16, Effect: domain.EffectUnknown}, current())
		if err != nil {
			t.Fatal(err)
		}
		r.Held = []Reservation{hold(old)}
		reason(t, r, InsufficientStock)
	}
}
