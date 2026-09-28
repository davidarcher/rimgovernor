package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
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

// An open session's phase is read from the live sheet (#999): after a
// save load the journal holds no line staging, so staged sheet lines
// resume at accept, an empty sheet stages lines, and a failed staging
// this epoch cancels.
func TestTradeSessionPhaseReadsLiveSheet(t *testing.T) {
	for _, c := range []struct {
		lines  tradePhase
		staged int
		want   domain.TradeOperationKind
	}{
		{tradePhase{}, 0, domain.TradeSetLines},
		{tradePhase{}, 2, domain.TradeAccept},
		{tradePhase{found: true, completed: true}, 1, domain.TradeAccept},
		{tradePhase{found: true}, 1, domain.TradeEnd},
	} {
		if got := tradeSessionPhase(c.lines, c.staged); got != c.want {
			t.Fatalf("%+v staged=%d: %v", c.lines, c.staged, got)
		}
	}
}
