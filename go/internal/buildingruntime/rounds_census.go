package buildingruntime

import (
	"context"
	"reflect"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// roundsCensus is the review's observation, retained so the planners that
// step after it plan from the projection it already produced instead of
// re-observing the world. The review reads through the
// colony mirror and decides at the tick its sections are complete
// through; a planner of the same colony, load, map and native generation
// decides there too, on a paused or a running clock, until committed
// clock evidence invalidates the census. A stale read
// is not obeyed: every write carries its CAS evidence, which the native
// refuses once the world moved.
type roundsCensus struct {
	reading     observation.RoundsReading
	rooms       bool
	claims      bool
	definitions map[string]bool
	generation  uint64
	// colony is the mirror version of each colony facts section the
	// review read through the mirror (nil: it read none), which planners
	// of this census serve (colonyFacts).
	colony map[string]uint64
}

// roundsCensusStore keeps the latest census across steps: a planning step
// at the same paused tick reuses it. generation counts the typed-event
// invalidations since the census was retained; a census from an older
// generation is not served even at the same tick.
type roundsCensusStore struct {
	mu                  sync.Mutex
	latest              *roundsCensus
	generation          uint64
	foodIdentity        observation.Identity
	foodPlan            domain.Fact[policy.FoodPlan]
	foodGeneration      uint64
	foodMin, foodTarget float64
	foodOffers          uint64
	// supply is the Round's resource supply plan, built once per supplyKey.
	supply    *resourceSupply
	supplyKey resourceSupplyKey
	// layout is the v2 layout plan the latest review served for layoutScope:
	// a planner whose read misses the census plans on it too, so a
	// fresh read never drops the plan the review already derived.
	layout      domain.Fact[policy.LayoutPlan]
	layoutScope observation.Identity
	// royalty is the royalty read the latest review served for royaltyScope,
	// served to planners the same way as the layout plan.
	royalty      domain.Fact[policy.RoyaltyFacts]
	royaltyScope observation.Identity
	// worship is the ideoligion's required buildings the latest review
	// named, which planners name in their own reads.
	worship []string
	// benches is the mirror version of the bench table the latest review
	// refreshed (0: it read none), which planners of its census serve.
	benches uint64
	// readColony is the colony sections the review in flight published,
	// which retain hands to its census.
	readColony map[string]uint64
}

// rememberColony records the colony sections the review just published.
func (s *roundsCensusStore) rememberColony(versions map[string]uint64) {
	s.mu.Lock()
	s.readColony = versions
	s.mu.Unlock()
}

// rememberBenches records the bench table the review just published.
func (s *roundsCensusStore) rememberBenches(version uint64) {
	s.mu.Lock()
	s.benches = version
	s.mu.Unlock()
}

// benchTable is the bench table the latest review published when its
// census still serves expected and the mirror still holds that table.
func (s *roundsCensusStore) benchTable(m *facts.Store, scope facts.Scope, expected observation.Identity) (facts.Table[string, bridge.GearBenchRead], bool) {
	s.mu.Lock()
	census, generation, version := s.latest, s.generation, s.benches
	s.mu.Unlock()
	if version == 0 || census == nil || census.generation != generation || !sameObservedIdentity(census.reading.Projection.Identity, expected) {
		return facts.Table[string, bridge.GearBenchRead]{}, false
	}
	table, ok := facts.GetTable[string, bridge.GearBenchRead](m, scope, benchSectionName)
	return table, ok && table.Version == version
}

// rememberLayout keeps the v2 layout plan the review served under its identity.
func (s *roundsCensusStore) rememberLayout(identity observation.Identity, layout domain.Fact[policy.LayoutPlan]) {
	s.mu.Lock()
	s.layout, s.layoutScope = layout, identity
	s.mu.Unlock()
}

// rememberRoyalty keeps the royalty read the review served under its identity.
func (s *roundsCensusStore) rememberRoyalty(identity observation.Identity, royalty domain.Fact[policy.RoyaltyFacts]) {
	s.mu.Lock()
	s.royalty, s.royaltyScope = royalty, identity
	s.mu.Unlock()
}

// rememberWorship keeps the required buildings the review named.
func (s *roundsCensusStore) rememberWorship(names []string) {
	s.mu.Lock()
	s.worship = names
	s.mu.Unlock()
}

// rememberedWorship is the required buildings the latest review named.
func (s *roundsCensusStore) rememberedWorship() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.worship
}

