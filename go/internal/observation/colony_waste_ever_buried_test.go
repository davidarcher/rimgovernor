package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The waste census carries the corpse's everBuriedInSarcophagus flag (#2342).
func TestColonyWasteCarriesEverBuriedInSarcophagus(t *testing.T) {
	head := func(id string) *o.EntityRef {
		return &o.EntityRef{Id: proto.String(id), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}}
	}
	rows := []*o.WasteItem{
		{Thing: &c.Ref{Id: proto.String("Corpse_1")}, CorpseClass: c.CorpseClass_CORPSE_CLASS_STRANGER.Enum(), EverBuriedInSarcophagus: proto.Bool(true)},
		{Thing: &c.Ref{Id: proto.String("Corpse_2")}, CorpseClass: c.CorpseClass_CORPSE_CLASS_STRANGER.Enum()},
	}
	tables := bridge.Tables{Things: bridge.NewThings(&o.Thing{Thing: head("Corpse_1")}, &o.Thing{Thing: head("Corpse_2")})}
	facts := &o.ColonyFactsSnapshot{Waste: &o.WasteReply{Outcome: &o.WasteReply_Observed{Observed: &o.WasteSnapshot{Items: rows}}}}
	items, known := colonyWaste(facts, tables).Value()
	if !known || len(items) != 2 || !items[0].EverBuriedInSarcophagus || items[1].EverBuriedInSarcophagus {
		t.Fatalf("census: %+v %v", items, known)
	}
}
