package buildingruntime

import (
	"testing"
)

// An applied open is over only when the session read no longer shows its
// walk or session; a refused one is over at once.
func TestTradeOpenSpentReadsLiveEngagement(t *testing.T) {
	applied := tradePhase{found: true, completed: true}
	refused := tradePhase{found: true}
	pending := tradePhase{found: true, open: true}
	for _, c := range []struct {
		phase   tradePhase
		engaged bool
		want    bool
	}{
		{applied, true, false}, {applied, false, true},
		{refused, true, true}, {refused, false, true},
		{pending, false, false}, {tradePhase{}, false, false},
	} {
		if got := tradeOpenSpent(c.phase, c.engaged); got != c.want {
			t.Fatalf("%+v engaged=%v: %v", c.phase, c.engaged, got)
		}
	}
}
