package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type zonesFake struct {
	tick  int64
	reads int
}

type policyEntityFake struct {
	*entityFake
	*zonesFake
}

func TestEntityRefreshPreservesPolicyZoneSection(t *testing.T) {
	f := newClockFacts(nil)
	scope := facts.Scope{Load: "a", Generation: 1}
	native := &policyEntityFake{entityFake: &entityFake{tick: 100}, zonesFake: &zonesFake{tick: 100}}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("a"), MapId: proto.Int32(0)}
	p := &zoneRefresher{native: native, store: f.store, scope: scope, tick: 100}
	if _, err := p.Zones(context.Background(), id); err != nil {
		t.Fatal(err)
	}
	refreshEntitySections(context.Background(), native, f, id, scope)
	if native.zonesFake.reads != 1 {
		t.Fatal("zone census read twice")
	}
	if held, ok := facts.Get[bridge.ZonesRead](f.store, facts.Zones); !ok || len(held.Value.Rows) != 1 {
		t.Fatal("policy zone census overwritten")
	}
}

func (f *zonesFake) ReadZoneSection(_ context.Context, id *c.Identity) (bridge.ZonesRead, bridge.Result, error) {
	f.reads++
	return bridge.ZonesRead{Context: &c.ObservationContext{Identity: proto.Clone(id).(*c.Identity), Tick: proto.Int64(f.tick), NativeGeneration: proto.Uint64(1)}, AsOf: f.tick, Rows: []*o.ZoneState{{Id: proto.String("z"), FoodStorage: proto.Bool(true)}}}, bridge.Result{}, nil
}

// A fresh census is served held; every invalidation rereads it whole; a
// scope change rereads it.
func TestZonesRefreshInvalidationAndScope(t *testing.T) {
	ctx := context.Background()
	f := &zonesFake{tick: 100}
	store := facts.NewStore()
	p := &zoneRefresher{native: f, store: store, scope: facts.Scope{Load: "a", Generation: 1}, tick: 100}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("a"), MapId: proto.Int32(0)}
	for i := 0; i < 2; i++ {
		if _, err := p.Zones(ctx, id); err != nil {
			t.Fatal(err)
		}
	}
	if f.reads != 1 {
		t.Fatal("fresh section reread", f.reads)
	}
	for i := 0; i < 3; i++ {
		store.InvalidateFamily(bridge.FactColony)
		p.tick++
		f.tick = p.tick
		if _, err := p.Zones(ctx, id); err != nil {
			t.Fatal(err)
		}
	}
	held, _ := facts.Get[bridge.ZonesRead](store, facts.Zones)
	if f.reads != 4 || held.AsOf != 103 {
		t.Fatal(f.reads, held)
	}
	p.scope.Load = "b"
	id.LoadToken = proto.String("b")
	if _, err := p.Zones(ctx, id); err != nil {
		t.Fatal(err)
	}
	if f.reads != 5 {
		t.Fatal("scope change served held census", f.reads)
	}
}
