package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A repair deficit holds the shrine breach only while repairs are served:
// in clearance/shrine-breach combat damage left a repair need no family
// could serve and the breach waited on it forever.
func TestShrineDefersToRepairsOnlyWhenRepairsServed(t *testing.T) {
	f := stableRoutine()
	f.Upkeep.Shrines = domain.Known([]AncientShrine{{ID: "shrine", InHome: true, Sealed: true}})
	f.Upkeep.Structures = domain.Known([]UpkeepStructure{{ID: "door", Home: true, HitPoints: 50, MaxHitPoints: 100}})
	for _, served := range []bool{true, false} {
		methods := []GoalID{ClearAncientShrine}
		if served {
			methods = append(methods, MaintainEssentialRepairs)
		}
		f.AvailableMethods = domain.Known(methods)
		found := false
		for _, g := range needs(t, f, RoutineLatches{}).Goals {
			if g.ID == ClearAncientShrine {
				found = true
				if g.MethodUnavailable != served {
					t.Fatal(served, g)
				}
			}
		}
		if !found {
			t.Fatal("no shrine goal", served)
		}
	}
}
