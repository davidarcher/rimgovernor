package buildingruntime

import (
	"context"
	"reflect"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// routineCensus is the review's observation, retained so the planners that
// step after it plan from the projection it already produced instead of
// re-observing the world (#795 step 3). The review reads through the
// colony mirror and decides at the tick its sections are complete
// through; a planner of the same colony, load, map and native generation
// decides there too, on a paused or a running clock, until committed
// clock evidence invalidates the census or the planner's tick passes it by
// more than the pawn cadence (bridge.FactTickTolerancePawns). A stale read
// is not obeyed: every write carries its CAS evidence, which the native
// refuses once the world moved.
type routineCensus struct {
	reading     observation.RoutineReading
	rooms       bool
	claims      bool
	definitions map[string]bool
	generation  uint64
	// colony is the mirror version of each colony facts section the
	// review read through the mirror (nil: it read none), which planners
	// of this census serve (colonyFacts).
	colony map[string]uint64
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
	// grid is the colony grid the latest review served for gridScope (#667):
	// a planner whose read misses the census plans on it too, so a fresh
	// read never drops the grid the review already fixed.
	grid      domain.Fact[policy.ColonyGrid]
	layout    domain.Fact[policy.LayoutPlan]
	gridScope observation.Identity
	// benches is the mirror version of the bench table the latest review
	// refreshed (0: it read none), which planners of its census serve.
	benches uint64
	// readColony is the colony sections the review in flight published,
	// which retain hands to its census.
	readColony map[string]uint64
}

// rememberColony records the colony sections the review just published.
func (s *routineCensusStore) rememberColony(versions map[string]uint64) {
	s.mu.Lock()
	s.readColony = versions
	s.mu.Unlock()
}

// rememberBenches records the bench table the review just published.
func (s *routineCensusStore) rememberBenches(version uint64) {
	s.mu.Lock()
	s.benches = version
	s.mu.Unlock()
}

// benchTable is the bench table the latest review published when its
// census still serves expected and the mirror still holds that table.
func (s *routineCensusStore) benchTable(m *mirror.Mirror, scope mirror.Scope, expected observation.Identity) (mirror.Table[string, bridge.GearBenchRead], bool) {
	s.mu.Lock()
	census, generation, version := s.latest, s.generation, s.benches
	s.mu.Unlock()
	if version == 0 || census == nil || census.generation != generation || !sameObservedIdentity(census.reading.Projection.Identity, expected) {
		return mirror.Table[string, bridge.GearBenchRead]{}, false
	}
	table, ok := mirror.Get[string, bridge.GearBenchRead](m, scope, benchSectionName)
	return table, ok && table.Version == version
}

// rememberGrid keeps the grid and v2 layout plan the review served under its identity.
func (s *routineCensusStore) rememberGrid(identity observation.Identity, grid domain.Fact[policy.ColonyGrid], layout domain.Fact[policy.LayoutPlan]) {
	s.mu.Lock()
	s.grid, s.layout, s.gridScope = grid, layout, identity
	s.mu.Unlock()
}

// serveGrid sets the remembered grid on a fresh projection of the same
// colony, map and load; the grid never moves once established, so a later
// tick of the same load plans on it as the review did.
func (s *routineCensusStore) serveGrid(projection *observation.ColonyProjection) {
	if _, known := projection.ColonyGrid.Value(); known {
		return
	}
	s.mu.Lock()
	grid, layout, scope := s.grid, s.layout, s.gridScope
	s.mu.Unlock()
	id := projection.Identity
	if _, known := grid.Value(); known && scope.Colony == id.Colony && scope.Map == id.Map && scope.Load == id.Load {
		projection.ColonyGrid = grid
		projection.LayoutPlan = layout
	}
}

func (s *routineCensusStore) retain(reading observation.RoutineReading, rooms bool, claims domain.Fact[[]policy.ConstructionClaim]) {
	census := &routineCensus{reading: reading, rooms: rooms, definitions: map[string]bool{}}
	_, census.claims = claims.Value()
	for _, definition := range reading.Projection.Definitions {
		census.definitions[definition.Name] = true
	}
	s.mu.Lock()
	census.generation = s.generation
	census.colony, s.readColony = s.readColony, nil
	s.latest = census
	s.benches = 0
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
// generation, at the census tick or within the pawn cadence after it. An
// expected tick before the census belongs to an older read.
func sameObservedIdentity(census, expected observation.Identity) bool {
	a, ak := census.NativeGeneration.Value()
	b, bk := expected.NativeGeneration.Value()
	lag := int64(expected.Tick) - int64(census.Tick)
	return census.SameContext(expected) && lag >= 0 && lag <= bridge.FactTickTolerancePawns && ak && bk && a == b
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
		r.census.serveGrid(&reading.Projection)
		reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
		r.reviewMeals(&reading.Projection)
		r.reviewReserve(&reading.Projection)
	}
	return reading, err
}

// benchSource is the bench census a work requirement reads through the
// colony mirror (#795 step 3): the review (fresh) refreshes the bench
// section and remembers the table; a planner serves that table while the
// review's census serves it and refreshes the section otherwise. Without
// a mirror (a standalone reviewer) it is native itself.
func (r *RoutineReviewer) benchSource(native RoutineWorkBenchSource, expected observation.Identity, fresh bool) RoutineWorkBenchSource {
	if native == nil || r.mirror == nil {
		return native
	}
	generation, _ := expected.NativeGeneration.Value()
	scope := mirror.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(generation)}
	return mirroredBenches{reviewer: r, native: native, scope: scope, expected: expected, fresh: fresh}
}

type mirroredBenches struct {
	reviewer *RoutineReviewer
	native   RoutineWorkBenchSource
	scope    mirror.Scope
	expected observation.Identity
	fresh    bool
}

func (m mirroredBenches) ReadGearBenches(ctx context.Context, id *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	r := m.reviewer
	if !m.fresh {
		if table, ok := r.census.benchTable(r.mirror, m.scope, m.expected); ok {
			return benchRows(table.Rows), bridge.Result{}, nil
		}
	}
	tick := int64(m.expected.Tick)
	table, _, err := mirror.Refresh(ctx, r.mirror, m.scope, tick, benchSection{native: m.native, id: id, tick: tick})
	if err != nil {
		return nil, bridge.Result{}, err
	}
	if m.fresh {
		r.census.rememberBenches(table.Version)
	}
	return benchRows(table.Rows), bridge.Result{}, nil
}

// observeRooms is ObserveRoutineRooms served from the census when it read
// rooms, otherwise a fresh read through source.
func (r *RoutineReviewer) observeRooms(ctx context.Context, source observation.RoutineSource, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (observation.RoutineReading, error) {
	if reading, ok := r.census.lookup(source, r.native, expected, true, claims, definitions); ok {
		return reading, nil
	}
	reading, err := observation.ObserveRoutineRooms(ctx, source, r.clock, expected, r.maxAge, claims, definitions...)
	if err == nil {
		r.census.serveGrid(&reading.Projection)
		reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
		r.reviewMeals(&reading.Projection)
		r.reviewReserve(&reading.Projection)
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
		r.census.serveGrid(&reading.Projection)
		reading.Projection.Facts.FoodPlan = r.planFood(reading.Projection)
		r.reviewMeals(&reading.Projection)
		r.reviewReserve(&reading.Projection)
	}
	return reading, err
}
