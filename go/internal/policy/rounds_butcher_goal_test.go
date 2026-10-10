package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// EnsureFoodSupply owes the butcher facility until a table stands apart from
// the kitchen with no spot beside it, whatever the food runway; the spot alone
// is enough only while the wood for the table is short.
func TestButcherFacilityKeepsFoodGoalOpenUntilTableStandsAlone(t *testing.T) {
	spot := ButcherBench{ID: "spot", Definition: ButcherSpotDefinition}
	table := ButcherBench{ID: "table", Definition: "TableButcher"}
	for _, c := range []struct {
		name    string
		benches []ButcherBench
		wood    int64
		owed    bool
	}{
		{"no bench", nil, 0, true},
		{"spot, wood for the table", []ButcherBench{spot}, ButcherTableWood, true},
		{"spot, short of wood", []ButcherBench{spot}, ButcherTableWood - 1, false},
		{"table beside its spot", []ButcherBench{spot, table}, ButcherTableWood, true},
		{"table alone", []ButcherBench{table}, 0, false},
	} {
		f := stableRounds()
		f.ButcherBenches = domain.Known(c.benches)
		f.Wood = domain.Known(c.wood)
		f.Upkeep.Rooms = domain.Known(RoomObservation{})
		if got := hasNeed(needs(t, f, RoundsLatches{}), EnsureFoodSupply); got != c.owed {
			t.Errorf("%s: food goal raised = %v, want %v", c.name, got, c.owed)
		}
	}
	f := stableRounds()
	f.ButcherBenches = domain.Unknown[[]ButcherBench]()
	if got := butcherFacilityMet(f); got != domain.Unknown[bool]() {
		t.Fatal("an unread census became evidence", got)
	}
	if hasNeed(needs(t, f, RoundsLatches{}), EnsureFoodSupply) {
		t.Fatal("an unread census raised the food goal")
	}
}
