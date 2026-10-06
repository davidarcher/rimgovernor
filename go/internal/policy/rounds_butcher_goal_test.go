package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The butcher goal is owed until a butcher bench stands apart from the
// kitchen, whatever the food runway.
func TestButcherGoalIsOwedUntilABenchStandsApart(t *testing.T) {
	f := stableRounds()
	f.ButcherBenches = domain.Known([]ButcherBench{})
	r := needs(t, f, RoundsLatches{})
	if !hasNeed(r, MaintainButcherSpot) {
		t.Fatal("no butcher bench, goal not raised", r.Concerns)
	}
	f.ButcherBenches = domain.Known([]ButcherBench{{ID: "table", Definition: "TableButcher"}})
	if r = needs(t, f, RoundsLatches{}); hasNeed(r, MaintainButcherSpot) {
		t.Fatal("a standing table apart left the goal open")
	}
	f.ButcherBenches = domain.Unknown[[]ButcherBench]()
	if got := butcherSpotMet(f); got != domain.Unknown[bool]() {
		t.Fatal("an unread census became evidence", got)
	}
}
