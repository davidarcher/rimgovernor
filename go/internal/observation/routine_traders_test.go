package observation

import (
	"context"
	"os"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

type traderSource struct {
	*projectSource
	read bridge.TradersRead
}

func (s *traderSource) ListTraders(context.Context, *c.Identity) (bridge.TradersRead, bridge.Result, error) {
	return s.read, bridge.Result{}, nil
}

// The routine trader facts carry native's arrival verdict: a caravan still
// walking in is neither tradeable nor absent (#234).
func TestRoutineTraderFactsCarryTravelling(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	base := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, base); err != nil {
		t.Fatal(err)
	}
	expected, err := DecodeIdentity(&l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Paused: proto.Bool(true)}}})
	if err != nil {
		t.Fatal(err)
	}
	read := bridge.TradersRead{Context: proto.Clone(base.GetObserved().Context).(*c.ObservationContext), Traders: []bridge.TraderRead{
		{ID: "Thing_Human1", Kind: "Caravan_Outlander_BulkGoods", Faction: "Faction_0", Travelling: true, GoodsStacks: 36},
		{ID: "Thing_Human2", Kind: "Caravan_Neolithic", Faction: "Faction_1", CanTrade: true, GoodsStacks: 4},
	}}
	source := &traderSource{projectSource: &projectSource{colonySource: &colonySource{reply: base}}, read: read}
	out, err := ObserveRoutine(context.Background(), source, testkit.NewManualClock(time.Now()), expected, time.Second)
	if err != nil {
		t.Fatal(err)
	}
	want := domain.Known([]policy.TraderFacts{
		{ID: "Thing_Human1", Kind: "Caravan_Outlander_BulkGoods", Faction: "Faction_0", Travelling: true, GoodsStacks: 36},
		{ID: "Thing_Human2", Kind: "Caravan_Neolithic", Faction: "Faction_1", CanTrade: true, GoodsStacks: 4},
	})
	if !reflect.DeepEqual(out.Projection.Facts.Traders, want) {
		t.Fatal(out.Projection.Facts.Traders)
	}
}
