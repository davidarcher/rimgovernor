package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"reflect"
	"testing"
)

type zonesFake struct {
	tick int64
	asks []int64
}

type policyEntityFake struct {
	*entityFake
	*zonesFake
}

func TestEntityRefreshPreservesPolicyZoneSection(t *testing.T) {
	f := newClockFacts(nil, nil)
	scope := facts.Scope{Load: "a", Generation: 1}
	native := &policyEntityFake{entityFake: &entityFake{tick: 100}, zonesFake: &zonesFake{tick: 100}}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("a"), MapId: proto.Int32(0)}
	p := &zoneRefresher{native: native, store: f.store, scope: scope, tick: 100, refreshes: &f.zoneRefreshes}
	if _, err := p.Zones(context.Background(), id); err != nil {
		t.Fatal(err)
	}
	refreshEntitySections(context.Background(), native, f, id, scope, 100)
	if len(native.entityFake.since[facts.Zones]) != 0 || len(native.zonesFake.asks) != 1 {
		t.Fatal("zone census read twice")
	}
	if held, ok := facts.Get[bridge.ZonesRead](f.store, facts.Zones); !ok || len(held.Value.Rows) != 1 {
		t.Fatal("policy zone census overwritten")
	}
}

func (f *zonesFake) ReadZoneSection(_ context.Context, id *c.Identity, since int64) (bridge.ZonesRead, bridge.Result, error) {
	f.asks = append(f.asks, since)
	return bridge.ZonesRead{Context: &c.ObservationContext{Identity: proto.Clone(id).(*c.Identity), Tick: proto.Int64(f.tick), NativeGeneration: proto.Uint64(1)}, AsOf: f.tick, Delta: since > 0, Rows: []*o.ZoneState{{Id: proto.String("z"), FoodStorage: proto.Bool(true)}}}, bridge.Result{}, nil
}
func TestZonesRefreshInvalidationResyncAndScope(t *testing.T) {
	ctx := context.Background()
	f := &zonesFake{tick: 100}
	store := facts.NewStore()
	refreshes := 0
	p := &zoneRefresher{native: f, store: store, scope: facts.Scope{Load: "a", Generation: 1}, tick: 100, refreshes: &refreshes}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("a"), MapId: proto.Int32(0)}
	if _, err := p.Zones(ctx, id); err != nil {
		t.Fatal(err)
	}
	if _, err := p.Zones(ctx, id); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(f.asks, []int64{0}) {
		t.Fatal("fresh section reread", f.asks)
	}
	for i := 0; i < 8; i++ {
		store.InvalidateFamily(bridge.FactColony)
		if held, ok := facts.Get[bridge.ZonesRead](store, facts.Zones); !ok || !held.Stale.All {
			t.Fatal("invalidation dropped zone baseline")
		}
		p.tick++
		f.tick = p.tick
		if _, err := p.Zones(ctx, id); err != nil {
			t.Fatal(err)
		}
	}
	if len(f.asks) != 10 || f.asks[1] != 100 || f.asks[8] != 107 || f.asks[9] != 0 {
		t.Fatal("delta/backstop cadence", f.asks)
	}
	held, _ := facts.Get[bridge.ZonesRead](store, facts.Zones)
	if held.AsOf != 108 || held.Stale.Any() {
		t.Fatal(held)
	}
	p.scope.Load = "b"
	id.LoadToken = proto.String("b")
	if _, err := p.Zones(ctx, id); err != nil {
		t.Fatal(err)
	}
	if f.asks[len(f.asks)-1] != 0 {
		t.Fatal("scope change reused delta", f.asks)
	}
}
