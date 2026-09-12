package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestExpansionHeadroomUnknownAndHousingGate(t *testing.T) {
	for _, tc := range []struct {
		name                 string
		population, capacity domain.Fact[int64]
		need                 domain.NeedState
		blocked              bool
		deficit              float64
		known                bool
	}{
		{"spare", domain.Known(int64(3)), domain.Known(int64(4)), domain.NeedRecovered, false, 0, true},
		{"full", domain.Known(int64(3)), domain.Known(int64(3)), domain.NeedDeficit, false, .25, true},
		{"short", domain.Known(int64(3)), domain.Known(int64(2)), domain.NeedDeficit, true, .5, true},
		{"unknown", domain.Known(int64(3)), domain.Unknown[int64](), domain.NeedUnknown, true, 0, false},
		{"empty", domain.Known(int64(0)), domain.Known(int64(4)), domain.NeedUnknown, true, 0, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			f := stableRoutine()
			f.Colonists = tc.population
			f.IndoorCapacity = tc.capacity
			r := needs(t, f, RoutineLatches{})
			found := false
			for _, a := range r.Assessments {
				if a.ID == EnsureExpansion {
					found = true
					if a.Need != tc.need || a.Priority != 4 {
						t.Fatal(a)
					}
				}
			}
			if !found {
				t.Fatal("missing maintained expansion")
			}
			v, k := RoutineDevelopmentDeficit(EnsureExpansion, f, DefaultRoutinePolicy()).Value()
			if k != tc.known || k && v != tc.deficit {
				t.Fatal(v, k)
			}
			for _, g := range r.Goals {
				if g.ID == EnsureExpansion && g.Blocked != tc.blocked {
					t.Fatal(g)
				}
			}
			if tc.need == domain.NeedRecovered && hasNeed(r, EnsureExpansion) {
				t.Fatal("spare capacity requested work")
			}
		})
	}
	f := stableRoutine()
	f.Colonists = domain.Known(int64(4))
	f.BedCapacity = domain.Known(int64(4))
	if !hasNeed(needs(t, f, RoutineLatches{}), EnsureExpansion) {
		t.Fatal("arrival did not renew headroom deficit")
	}
}
