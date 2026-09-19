package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// entityFake answers the three entity reads: every full ask lists full,
// every delta ask (since > 0) answers delta the way a native with entity
// tracking does, or expired when set. since records every ask per section.
type entityFake struct {
	tick    int64
	since   map[facts.Section][]int64
	full    map[string]*o.ZoneState
	delta   bridge.EntityRows[*o.ZoneState]
	expired bool
	err     error
}

func (f *entityFake) ask(section facts.Section, since int64) {
	if f.since == nil {
		f.since = map[facts.Section][]int64{}
	}
	f.since[section] = append(f.since[section], since)
}

func (f *entityFake) ReadZones(_ context.Context, _ *c.Identity, since int64) (bridge.EntityRows[*o.ZoneState], bridge.Result, error) {
	f.ask(facts.Zones, since)
	if f.err != nil {
		return bridge.EntityRows[*o.ZoneState]{}, bridge.Result{}, f.err
	}
	context := &c.ObservationContext{Tick: proto.Int64(f.tick)}
	if since > 0 {
		if f.expired {
			f.expired = false
			return bridge.EntityRows[*o.ZoneState]{}, bridge.Result{}, errors.Join(bridge.ErrDeltaExpired, bridge.ErrUnavailable)
		}
		out := f.delta
		out.Context, out.Delta = context, true
		return out, bridge.Result{}, nil
	}
	return bridge.EntityRows[*o.ZoneState]{Context: context, Rows: f.full}, bridge.Result{}, nil
}

func (f *entityFake) ReadBuildings(_ context.Context, _ *c.Identity, since int64) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	f.ask(facts.Buildings, since)
	return bridge.EntityRows[*o.BuildingState]{Context: &c.ObservationContext{Tick: proto.Int64(f.tick)}, Rows: map[string]*o.BuildingState{}, Delta: since > 0}, bridge.Result{}, nil
}

func (f *entityFake) ReadBillStacks(_ context.Context, _ *c.Identity, since int64) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	f.ask(facts.Bills, since)
	return bridge.EntityRows[*o.BillStack]{Context: &c.ObservationContext{Tick: proto.Int64(f.tick)}, Rows: map[string]*o.BillStack{}, Delta: since > 0}, bridge.Result{}, nil
}

func zoneState(id, label string) *o.ZoneState {
	return &o.ZoneState{Id: proto.String(id), Label: proto.String(label)}
}

