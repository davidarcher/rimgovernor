package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestUnsuccessfulWorkYieldsOnlyToFreshNativeFacts(t *testing.T) {
	for _, cancelled := range []bool{false, true} {
		t.Run(fmt.Sprint(cancelled), func(t *testing.T) {
			old := issue(t, candidate(t, "old", 1, 100))
			var err error
			if cancelled {
				old.Progress, err = old.Progress.Cancel()
				if err != nil {
					t.Fatal(err)
				}
			}
			old.Progress, err = old.Progress.Observe(domain.Observation{Action: "old", Attempt: 1, Snapshot: current(), Tick: 15, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure}, current())
			if err != nil {
				t.Fatal(err)
			}
			r := request(candidate(t, "new", 1, 40))
			r.Held = []Reservation{hold(old)}
			// Native stock reflects actual remaining resources, not the original budget.
			r.Stock.Values[0].Available = domain.Known(int64(40))
			d := decide(t, r)
			if len(d.Admitted) != 1 || len(d.Held) != 1 || d.Held[0].Progress != old.Progress {
				t.Fatal(d)
			}
			r.Stock.Values[0].Available = domain.Known(int64(39))
			reason(t, r, InsufficientStock)
			r.Stock.Values[0].Available = domain.Unknown[int64]()
			reason(t, r, UnknownFacts)
			r.Stock.Values[0].Available = domain.Known(int64(40))
			r.Candidates[0].Preview.SafeToPlace = domain.Known(false)
			reason(t, r, UnsafePlacement)
			r.Candidates[0].Preview.SafeToPlace = domain.Known(true)
			r.Stock.Tick = 14
			reason(t, r, StaleFacts)
			r.CurrentTick, r.Candidates[0].Preview.Tick = 14, 14
			if len(decide(t, r).Admitted) != 0 {
				t.Fatal("pre-outcome facts released reservation")
			}
		})
	}
}
