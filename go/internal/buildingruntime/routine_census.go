package buildingruntime

import (
	"context"
	"reflect"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// routineCensus is the review's own paused-tick observation, retained so the
// planners that step after it in the same tick plan from the projection it
// already produced instead of re-observing the world. It is exactly what a
// planner's own observe would return under the same identity (same colony,
// load, map, tick and native generation: the game is paused and nothing has
// been written), so reuse changes no decision; a planner still takes its
// own fresh identity read immediately before it writes.
type routineCensus struct {
	reading     observation.RoutineReading
	rooms       bool
	claims      bool
	definitions map[string]bool
	generation  uint64
}

// routineCensusStore keeps the latest census across steps: a planning step
// at the same paused tick reuses it. generation counts the typed-event
// invalidations since the census was retained; a census from an older
// generation is not served even at the same tick.
type routineCensusStore struct {
	mu                  sync.Mutex
	latest              *routineCensus
	generation          uint64
	foodIdentity        observation.Identity
	foodPlan            domain.Fact[policy.FoodPlan]
	foodGeneration      uint64
	foodMin, foodTarget float64
}

func (s *routineCensusStore) retain(reading observation.RoutineReading, rooms bool, claims domain.Fact[[]policy.ConstructionClaim]) {
	census := &routineCensus{reading: reading, rooms: rooms, definitions: map[string]bool{}}
	_, census.claims = claims.Value()
	for _, definition := range reading.Projection.Definitions {
		census.definitions[definition.Name] = true
	}
	s.mu.Lock()
	census.generation = s.generation
	s.latest = census
	s.mu.Unlock()
}

// invalidate retires the retained census: committed clock evidence made
// some of what it observed stale.
func (s *routineCensusStore) invalidate() {
	s.mu.Lock()
	s.generation++
	s.mu.Unlock()
}

// lookup returns the retained census when it can stand in for an observation
// a planner would take through source under expected: the same native source
// (a planner composed over a different source observes for itself), the same
// paused identity, rooms present when asked for, every requested definition
// already in the census (the default planning census carries the planner
// definitions; a missing one falls back to a fresh read), and construction
// claims resolved when the planner supplies them.
func (s *routineCensusStore) lookup(source, reviewerSource any, expected observation.Identity, rooms bool, claims domain.Fact[[]policy.ConstructionClaim], definitions []string) (observation.RoutineReading, bool) {
	if !sameNativeSource(source, reviewerSource) {
		return observation.RoutineReading{}, false
	}
	s.mu.Lock()
	census, generation := s.latest, s.generation
	s.mu.Unlock()
	if census == nil || census.generation != generation || !sameObservedIdentity(census.reading.Projection.Identity, expected) {
		return observation.RoutineReading{}, false
	}
	if rooms && !census.rooms {
		return observation.RoutineReading{}, false
	}
	if _, known := claims.Value(); known && !census.claims {
		return observation.RoutineReading{}, false
	}
	for _, definition := range definitions {
		if !census.definitions[definition] {
			return observation.RoutineReading{}, false
		}
	}
	return census.reading, true
}

// sameObservedIdentity matches the census identity (from colony facts)
// against the planner's expected identity: the same load, map and
// generation, at the same tick. A census is one step's read; a later step
// at another tick reads its own.
func sameObservedIdentity(census, expected observation.Identity) bool {
	a, ak := census.NativeGeneration.Value()
	b, bk := expected.NativeGeneration.Value()
	return census.SameContext(expected) && census.Tick == expected.Tick && ak && bk && a == b
}

// sameNativeSource reports whether two planner sources are one native
// client. Sources of an uncomparable dynamic type never match.
func sameNativeSource(a, b any) bool {
	if a == nil || b == nil {
		return false
	}
	ta, tb := reflect.TypeOf(a), reflect.TypeOf(b)
	return ta == tb && ta.Comparable() && a == b
}

// observeOwned is ObserveRoutineOwned served from the review's census when
// the census covers the request, otherwise a fresh read through source.
func (r *RoutineReviewer) observeOwned(ctx context.Context, source observation.RoutineSource, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (observation.RoutineReading, error) {
	if reading, ok := r.census.lookup(source, r.native, expected, false, claims, definitions); ok {
		return reading, nil
	}
	reading, err := observation.ObserveRoutineOwned(ctx, source, r.clock, expected, r.maxAge, claims, definitions...)
	if err == nil {
		reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
		r.reviewMeals(&reading.Projection)
	}
	return reading, err
}

// observeRooms is ObserveRoutineRooms served from the census when it read
// rooms, otherwise a fresh read through source.
func (r *RoutineReviewer) observeRooms(ctx context.Context, source observation.RoutineSource, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (observation.RoutineReading, error) {
	if reading, ok := r.census.lookup(source, r.native, expected, true, claims, definitions); ok {
		return reading, nil
	}
	reading, err := observation.ObserveRoutineRooms(ctx, source, r.clock, expected, r.maxAge, claims, definitions...)
	if err == nil {
		reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
		r.reviewMeals(&reading.Projection)
	}
	return reading, err
}

// observeColony is the planning ObserveColony served from the census (its
// ColonyReading is the same census read with the routine sections beside it),
// otherwise a fresh read through source.
func (r *RoutineReviewer) observeColony(ctx context.Context, source observation.ColonySource, expected observation.Identity, definitions []string) (observation.ColonyReading, error) {
	if reading, ok := r.census.lookup(source, r.native, expected, false, domain.Unknown[[]policy.ConstructionClaim](), definitions); ok {
		return reading.ColonyReading, nil
	}
	reading, err := observation.ObserveColony(ctx, source, r.clock, expected, r.maxAge, true, definitions)
	if err == nil {
		reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
		r.reviewMeals(&reading.Projection)
	}
	return reading, err
}
