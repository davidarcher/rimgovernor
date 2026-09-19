package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// TestPlannersPlanFromReviewCensus: planners composed over the reviewer's
// own native source plan from the review's retained reading instead of
// re-reading colony facts, and still admit their methods from it.
func TestPlannersPlanFromReviewCensus(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	_, _, _, _, native, planners := composedRoutineFixture(t)
	reads := native.reads
	if reads == 0 {
		t.Fatal("review never read colony facts")
	}
	acquisition, err := planners.acquisition.Step(ctx)
	if err != nil || acquisition.Reason != BuildingMethodAdmitted {
		t.Fatalf("acquisition: %+v %v", acquisition, err)
	}
	work, err := planners.work.Step(ctx)
	if err != nil || work.Reason != BuildingMethodAdmitted {
		t.Fatalf("work: %+v %v", work, err)
	}
	if native.reads != reads {
		t.Fatalf("planners re-read colony facts the review already observed: %d -> %d", reads, native.reads)
	}
}

func TestRoutineCensusLookupConditions(t *testing.T) {
	t.Parallel()
	identity := observation.Identity{Colony: "c", Load: "l", Map: 1, Tick: 100, NativeGeneration: domain.Known(domain.NativeGeneration(3)), Paused: domain.Known(true)}
	reading := observation.RoutineReading{}
	reading.Projection.Identity = identity
	reading.Projection.Identity.Paused = domain.Unknown[bool]() // colony facts carry no pause state
	reading.Projection.Definitions = []observation.PlanningDefinition{{Name: "Wall"}, {Name: "SleepingSpot"}}
	source := &routineNative{}
	claims := domain.Known([]policy.ConstructionClaim{})
	var store routineCensusStore
	if _, ok := store.lookup(source, source, identity, false, claims, nil); ok {
		t.Fatal("empty store served a reading")
	}
	store.retain(reading, false, claims)
	cases := []struct {
		name        string
		source      any
		identity    observation.Identity
		rooms       bool
		claims      domain.Fact[[]policy.ConstructionClaim]
		definitions []string
		want        bool
	}{
		{"same request", source, identity, false, claims, []string{"Wall", "SleepingSpot"}, true},
		{"claims not needed", source, identity, false, domain.Unknown[[]policy.ConstructionClaim](), nil, true},
		{"other source", &routineNative{}, identity, false, claims, nil, false},
		{"rooms wanted", source, identity, true, claims, nil, false},
		{"definition missing", source, identity, false, claims, []string{"Campfire"}, false},
		{"later tick", source, func() observation.Identity { i := identity; i.Tick++; return i }(), false, claims, nil, false},
		{"other generation", source, func() observation.Identity {
			i := identity
			i.NativeGeneration = domain.Known(domain.NativeGeneration(4))
			return i
		}(), false, claims, nil, false},
		{"other load", source, func() observation.Identity { i := identity; i.Load = "x"; return i }(), false, claims, nil, false},
		{"not paused", source, func() observation.Identity { i := identity; i.Paused = domain.Unknown[bool](); return i }(), false, claims, nil, true},
	}
	for _, tc := range cases {
		if _, ok := store.lookup(tc.source, source, tc.identity, tc.rooms, tc.claims, tc.definitions); ok != tc.want {
			t.Errorf("%s: served=%v want %v", tc.name, ok, tc.want)
		}
	}
	// A census without claims cannot serve a planner that supplies them;
	// one with rooms serves planners with or without them.
	var partial routineCensusStore
	partial.retain(reading, true, domain.Unknown[[]policy.ConstructionClaim]())
	if _, ok := partial.lookup(source, source, identity, false, claims, nil); ok {
		t.Fatal("claimless census served a claimed request")
	}
	if _, ok := partial.lookup(source, source, identity, true, domain.Unknown[[]policy.ConstructionClaim](), nil); !ok {
		t.Fatal("rooms census refused a rooms request")
	}
	// A typed-event invalidation retires the census at the same tick until
	// the reviewer retains a fresh one.
	partial.invalidate()
	if _, ok := partial.lookup(source, source, identity, true, domain.Unknown[[]policy.ConstructionClaim](), nil); ok {
		t.Fatal("invalidated census was served")
	}
	partial.retain(reading, true, domain.Unknown[[]policy.ConstructionClaim]())
	if _, ok := partial.lookup(source, source, identity, true, domain.Unknown[[]policy.ConstructionClaim](), nil); !ok {
		t.Fatal("re-retained census refused")
	}
}
