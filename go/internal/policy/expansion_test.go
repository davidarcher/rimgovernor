package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainHousing's expansion phase keeps one spare indoor place; a colony
// short of its first places is on the shelter phase instead, at foothold
// priority, and never reaches expansion until those stand.
func TestExpansionHeadroomUnknownAndHousingGate(t *testing.T) {
	for _, tc := range []struct {
		name                 string
		population, capacity domain.Fact[int64]
		need                 domain.NeedState
		phase                Phase
		priority             int
		deficit              float64
		known                bool
	}{
		{"spare", domain.Known(int64(3)), domain.Known(int64(4)), domain.NeedRecovered, "", 2, 0, true},
		{"full", domain.Known(int64(3)), domain.Known(int64(3)), domain.NeedDeficit, HousingExpansion, 4, .25, true},
		{"short", domain.Known(int64(3)), domain.Known(int64(2)), domain.NeedDeficit, HousingShelter, 2, .5, true},
		{"unknown", domain.Known(int64(3)), domain.Unknown[int64](), domain.NeedUnknown, HousingShelter, 2, 0, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			f := stableRoutine()
			f.Colonists = tc.population
			f.IndoorCapacity = tc.capacity
			r := needs(t, f, RoutineLatches{})
			found := false
			for _, a := range r.Assessments {
				if a.ID == MaintainHousing {
					found = true
					if a.Need != tc.need || a.Priority != tc.priority {
						t.Fatal(a)
					}
				}
			}
			if !found {
				t.Fatal("missing housing assessment")
			}
			if r.Latches.Housing != tc.phase {
				t.Fatal(r.Latches.Housing)
			}
			v, k := RoutineDevelopmentDeficit(MaintainHousing, f, DefaultRoutinePolicy()).Value()
			if k != tc.known || k && v != tc.deficit {
				t.Fatal(v, k)
			}
			if tc.need == domain.NeedRecovered && hasNeed(r, MaintainHousing) {
				t.Fatal("spare capacity requested work")
			}
		})
	}
	f := stableRoutine()
	f.Colonists = domain.Known(int64(4))
	f.BedCapacity = domain.Known(int64(4))
	if !hasNeed(needs(t, f, RoutineLatches{}), MaintainHousing) {
		t.Fatal("arrival did not renew headroom deficit")
	}
	// A food gap holds the spare room.
	f = stableRoutine()
	f.Colonists, f.IndoorCapacity = domain.Known(int64(3)), domain.Known(int64(3))
	f.FoodPlan = domain.Known(FoodPlan{GapPerDay: 1})
	for _, g := range needs(t, f, RoutineLatches{}).Goals {
		if g.ID == MaintainHousing && !g.Blocked {
			t.Fatal(g)
		}
	}
	// Before StageReserves the spare room is not raised.
	f = stableRoutine()
	f.Colonists, f.IndoorCapacity = domain.Known(int64(3)), domain.Known(int64(3))
	p := DefaultRoutinePolicy()
	p.ColonyStage = StageFoothold
	r, err := DetectRoutine(f, RoutineLatches{}, p)
	if err != nil {
		t.Fatal(err)
	}
	if hasNeed(r, MaintainHousing) || r.Latches.Housing != "" {
		t.Fatal(r.Latches.Housing)
	}
}
