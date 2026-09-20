package bridge

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// TestFactFamilyOfCoversEveryCacheableRead: a cacheable read without a
// family would be memoized per step but never carried across steps.
func TestFactFamilyOfCoversEveryCacheableRead(t *testing.T) {
	for _, name := range nativeReadMethods() {
		family, ok := FactFamilyOf(name)
		if cacheableRead(name) != ok {
			t.Fatalf("%s: cacheable=%v family=%v", name, cacheableRead(name), ok)
		}
		if ok && (family.TickTolerance() == FactTickUnbounded) != (family == FactDefinitions || family == FactWorld) {
			t.Fatal(name, family)
		}
	}
	for _, wire := range []k.FactFamily{k.FactFamily_FACT_FAMILY_DEFINITIONS, k.FactFamily_FACT_FAMILY_RESEARCH} {
		if _, ok := FactFamilyFromWire(wire); !ok {
			t.Fatal(wire)
		}
	}
	if _, ok := FactFamilyFromWire(k.FactFamily_FACT_FAMILY_UNSPECIFIED); ok {
		t.Fatal("unspecified family accepted")
	}
}

// nativeReadMethods enumerates the reviewed reads by probing the allowlist
// with every method protoCall admits.
func nativeReadMethods() []string {
	var out []string
	for _, name := range []string{"rimgovernor/observations_list_supplies", "rimgovernor/observations_read_colony_facts", "rimgovernor/observations_list_buildings", "rimgovernor/observations_list_rooms", "rimgovernor/observations_read_research", "rimgovernor/observations_list_wall_upgrade_sites", "rimgovernor/observations_list_zones", "rimgovernor/observations_read_defense_site", "rimgovernor/observations_read_lines_of_fire", "rimgovernor/observations_read_spatial_access", "rimgovernor/presentation_camera", "rimgovernor/presentation_selection", "rimgovernor/presentation_colonists", "rimgovernor/presentation_notifications", "rimgovernor/presentation_render_state", "rimgovernor/clock_read_events", "rimgovernor/clock_read_status", "rimgovernor/clock_read_attempt", "rimgovernor/operations_preview", "rimgovernor/observations_list_pawns", "rimgovernor/observations_get_cells", "rimgovernor/lifecycle_read_identity", "rimgovernor/lifecycle_read_tick", "rimgovernor/observations_read_status", "rimgovernor/placement_preview", "rimgovernor/authority_read_status", "rimgovernor/receipts_lookup", "rimgovernor/receipts_observe_progress", "rimgovernor/observations_read_caravan_catalog", "rimgovernor/observations_read_world_progression", "rimgovernor/observations_read_world", "rimgovernor/observations_read_bills", "rimgovernor/observations_read_recipes", "rimgovernor/observations_list_resource_sources", "rimgovernor/observations_read_production_policy", "rimgovernor/observations_read_population", "rimgovernor/observations_read_trade_sheet", "rimgovernor/observations_read_excavation_site", "rimgovernor/observations_read_bundle"} {
		if nativeReadMethod(name) {
			out = append(out, name)
		}
	}
	return out
}

