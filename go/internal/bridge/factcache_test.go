package bridge

import (
	"context"
	"testing"
	"time"

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
		if ok && family.SurvivesTick() != (family == FactDefinitions || family == FactWorld) {
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

	// Step 3 after a tick advance: world survives, rooms is re-read.
	server.tick.Add(60)
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
	if _, _, ok := parent.lookup(rooms, readScope{load: "l", tick: 2, generation: 1}); ok || parent.Len() != 1 {
		t.Fatal("same-tick row served or kept after a tick advance", parent.Len())
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
