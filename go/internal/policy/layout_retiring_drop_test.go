package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// An emptied Retiring wing stays unless a replan sited without it scores
// clearly better and still houses the colonists (#1249, #1958); a wing with
// owned beds never goes.
func TestEmptiedRetiringWingDropsOnlyForGain(t *testing.T) {
	for _, size := range []int32{80, 120} {
		s := zoningSurvey(size, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
		camp, ok := DeriveLayoutPlan(s, 3, BuildTierCamp, nil, 0).Value()
		if !ok {
			t.Fatal(size, "no plan")
		}
		old := camp.Wings[0]
		has := func(p LayoutPlan, w Wing) bool {
			for _, x := range p.Wings {
				if x.Corridor == w.Corridor {
					return true
				}
			}
			return false
		}
		built := RoomGrowth{Fixed: allPins(LayoutPlan{Rooms: old.Rooms})}
		next, _, _ := ReplanLayoutWithRooms(camp, s, built, 0, 6, 1, BuildTierSpacer, nil, nil)
		if !has(next, old) {
			t.Fatal(size, "a wing with owned beds dropped")
		}
		next, _, _ = ReplanLayoutWithRooms(camp, s, built, 0, 6, 1, BuildTierSpacer, nil, map[domain.Cell]bool{old.Corridor.From: true})
		if !has(next, old) && bedroomCount(next) < min(bedroomCount(camp), 6) {
			t.Fatal(size, "the retiring wing dropped without housing the colonists", bedroomCount(next))
		}
	}
}