// Nothing held reads every section in full; a fresh section is left
// alone; a stale one is read as a delta since its as-of tick and merged,
// removed ids dropped; an invalidation makes the next refresh a delta.
func TestRefreshEntitySectionsFullThenDelta(t *testing.T) {
	f := newClockFacts(nil, nil)
	scope := facts.Scope{Load: "load", Generation: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	native := &entityFake{tick: 100, full: map[string]*o.ZoneState{"Zone_1": zoneState("Zone_1", "a"), "Zone_2": zoneState("Zone_2", "b")}}
	refreshEntitySections(context.Background(), native, f, identity, scope, 100)
	for _, section := range []facts.Section{facts.Zones, facts.Buildings, facts.Bills} {
		if asks := native.since[section]; len(asks) != 1 || asks[0] != 0 || !f.store.Fresh(section, 100) {
			t.Fatalf("%s: since=%v fresh=%v", section, asks, f.store.Fresh(section, 100))
		}
	}
	held, ok := facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	if !ok || len(held.Value) != 2 || held.AsOf != 100 || held.Source != "rimgovernor/observations_list_zones" {
		t.Fatalf("%+v ok=%v", held, ok)
	}
	// Fresh: no read.
	refreshEntitySections(context.Background(), native, f, identity, scope, 200)
	if len(native.since[facts.Zones]) != 1 {
		t.Fatal(native.since)
	}
	// Stale by cadence: a delta since 100 merged by id.
	native.tick = 5000
	native.delta = bridge.EntityRows[*o.ZoneState]{Rows: map[string]*o.ZoneState{"Zone_2": zoneState("Zone_2", "renamed"), "Zone_3": zoneState("Zone_3", "c")}, Removed: []string{"Zone_1"}, Unchanged: 0}
	refreshEntitySections(context.Background(), native, f, identity, scope, 5000)
	held, _ = facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	if asks := native.since[facts.Zones]; len(asks) != 2 || asks[1] != 100 || held.AsOf != 5000 || len(held.Value) != 2 || held.Value["Zone_2"].GetLabel() != "renamed" || held.Value["Zone_3"] == nil || held.Value["Zone_1"] != nil {
		t.Fatalf("%+v since=%v", held.Value, asks)
	}
	// A colony invalidation keeps the section and marks it stale (#358
	// point 5): the next review refresh is a delta since the merge, not a
	// full read.
	f.store.InvalidateFamily(bridge.FactColony)
	native.delta = bridge.EntityRows[*o.ZoneState]{Rows: map[string]*o.ZoneState{}, Unchanged: 2}
	refreshEntitySections(context.Background(), native, f, identity, scope, 5001)
	held, _ = facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	if asks := native.since[facts.Zones]; len(asks) != 3 || asks[2] != 5000 || len(held.Value) != 2 || !f.store.Fresh(facts.Zones, 5001) {
		t.Fatalf("%+v since=%v", held.Value, asks)
	}
}

// An expired delta falls back to a full read; a failed read keeps the
// held section; a scope change reads in full.
func TestRefreshEntitySectionsExpiryFailureAndScope(t *testing.T) {
	f := newClockFacts(nil, nil)
	scope := facts.Scope{Load: "load", Generation: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	facts.Put(f.store, scope, facts.Zones, facts.Held[EntitySection[*o.ZoneState]]{Value: EntitySection[*o.ZoneState]{"Zone_1": zoneState("Zone_1", "a")}, AsOf: 100, Complete: true, Source: "x"})
	native := &entityFake{tick: 9000, expired: true, full: map[string]*o.ZoneState{"Zone_5": zoneState("Zone_5", "e")}}
	refreshEntitySections(context.Background(), native, f, identity, scope, 9000)
	held, _ := facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	if asks := native.since[facts.Zones]; len(asks) != 2 || asks[0] != 100 || asks[1] != 0 || len(held.Value) != 1 || held.Value["Zone_5"] == nil || held.AsOf != 9000 {
		t.Fatalf("%+v since=%v", held.Value, asks)
	}
	native.err = errors.New("transport")
	native.tick = 20000
	refreshEntitySections(context.Background(), native, f, identity, scope, 20000)
	if held, _ = facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones); held.AsOf != 9000 || len(held.Value) != 1 {
		t.Fatalf("a failed read must keep the held section: %+v", held)
	}
	native.err = nil
	other := facts.Scope{Load: "load", Generation: 2}
	refreshEntitySections(context.Background(), native, f, identity, other, 20000)
	if asks := native.since[facts.Zones]; asks[len(asks)-1] != 0 || f.store.Scope() != other {
		t.Fatalf("a new scope must read in full: since=%v scope=%+v", asks, f.store.Scope())
	}
}

// The resync backstop: a requested resync reads the section in full
// beside the delta and files the full read.
func TestRefreshEntitySectionsResync(t *testing.T) {
	for _, tc := range []struct {
		requested bool
		refreshes int
		resync    bool
	}{{false, 0, false}, {false, 7, true}, {false, 8, false}, {true, 3, true}} {
		if got := entitySectionsResync(tc.requested, tc.refreshes); got != tc.resync {
			t.Errorf("requested=%v refreshes=%d: resync=%v, want %v", tc.requested, tc.refreshes, got, tc.resync)
		}
	}
	f := newClockFacts(nil, nil)
	scope := facts.Scope{Load: "load", Generation: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	facts.Put(f.store, scope, facts.Zones, facts.Held[EntitySection[*o.ZoneState]]{Value: EntitySection[*o.ZoneState]{"Zone_1": zoneState("Zone_1", "a"), "Zone_2": zoneState("Zone_2", "b")}, AsOf: 100, Complete: true, Source: "x"})
	native := &entityFake{tick: 5000, delta: bridge.EntityRows[*o.ZoneState]{Rows: map[string]*o.ZoneState{}, Unchanged: 2}, full: map[string]*o.ZoneState{"Zone_1": zoneState("Zone_1", "a")}}
	f.store.RequestResync(facts.Zones)
	refreshEntitySections(context.Background(), native, f, identity, scope, 5000)
	held, _ := facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	// The full read (one row) replaces the merged delta (two rows).
	if asks := native.since[facts.Zones]; len(asks) != 2 || asks[0] != 100 || asks[1] != 0 || len(held.Value) != 1 || held.AsOf != 5000 || f.entityRefreshes[facts.Zones] != 1 {
		t.Fatalf("%+v since=%v", held.Value, asks)
	}
	// The request was consumed: the next stale refresh is a delta alone.
	f.store.Invalidate(facts.Zones)
	refreshEntitySections(context.Background(), native, f, identity, scope, 5001)
	if asks := native.since[facts.Zones]; len(asks) != 3 || asks[2] != 5000 {
		t.Fatalf("since=%v", asks)
	}
}

func TestEntitySectionsAsOf(t *testing.T) {
	store := facts.NewStore()
	scope := facts.Scope{Load: "load", Generation: 1}
	if got := entitySectionsAsOf(store, nil); len(got) != 0 {
		t.Fatal(got)
	}
	facts.Put(store, scope, facts.Zones, facts.Held[EntitySection[*o.ZoneState]]{AsOf: 70, Complete: true, Source: "x"})
	facts.Put(store, scope, facts.Colony, facts.Held[string]{AsOf: 80, Complete: true, Source: "x"})
	got := entitySectionsAsOf(store, map[facts.Section]int64{facts.Colony: 90})
	if len(got) != 2 || got[facts.Zones] != 70 || got[facts.Colony] != 90 {
		t.Fatal(got)
	}
}
