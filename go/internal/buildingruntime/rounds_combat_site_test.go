package buildingruntime

import (
	"context"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

type siteCombatKeySource struct {
	RoundsDefenseSource
	fake *combatOrdersFake
}

func (s siteCombatKeySource) CombatOrders(ctx context.Context, id *c.Identity, key string, command *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	return s.fake.CombatOrders(ctx, id, key, command)
}

type siteCombatSource struct {
	siteCombatKeySource
	identity *c.Identity
}

func (s *siteCombatSource) CombatOrders(ctx context.Context, id *c.Identity, key string, command *op.CombatOrders) ([]bridge.CombatOrderResult, error) {
	s.identity = proto.Clone(id).(*c.Identity)
	return s.siteCombatKeySource.CombatOrders(ctx, id, key, command)
}

func (s *siteCombatSource) CombatGeometry(ctx context.Context, request *mp.CombatGeometryRequest) (*mp.CombatGeometry, bridge.Result, error) {
	return s.fake.CombatGeometry(ctx, request)
}

// A selected destination map uses the same recorded combat frame, decision,
// geometry and order path as home defense. Entry and control reacquisition are
// separate expedition work; this test begins with acquired site scope.
func TestSelectedSiteMapUsesSharedCombat(t *testing.T) {
	stops, err := replayCombat("testdata/combat/lab-choke.json.gz")
	if err != nil || len(stops) == 0 {
		t.Fatalf("recording: %v", err)
	}
	var s combatReplayStop
	for _, stop := range stops {
		if len(stop.Orders) > 0 {
			s = stop
			break
		}
	}
	if s.Frame == nil {
		t.Fatal("recording has no changed combat orders")
	}
	frame := proto.Clone(s.Frame).(*o.BundleSnapshot)
	const siteMap = 77
	var rebase func(protoreflect.Message)
	rebase = func(m protoreflect.Message) {
		m.Range(func(fd protoreflect.FieldDescriptor, value protoreflect.Value) bool {
			if fd.Name() == "map_id" {
				m.Set(fd, protoreflect.ValueOfInt32(siteMap))
			} else if fd.Kind() == protoreflect.MessageKind {
				if fd.IsList() {
					for i := 0; i < value.List().Len(); i++ {
						rebase(value.List().Get(i).Message())
					}
				} else if !fd.IsMap() {
					rebase(value.Message())
				}
			}
			return true
		})
	}
	rebase(frame.ProtoReflect())
	decide := func(f *o.BundleSnapshot) ([]policy.CombatOrder, *policy.GeometryRequest) {
		t.Helper()
		combat, err := decodeCombatWithCatalog(f)
		if err != nil {
			t.Fatal(err)
		}
		in, reason, err := combatFrameInputs(combat, nil)
		if err != nil || !reason.IsZero() {
			t.Fatalf("frame inputs: %v %v", reason, err)
		}
		var layout domain.Fact[policy.CombatLayout]
		if s.Layout != nil {
			layout = domain.Known(*s.Layout)
		}
		view := combatView(combat, in, s.Orderable, layout)
		orders, ask, _ := policy.DecideCombat(view, s.Reply, s.Stop, s.MemoryIn)
		return orders, ask
	}
	want, wantAsk := decide(s.Frame)
	orders, ask := decide(frame)
	if len(orders) == 0 || !reflect.DeepEqual(orders, want) || !reflect.DeepEqual(ask, wantAsk) {
		t.Fatalf("site combat differs: orders=%+v home=%+v", orders, want)
	}
	native := &siteCombatSource{siteCombatKeySource: siteCombatKeySource{fake: &combatOrdersFake{}}}
	planner := &RoundsDefensePlanner{native: native}
	id := frame.GetContext().GetIdentity()
	if s.Ask != nil {
		planner.answerGeometry(context.Background(), id, s.Ask)
		if len(native.fake.asks) != 1 || native.fake.asks[0].GetIdentity().GetMapId() != siteMap {
			t.Fatalf("geometry escaped site scope: %v", native.fake.asks)
		}
	}
	state := ControlState{Snapshot: domain.GenerationSnapshot{Colony: domain.ColonyID(id.GetColonyId()), Load: domain.LoadID(id.GetLoadToken()), Map: siteMap}}
	plan, err := combatBatchPlan("fight", "site-fight", nil, orders)
	if err != nil {
		t.Fatal(err)
	}
	wire, err := bridge.IntentAction("site-key", plan.Actions()[0])
	if err != nil {
		t.Fatal(err)
	}
	results, err := native.CombatOrders(context.Background(), boundary.Identity(state.Snapshot), wire.GetKey(), wire.GetCombatOrders())
	if err != nil || len(results) != len(orders) || native.identity.GetMapId() != siteMap {
		t.Fatalf("site batch: identity=%v results=%v err=%v", native.identity, results, err)
	}
	selected := map[domain.PawnID]bool{}
	for _, pawn := range s.Orderable {
		selected[pawn] = true
	}
	for _, order := range native.fake.batches[0].Orders {
		if order.Pawn != nil && !selected[domain.PawnID(order.GetPawn().GetEntityId())] {
			t.Fatalf("order escaped selected site squad: %v", order)
		}
	}
}