// TestFactCacheServesAcrossSteps is the cross-step contract: a second step
// at the same tick takes its facts from the parent after one identity read;
// a tick advance keeps world (tick-independent) rows and drops rooms; a
// family invalidation drops one family; a write drops everything.
func TestFactCacheServesAcrossSteps(t *testing.T) {
	server := newReadCacheServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	parent := NewFactCache()
	step := func(t *testing.T) (*StepReadCache, context.Context) {
		cache := NewChildReadCache(parent)
		ctx := WithStepReadCache(context.Background(), cache)
		// The identity read establishes the step's scope; before it a
		// parent row cannot be trusted, so it always crosses the bridge.
		if _, _, err := client.Identity(ctx); err != nil {
			t.Fatal(err)
		}
		return cache, ctx
	}
	read := func(t *testing.T, ctx context.Context) {
		if _, _, err := client.ReadWorld(ctx, pbIdentity(), 42, 0); err != nil {
			t.Fatal(err)
		}
		if _, _, err := client.ReadTemperatureRooms(ctx, pbIdentity()); err != nil {
			t.Fatal(err)
		}
	}
	counts := func() (int64, int64, int64) {
		return server.count("rimgovernor/lifecycle_read_identity"), server.count("rimgovernor/observations_read_world"), server.count("rimgovernor/observations_list_rooms")
	}

	// Step 1 fills the parent.
	cache, ctx := step(t)
	read(t, ctx)
	if id, world, rooms := counts(); id != 1 || world != 1 || rooms != 1 {
		t.Fatal(id, world, rooms)
	}
	if stats := cache.Stats(); stats.ParentHits != 0 || stats.Misses != 3 {
		t.Fatalf("step 1 %+v", stats)
	}
	if parent.Len() != 3 {
		t.Fatal(parent.Len())
	}

	// Step 2, same tick: only the identity read is native.
	cache, ctx = step(t)
	read(t, ctx)
	read(t, ctx) // step-local hits still sit above the parent
	if id, world, rooms := counts(); id != 2 || world != 1 || rooms != 1 {
		t.Fatal(id, world, rooms)
	}
	if stats := cache.Stats(); stats.ParentHits != 2 || stats.Misses != 1 || stats.Hits != 2 {
		t.Fatalf("step 2 %+v", stats)
	}

	// Step 3 after a tick advance past the rooms tolerance: world survives,
	// rooms is re-read.
	server.tick.Add(FactTickToleranceRooms + 1)
	cache, ctx = step(t)
	read(t, ctx)
	if id, world, rooms := counts(); id != 3 || world != 1 || rooms != 2 {
		t.Fatal(id, world, rooms)
	}
	if stats := cache.Stats(); stats.ParentHits != 1 {
		t.Fatalf("step 3 %+v", stats)
	}

	// A family invalidation drops just that family.
	parent.InvalidateFamilies(FactWorld)
	cache, ctx = step(t)
	read(t, ctx)
	if id, world, rooms := counts(); id != 4 || world != 2 || rooms != 2 {
		t.Fatal(id, world, rooms)
	}
	if stats := cache.Stats(); stats.ParentHits != 1 {
		t.Fatalf("family invalidation %+v", stats)
	}

	// A write through a step drops the parent too.
	if _, err := client.protoCall(ctx, "rimgovernor/clock_pause", clockTestOwned(), &k.StatusReply{}); err != nil {
		t.Fatal(err)
	}
	if parent.Len() != 0 {
		t.Fatalf("write left %d parent rows", parent.Len())
	}
	// The write moved the native generation: rows read afterwards belong
	// to the new scope, and a later step at that scope is served again.
	_, ctx = step(t)
	read(t, ctx)
	if id, world, rooms := counts(); id != 5 || world != 3 || rooms != 3 {
		t.Fatal(id, world, rooms)
	}
	cache, ctx = step(t)
	read(t, ctx)
	if id, world, rooms := counts(); id != 6 || world != 3 || rooms != 3 {
		t.Fatal(id, world, rooms)
	}
	if stats := cache.Stats(); stats.ParentHits != 2 {
		t.Fatalf("post-write step %+v", stats)
	}
	if stats := parent.Stats(); stats.Hits != 6 || stats.Invalidations < 2 {
		t.Fatalf("parent %+v", stats)
	}
}

// TestStepReadCacheWriteFamiliesNarrowTheParentDrop: a write through a
// step cache that names the families it can change drops those in the
// parent and keeps the rest, so a dispatch every step no longer empties
// the cross-step cache (#593); a step without the narrowing, or one that
// reports everything, drops the parent whole as before. The step's own
// rows go either way.
func TestStepReadCacheWriteFamiliesNarrowTheParentDrop(t *testing.T) {
	scope := readScope{load: "l", tick: 1, generation: 1}
	world := readCacheKey{method: "rimgovernor/observations_read_world", request: "a"}
	rooms := readCacheKey{method: "rimgovernor/observations_list_rooms", request: "a"}
	fill := func() *FactCache {
		parent := NewFactCache()
		parent.store(world, scope, []byte("w"), Result{})
		parent.store(rooms, scope, []byte("r"), Result{})
		return parent
	}
	held := func(parent *FactCache, key readCacheKey) bool {
		_, _, ok := parent.lookup(key, scope)
		return ok
	}

	parent := fill()
	cache := NewChildReadCache(parent)
	cache.seed(world, scope, []byte("w"), Result{})
	cache.SetWriteFamilies(func() (bool, []FactFamily) { return false, []FactFamily{FactRooms} })
	cache.Invalidate()
	if !held(parent, world) || held(parent, rooms) {
		t.Fatalf("narrowed write: world=%v rooms=%v", held(parent, world), held(parent, rooms))
	}
	if cache.Stats().Invalidations != 1 || len(cache.entries) != 0 {
		t.Fatalf("step rows survived the write: %+v %d", cache.Stats(), len(cache.entries))
	}

	parent = fill()
	cache = NewChildReadCache(parent)
	cache.SetWriteFamilies(func() (bool, []FactFamily) { return true, nil })
	cache.Invalidate()
	if parent.Len() != 0 {
		t.Fatalf("everything left %d rows", parent.Len())
	}

	parent = fill()
	NewChildReadCache(parent).Invalidate()
	if parent.Len() != 0 {
		t.Fatalf("default left %d rows", parent.Len())
	}
}