// remembered is the latest royalty read, whatever its scope.
func (s *roundsCensusStore) remembered() domain.Fact[policy.RoyaltyFacts] {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.royalty
}

// serveLayout sets the remembered layout plan and royalty read on a fresh
// projection of the same colony, map and load.
func (s *roundsCensusStore) serveLayout(projection *observation.ColonyProjection) {
	s.mu.Lock()
	layout, scope, royalty, royaltyScope := s.layout, s.layoutScope, s.royalty, s.royaltyScope
	s.mu.Unlock()
	id := projection.Identity
	same := func(scope observation.Identity) bool {
		return scope.Colony == id.Colony && scope.Map == id.Map && scope.Load == id.Load
	}
	if _, known := projection.LayoutPlan.Value(); !known {
		if _, known := layout.Value(); known && same(scope) {
			projection.LayoutPlan = layout
		}
	}
	if _, known := projection.Royalty.Value(); !known {
		if _, known := royalty.Value(); known && same(royaltyScope) {
			projection.Royalty = royalty
		}
	}
}

func (s *roundsCensusStore) retain(reading observation.RoundsReading, rooms bool, claims domain.Fact[[]policy.ConstructionClaim]) {
	census := &roundsCensus{reading: reading, rooms: rooms, definitions: map[string]bool{}}
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

// psycasters is the royalty read and the colonists' rows of the latest
// census, which a psycast planner reads its casters from. Both are
// unknown without a current census.
func (s *roundsCensusStore) psycasters() (domain.Fact[policy.RoyaltyFacts], domain.Fact[[]policy.WorkPawn]) {
	s.mu.Lock()
	census, generation, royalty := s.latest, s.generation, s.royalty
	s.mu.Unlock()
	if census == nil || census.generation != generation {
		return domain.Unknown[policy.RoyaltyFacts](), domain.Unknown[[]policy.WorkPawn]()
	}
	return royalty, census.reading.Projection.WorkPawns
}

// invalidate retires the retained census: committed clock evidence made
// some of what it observed stale.
func (s *roundsCensusStore) invalidate() {
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
func (s *roundsCensusStore) lookup(source, reviewerSource any, expected observation.Identity, rooms bool, claims domain.Fact[[]policy.ConstructionClaim], definitions []string) (observation.RoundsReading, bool) {
	if !sameNativeSource(source, reviewerSource) {
		return observation.RoundsReading{}, false
	}
	s.mu.Lock()
	census, generation := s.latest, s.generation
	s.mu.Unlock()
	if census == nil || census.generation != generation || !sameObservedIdentity(census.reading.Projection.Identity, expected) {
		return observation.RoundsReading{}, false
	}
	if rooms && !census.rooms {
		return observation.RoundsReading{}, false
	}
	if _, known := claims.Value(); known && !census.claims {
		return observation.RoundsReading{}, false
	}
	for _, definition := range definitions {
		if !census.definitions[definition] {
			return observation.RoundsReading{}, false
		}
	}
	return census.reading, true
}

// sameObservedIdentity matches the census identity (from colony facts)
// against the planner's expected identity: the same load, map and
// generation, at the census tick or after it. An expected tick before the
// census belongs to an older read.
func sameObservedIdentity(census, expected observation.Identity) bool {
	a, ak := census.NativeGeneration.Value()
	b, bk := expected.NativeGeneration.Value()
	lag := int64(expected.Tick) - int64(census.Tick)
	return census.SameContext(expected) && lag >= 0 && ak && bk && a == b
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

// observeOwned is ObserveRoundsOwned served from the review's census when
// the census covers the request, otherwise a fresh read through source.
func (r *Rounder) observeOwned(ctx context.Context, source observation.RoundsSource, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (observation.RoundsReading, error) {
	if immediateReview(ctx) {
		return observation.ObserveRoundsProtection(ctx, source, r.clock, expected, r.maxAge)
	}
	ctx = standaloneWindow(ctx, source)
	if reading, ok := r.census.lookup(source, r.native, expected, false, claims, definitions); ok {
		return reading, nil
	}
	reading, err := observation.ObserveRoundsOwned(ctx, source, r.clock, expected, r.maxAge, claims, definitions...)
	if err == nil {
		r.census.serveLayout(&reading.Projection)
		r.planFood(&reading.Projection)
		r.reviewMeals(&reading.Projection)
		r.reviewReserve(&reading.Projection)
		r.reviewBabyFeeding(&reading.Projection)
		if r.player != nil && r.methodEnabled(policy.MaintainRituals) {
			if state := r.player.session.State(); state.ObservationKnown && state.Snapshot.Validate() == nil {
				r.reviewRituals(&reading, state.Snapshot)
			}
		}
	}
	return reading, err
}

// benchSource is the bench census a work requirement reads through the
// colony mirror: the review (fresh) refreshes the bench
// section and remembers the table; a planner serves that table while the
// review's census serves it and refreshes the section otherwise. Without
// a mirror (a standalone reviewer) it is native itself.
func (r *Rounder) benchSource(native RoundsWorkBenchSource, expected observation.Identity, fresh bool) RoundsWorkBenchSource {
	if native == nil || r.store == nil {
		return native
	}
	generation, _ := expected.NativeGeneration.Value()
	scope := facts.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(generation)}
	return mirroredBenches{reviewer: r, native: native, scope: scope, expected: expected, fresh: fresh}
}

type mirroredBenches struct {
	reviewer *Rounder
	native   RoundsWorkBenchSource
	scope    facts.Scope
	expected observation.Identity
	fresh    bool
}

func (m mirroredBenches) ReadGearBenches(ctx context.Context, id *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	r := m.reviewer
	if !m.fresh {
		if table, ok := r.census.benchTable(r.store, m.scope, m.expected); ok {
			return benchRows(table.Rows), bridge.Result{}, nil
		}
	}
	tick := int64(m.expected.Tick)
	table, err := publishBenches(ctx, r.store, m.scope, m.native, id, tick)
	if err != nil {
		return nil, bridge.Result{}, err
	}
	if m.fresh {
		r.census.rememberBenches(table.Version)
	}
	return benchRows(table.Rows), bridge.Result{}, nil
}

// observeRooms is ObserveRoundsRooms served from the census when it read
// rooms, otherwise a fresh read through source.
func (r *Rounder) observeRooms(ctx context.Context, source observation.RoundsSource, expected observation.Identity, claims domain.Fact[[]policy.ConstructionClaim], definitions ...string) (observation.RoundsReading, error) {
	if immediateReview(ctx) {
		return observation.ObserveRoundsProtection(ctx, source, r.clock, expected, r.maxAge)
	}
	ctx = standaloneWindow(ctx, source)
	if reading, ok := r.census.lookup(source, r.native, expected, true, claims, definitions); ok {
		return reading, nil
	}
	reading, err := observation.ObserveRoundsRooms(ctx, source, r.clock, expected, r.maxAge, claims, definitions...)
	if err == nil {
		r.census.serveLayout(&reading.Projection)
		r.planFood(&reading.Projection)
		r.reviewMeals(&reading.Projection)
		r.reviewReserve(&reading.Projection)
		r.reviewBabyFeeding(&reading.Projection)
	}
	return reading, err
}

// observeColony is the planning ObserveColony served from the census (its
// ColonyReading is the same census read with the routine sections beside it),
// otherwise a fresh read through source. Project definitions ride the
// snapshot frame's subscription, so a read naming any takes the frame.
func (r *Rounder) observeColony(ctx context.Context, source observation.ColonySource, expected observation.Identity, definitions []string) (observation.ColonyReading, error) {
	if immediateReview(ctx) {
		reading, err := observation.ObserveRoundsProtection(ctx, r.native, r.clock, expected, r.maxAge)
		return reading.ColonyReading, err
	}
	ctx = standaloneWindow(ctx, source)
	if len(definitions) > 0 {
		routine, ok := source.(observation.RoundsSource)
		if !ok {
			return observation.ColonyReading{}, observation.ErrContract
		}
		reading, err := r.observeOwned(ctx, routine, expected, domain.Unknown[[]policy.ConstructionClaim](), definitions...)
		return reading.ColonyReading, err
	}
	if reading, ok := r.census.lookup(source, r.native, expected, false, domain.Unknown[[]policy.ConstructionClaim](), nil); ok {
		return reading.ColonyReading, nil
	}
	reading, err := observation.ObserveColony(ctx, source, r.clock, expected, r.maxAge, true)
	if err == nil {
		r.census.serveLayout(&reading.Projection)
		r.planFood(&reading.Projection)
		r.reviewMeals(&reading.Projection)
		r.reviewReserve(&reading.Projection)
		r.reviewBabyFeeding(&reading.Projection)
	}
	return reading, err
}
