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

// entityFake answers the three entity reads whole and counts reads per
// section.
type entityFake struct {
	tick  int64
	reads map[facts.Section]int
	full  map[string]*o.ZoneState
	err   error
}

func (f *entityFake) ask(section facts.Section) {
	if f.reads == nil {
		f.reads = map[facts.Section]int{}
	}
	f.reads[section]++
}

func (f *entityFake) ReadZones(context.Context, *c.Identity) (bridge.EntityRows[*o.ZoneState], bridge.Result, error) {
	f.ask(facts.Zones)
	if f.err != nil {
		return bridge.EntityRows[*o.ZoneState]{}, bridge.Result{}, f.err
	}
	return bridge.EntityRows[*o.ZoneState]{Context: &c.ObservationContext{Tick: proto.Int64(f.tick)}, Rows: f.full}, bridge.Result{}, nil
}

func (f *entityFake) ReadBuildings(context.Context, *c.Identity) (bridge.EntityRows[*o.BuildingState], bridge.Result, error) {
	f.ask(facts.Buildings)
	return bridge.EntityRows[*o.BuildingState]{Context: &c.ObservationContext{Tick: proto.Int64(f.tick)}, Rows: map[string]*o.BuildingState{}}, bridge.Result{}, nil
}

func (f *entityFake) ReadBillStacks(context.Context, *c.Identity) (bridge.EntityRows[*o.BillStack], bridge.Result, error) {
	f.ask(facts.Bills)
	return bridge.EntityRows[*o.BillStack]{Context: &c.ObservationContext{Tick: proto.Int64(f.tick)}, Rows: map[string]*o.BillStack{}}, bridge.Result{}, nil
}

func zoneState(id, label string) *o.ZoneState {
	return &o.ZoneState{Id: proto.String(id), Label: proto.String(label)}
}

// Every refresh reads each section whole and replaces it; a failed read
// keeps the held section; a new scope is filed under that scope.
func TestRefreshEntitySectionsWholeReads(t *testing.T) {
	f := newClockFacts(nil)
	scope := facts.Scope{Load: "load", Generation: 1}
	identity := &c.Identity{ColonyId: proto.String("colony"), LoadToken: proto.String("load"), MapId: proto.Int32(0)}
	native := &entityFake{tick: 100, full: map[string]*o.ZoneState{"Zone_1": zoneState("Zone_1", "a"), "Zone_2": zoneState("Zone_2", "b")}}
	refreshEntitySections(context.Background(), native, f, identity, scope)
	held, ok := facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	if !ok || len(held.Value) != 2 || held.AsOf != 100 || held.Source != "rimgovernor/observations_list_zones" || native.reads[facts.Buildings] != 1 || native.reads[facts.Bills] != 1 {
		t.Fatalf("%+v ok=%v reads=%v", held, ok, native.reads)
	}
	native.tick = 200
	native.full = map[string]*o.ZoneState{"Zone_3": zoneState("Zone_3", "c")}
	refreshEntitySections(context.Background(), native, f, identity, scope)
	held, _ = facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones)
	if native.reads[facts.Zones] != 2 || held.AsOf != 200 || len(held.Value) != 1 || held.Value["Zone_3"] == nil {
		t.Fatalf("%+v reads=%v", held.Value, native.reads)
	}
	native.err = errors.New("transport")
	native.tick = 300
	refreshEntitySections(context.Background(), native, f, identity, scope)
	if held, _ = facts.Get[EntitySection[*o.ZoneState]](f.store, facts.Zones); held.AsOf != 200 || len(held.Value) != 1 {
		t.Fatalf("a failed read must keep the held section: %+v", held)
	}
	native.err = nil
	other := facts.Scope{Load: "load", Generation: 2}
	refreshEntitySections(context.Background(), native, f, identity, other)
	if f.store.Scope() != other {
		t.Fatalf("scope=%+v", f.store.Scope())
	}
}