// TestFactCacheIgnoresRowsFromAnotherScope: rows stored under one
// (load, generation) are never served under another, and storing under a
// new scope discards the old rows rather than mixing them.
func TestFactCacheIgnoresRowsFromAnotherScope(t *testing.T) {
	parent := NewFactCache()
	key := readCacheKey{method: "rimgovernor/observations_read_world", request: "a"}
	parent.store(key, readScope{load: "l", tick: 1, generation: 1}, []byte("x"), Result{})
	if _, _, ok := parent.lookup(key, readScope{load: "l", tick: 9, generation: 1}); !ok {
		t.Fatal("tick-independent row not served across ticks")
	}
	if _, _, ok := parent.lookup(key, readScope{load: "l", tick: 1, generation: 2}); ok {
		t.Fatal("row served under another generation")
	}
	if _, _, ok := parent.lookup(key, readScope{load: "other", tick: 1, generation: 1}); ok {
		t.Fatal("row served under another load")
	}
	rooms := readCacheKey{method: "rimgovernor/observations_list_rooms", request: "b"}
	parent.store(rooms, readScope{load: "l", tick: 1, generation: 1}, []byte("y"), Result{})
	if _, _, ok := parent.lookup(rooms, readScope{load: "l", tick: 1 + FactTickToleranceRooms, generation: 1}); !ok {
		t.Fatal("rooms row not served within its tolerance")
	}
	if _, _, ok := parent.lookup(rooms, readScope{load: "l", tick: 0, generation: 1}); ok || parent.Len() != 1 {
		t.Fatal("rooms row served under a rewound tick", parent.Len())
	}
	parent.store(rooms, readScope{load: "l", tick: 1, generation: 1}, []byte("y"), Result{})
	if _, _, ok := parent.lookup(rooms, readScope{load: "l", tick: 2 + FactTickToleranceRooms, generation: 1}); ok || parent.Len() != 1 {
		t.Fatal("rooms row served or kept past its tolerance", parent.Len())
	}
	parent.store(key, readScope{load: "l", tick: 1, generation: 2}, []byte("z"), Result{})
	if parent.Len() != 1 {
		t.Fatalf("scope change kept old rows: %d", parent.Len())
	}
	parent.store(readCacheKey{method: "rimgovernor/clock_read_status", request: ""}, readScope{load: "l", tick: 1, generation: 2}, nil, Result{})
	if parent.Len() != 1 {
		t.Fatal("uncacheable method stored")
	}
}

