package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An emptied Retiring wing is dropped only when its ground lets Grow place
// a wing the real Grow could not; otherwise it stays as spare beds. Rooms
// outside the wing never move (#1249).
func TestEmptiedRetiringWingDropsOnlyForGain(t *testing.T) {
	for _, tc := range []struct {
		size int32
		drop bool
	}{{80, true}, {120, false}} {
		zones := Zone(zoningSurvey(tc.size, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} }))
		camp := PlanCore(zones, 3, BuildTierCamp)
		camp.Wings = retireWings(camp.Wings, BuildTierSpacer)
		old := camp.Wings[0]
		if _, dropped := dropEmptiedWings(camp, nil, nil, 6, 1, BuildTierSpacer); dropped {
			t.Fatal(tc.size, "a wing with owned beds dropped")
		}
		next, dropped := dropEmptiedWings(camp, nil, map[domain.Cell]bool{old.Corridor.From: true}, 6, 1, BuildTierSpacer)
		if dropped != tc.drop {
			t.Fatal(tc.size, "dropped", dropped)
		}
		kept := false
		for _, w := range next.Wings {
			kept = kept || (w.Purpose == WingBedroomsRetiring && w.Corridor == old.Corridor)
		}
		if kept == tc.drop || (tc.drop && activeWingRooms(next) != wingMaxRooms) {
			t.Fatal(tc.size, "retiring wing kept", kept, activeWingRooms(next))
		}
		for i, r := range camp.Rooms {
			if !next.Rooms[i].Same(r) {
				t.Fatal(tc.size, "a room moved", r, next.Rooms[i])
			}
		}
	}
}
