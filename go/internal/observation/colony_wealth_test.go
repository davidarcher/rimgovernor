package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestColonyWealthReachesRoutineFacts(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ColonyFactsReply{}
	if err := protojson.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	expected := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	wealth := &o.ThreatFacts{WealthItems: proto.Float64(30000), WealthBuildings: proto.Float64(10000), WealthPawns: proto.Float64(8000), WealthTotal: proto.Float64(48000)}
	wealth.Completeness = &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	reply.GetObserved().Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Observed{Observed: wealth}}
	projection, err := DecodeColony(reply, expected)
	if err != nil {
		t.Fatal(err)
	}
	if got, known := projection.Facts.Wealth.Value(); !known || got != (policy.WealthFacts{Items: 30000, Buildings: 10000, Pawns: 8000, Total: 48000}) {
		t.Fatalf("wealth = %v", projection.Facts.Wealth)
	}
	for _, field := range []**float64{&wealth.WealthItems, &wealth.WealthBuildings, &wealth.WealthPawns, &wealth.WealthTotal} {
		saved := *field
		*field = nil
		projection, err = DecodeColony(reply, expected)
		if err != nil {
			t.Fatal(err)
		}
		if _, known := projection.Facts.Wealth.Value(); known {
			t.Fatal("partial wealth became known")
		}
		*field = saved
	}
	reply.GetObserved().Threat = nil
	projection, err = DecodeColony(reply, expected)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := projection.Facts.Wealth.Value(); known {
		t.Fatal("absent wealth became known")
	}
}
