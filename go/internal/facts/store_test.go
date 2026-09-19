package facts

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

func TestStorePutGetFresh(t *testing.T) {
	s := NewStore()
	scope := Scope{Load: "load-1", Generation: 3}
	Put(s, scope, Research, Held[[]string]{Value: []string{"Electricity"}, AsOf: 1000, Complete: true, Source: "rimgovernor/observations_read_research"})
	Put(s, scope, Emergency, Held[int]{Value: 2, AsOf: 1000, Complete: true, Source: "rimgovernor/observations_read_bundle"})
	held, ok := Get[[]string](s, Research)
	if !ok || held.AsOf != 1000 || !held.Complete || len(held.Value) != 1 || held.Source != "rimgovernor/observations_read_research" {
		t.Fatalf("research = %+v ok=%v", held, ok)
	}
	if _, ok := Get[int](s, Research); ok {
		t.Fatal("a section put as one type must not read back as another")
	}
	if _, ok := Get[int](s, Rooms); ok {
		t.Fatal("an absent section reads back")
	}
	// Research tolerates a day; the emergency census only the planning tolerance.
	if !s.Fresh(Research, 1000+bridge.FactTickToleranceResearch) || s.Fresh(Research, 999) {
		t.Fatal("research freshness")
	}
	if s.Fresh(Emergency, 1000+bridge.FactTickToleranceEmergency+1_000_000) || !s.Fresh(Emergency, 1000) {
		t.Fatal("emergency freshness")
	}
	if s.Fresh(Rooms, 1000) {
		t.Fatal("an absent section is never fresh")
	}
	if s.Len() != 2 || s.Scope() != scope {
		t.Fatalf("len=%d scope=%+v", s.Len(), s.Scope())
	}
}

// The refresh cadence table (#360): the population census follows the
// colony's tolerance rather than its invalidation family's, every other
// section its family's; a policy's max age only ever tightens it.
func TestSectionCadence(t *testing.T) {
	for section, want := range map[Section]int64{
		Colony: bridge.FactTickToleranceColony, PlanningCells: bridge.FactTickToleranceColony, Zones: bridge.FactTickToleranceColony, Buildings: bridge.FactTickToleranceColony,
		Population: bridge.FactTickToleranceColony, Pawns: bridge.FactTickTolerancePawns, Emergency: bridge.FactTickToleranceEmergency,
		Rooms: bridge.FactTickToleranceRooms, Research: bridge.FactTickToleranceResearch,
	} {
		if got := section.TickTolerance(); got != want {
			t.Errorf("%s cadence = %d, want %d", section, got, want)
		}
	}
	s := NewStore()
	Put(s, Scope{Load: "load-1", Generation: 1}, Population, Held[int]{Value: 1, AsOf: 1000, Complete: true, Source: "x"})
	Put(s, Scope{Load: "load-1", Generation: 1}, Rooms, Held[int]{Value: 1, AsOf: 1000, Complete: true, Source: "x"})
	if !s.Fresh(Population, 1000+bridge.FactTickToleranceColony) || s.Fresh(Population, 1000+bridge.FactTickToleranceColony+1_000_000) {
		t.Fatal("population freshness follows the colony cadence")
	}
	if !s.FreshWithin(Rooms, 1000, 0) || s.FreshWithin(Rooms, 1001+int64(bridge.FactTickTolerancePawns)+1_000_000, 0) || !s.FreshWithin(Rooms, 1100, 100) || s.FreshWithin(Rooms, 1101+1_000_000, 100) {
		t.Fatal("a max age tightens the cadence")
	}
	if !s.FreshWithin(Rooms, 1000+bridge.FactTickToleranceRooms, 1_000_000) {
		t.Fatal("a max age wider than the cadence leaves it alone")
	}
	if s.FreshWithin(Rooms, 999, bridge.FactTickUnbounded) {
		t.Fatal("a scope behind the row is never fresh")
	}
}

func TestStoreScopeResets(t *testing.T) {
	s := NewStore()
	Put(s, Scope{Load: "a", Generation: 1}, Colony, Held[string]{Value: "x", AsOf: 5, Complete: true})
	Put(s, Scope{Load: "a", Generation: 2}, Pawns, Held[string]{Value: "y", AsOf: 6, Complete: true})
	if _, ok := Get[string](s, Colony); ok {
		t.Fatal("a new generation must empty the store")
	}
	if held, ok := Get[string](s, Pawns); !ok || held.Value != "y" {
		t.Fatalf("pawns = %+v ok=%v", held, ok)
	}
	Put(s, Scope{Load: "b", Generation: 2}, Colony, Held[string]{Value: "z", AsOf: 7, Complete: true})
	if _, ok := Get[string](s, Pawns); ok {
		t.Fatal("a new load must empty the store")
	}
}

func TestStoreInvalidate(t *testing.T) {
	s := NewStore()
	scope := Scope{Load: "a", Generation: 1}
	for _, section := range Sections() {
		Put(s, scope, section, Held[string]{Value: string(section), AsOf: 10, Complete: true})
	}
	s.InvalidateFamily(bridge.FactColony)
	for _, section := range []Section{Colony, PlanningCells, Zones, Buildings} {
		if _, ok := Get[string](s, section); ok {
			t.Fatalf("%s survived its family's invalidation", section)
		}
	}
	for _, section := range []Section{Population, Research, Pawns, Emergency, Rooms} {
		if _, ok := Get[string](s, section); !ok {
			t.Fatalf("%s dropped by another family's invalidation", section)
		}
	}
	s.Invalidate(Research)
	if _, ok := Get[string](s, Research); ok {
		t.Fatal("research survived Invalidate")
	}
	s.InvalidateAll()
	if s.Len() != 0 || s.Scope() != scope {
		t.Fatalf("after InvalidateAll len=%d scope=%+v", s.Len(), s.Scope())
	}
}

func TestStoreStatusAndSpread(t *testing.T) {
	s := NewStore()
	scope := Scope{Load: "a", Generation: 1}
	Put(s, scope, Pawns, Held[string]{AsOf: 130, Complete: false, Source: "p"})
	Put(s, scope, Colony, Held[string]{AsOf: 100, Complete: true, Source: "c"})
	status := s.Status()
	if len(status) != 2 || status[0].Section != Colony || status[1].Section != Pawns || status[0].Family != bridge.FactColony || status[1].Complete || status[0].StoredAt.IsZero() {
		t.Fatalf("status = %+v", status)
	}
	asOf := s.AsOf()
	min, spread := Spread(asOf)
	if min != 100 || spread != 30 {
		t.Fatalf("min=%d spread=%d from %v", min, spread, asOf)
	}
	if min, spread := Spread(nil); min != 0 || spread != 0 {
		t.Fatalf("empty spread = %d %d", min, spread)
	}
	var nilStore *Store
	if nilStore.Status() != nil || nilStore.AsOf() != nil || nilStore.Fresh(Colony, 0) || nilStore.Len() != 0 {
		t.Fatal("a nil store must hold nothing")
	}
	Put(nilStore, scope, Colony, Held[string]{})
	nilStore.Invalidate(Colony)
	nilStore.InvalidateFamily(bridge.FactColony)
	nilStore.InvalidateAll()
}