// TestFactCacheContextServesTheSeededIdentity: the identity row the bundle
// seeds (or an identity read stores) names the current load without a
// round trip and without fixing a step scope; a tick advance keeps it, a
// write or an identity invalidation drops it, and a later identity read at
// the new scope restores it (#181).
func TestFactCacheContextServesTheSeededIdentity(t *testing.T) {
	parent := NewFactCache()
	if _, ok := parent.Context(); ok {
		t.Fatal("empty cache served a context")
	}
	server := newBundleServer()
	client := testClient(t, &testServer{schema: protoSchema, handler: server.handle}, time.Second)
	stored := time.Date(2026, 9, 18, 0, 0, 0, 0, time.UTC)
	factCacheNow = func() time.Time { return stored }
	defer func() { factCacheNow = time.Now }()
	ctx := WithStepReadCache(context.Background(), NewChildReadCache(parent))
	if _, _, err := client.ReadBundle(ctx, bundleTestRequest()); err != nil {
		t.Fatal(err)
	}
	observed, ok := parent.Context()
	if !ok || observed.Context.GetTick() != 12 || observed.Context.GetIdentity().GetLoadToken() != pbIdentity().GetLoadToken() || observed.Paused || !observed.StoredAt.Equal(stored) {
		t.Fatal(observed, ok)
	}
	// Serving it establishes no step scope: a fresh step's first read is native.
	cache := NewChildReadCache(parent)
	ctx = WithStepReadCache(context.Background(), cache)
	if _, _, err := client.Tick(ctx); err != nil || server.calls["rimgovernor/lifecycle_read_tick"].Load() != 1 {
		t.Fatal(err, "tick served from the parent before a native read")
	}
	parent.InvalidateFamilies(FactIdentity)
	if _, ok = parent.Context(); ok {
		t.Fatal("identity invalidation kept the context")
	}
	identity := testClient(t, &testServer{schema: protoSchema, handler: newReadCacheServer().handle}, time.Second)
	if _, _, err := identity.Identity(WithStepReadCache(context.Background(), NewChildReadCache(parent))); err != nil {
		t.Fatal(err)
	}
	if observed, ok = parent.Context(); !ok || observed.Context.GetIdentity().GetLoadToken() != pbIdentity().GetLoadToken() || !observed.Paused {
		t.Fatal("identity read did not restore the context", observed, ok)
	}
	cache.Invalidate()
	if _, ok = parent.Context(); ok {
		t.Fatal("write kept the context")
	}
}

// TestFactFamilyTickTolerance is the per-family staleness contract (#243):
// a row serves a later scope while the advance is within the family's
// tolerance, never a scope behind it, and the tolerances order as the
// facts change: identity and research on the scale of a day, colony and
// rooms an hour, pawns and the emergency census minutes.
func TestFactFamilyTickTolerance(t *testing.T) {
	for _, family := range FactFamilies() {
		tolerance := family.TickTolerance()
		if !family.Fresh(100, 100) || family.Fresh(100, 99) {
			t.Fatal(family, "same tick or rewind")
		}
		if tolerance == FactTickUnbounded {
			if !family.Fresh(0, 1<<40) {
				t.Fatal(family, "bounded")
			}
			continue
		}
		if tolerance < 0 || !family.Fresh(100, 100+tolerance) || family.Fresh(100, 101+tolerance) {
			t.Fatal(family, tolerance)
		}
	}
	if FactTickToleranceIdentity != 0 || FactTickToleranceResearch < FactTickToleranceColony || FactTickToleranceColony < FactTickTolerancePawns || FactTickToleranceRooms < FactTickTolerancePawns || FactTickToleranceEmergency > FactTickTolerancePawns {
		t.Fatal("tolerances out of order")
	}
	if PlanningTickTolerance() != FactTickTolerancePawns {
		t.Fatal(PlanningTickTolerance())
	}
	if FactFamily("other").TickTolerance() != 0 || FactFamily("other").Fresh(1, 2) {
		t.Fatal("unknown family tolerates an advance")
	}
}

// TestFactFamilyOutrunFollowsFresh: a boundary pairing a cached family read
// with a fresher one (haul targets beside a pawn read) judges the pair by
// the same widened tolerance the cache serves it under, so a running
// window's LiveDrift never turns a row the cache still serves into wrong
// evidence (#328).
func TestFactFamilyOutrunFollowsFresh(t *testing.T) {
	t.Cleanup(func() { domain.SetLiveDrift(0) })
	for _, drift := range []domain.Tick{0, 20000} {
		domain.SetLiveDrift(drift)
		for _, family := range FactFamilies() {
			for advance := int64(0); advance <= 70000; advance += 500 {
				if family.Outrun(100, 100+advance) == family.Fresh(100, 100+advance) {
					t.Fatal(family, drift, advance)
				}
			}
			if family.Outrun(100, 99) {
				t.Fatal(family, "a later read behind the row is not an outrun")
			}
		}
	}
}
