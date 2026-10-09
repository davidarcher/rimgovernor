package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// The waste census carries each row's rot stage to policy.WasteItem.
func TestColonyWasteCarriesRotStage(t *testing.T) {
	head := func(id string) *o.EntityRef {
		return &o.EntityRef{Id: proto.String(id), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}}
	}
	rows := []*o.WasteItem{
		{Thing: &c.Ref{Id: proto.String("Corpse_1")}, CorpseClass: c.CorpseClass_CORPSE_CLASS_ANIMAL.Enum(), RotStage: c.RotStage_ROT_STAGE_ROTTING.Enum()},
		{Thing: &c.Ref{Id: proto.String("Corpse_2")}, CorpseClass: c.CorpseClass_CORPSE_CLASS_ANIMAL.Enum(), RotStage: c.RotStage_ROT_STAGE_FRESH.Enum()},
		{Thing: &c.Ref{Id: proto.String("Corpse_3")}, CorpseClass: c.CorpseClass_CORPSE_CLASS_ANIMAL.Enum(), RotStage: c.RotStage_ROT_STAGE_DESSICATED.Enum()},
		{Thing: &c.Ref{Id: proto.String("Junk_1")}},
	}
	tables := bridge.Tables{Things: bridge.NewThings(
		&o.Thing{Thing: head("Corpse_1")}, &o.Thing{Thing: head("Corpse_2")}, &o.Thing{Thing: head("Corpse_3")}, &o.Thing{Thing: head("Junk_1")})}
	facts := &o.ColonyFactsSnapshot{Waste: &o.WasteReply{Outcome: &o.WasteReply_Observed{Observed: &o.WasteSnapshot{Items: rows}}}}
	items, known := colonyWaste(facts, tables).Value()
	if !known || len(items) != 4 {
		t.Fatalf("census: %v %v", items, known)
	}
	for i, want := range []domain.RotStage{domain.RotRotting, domain.RotFresh, domain.RotDessicated, ""} {
		if items[i].RotStage != want {
			t.Errorf("%s: rot %q, want %q", items[i].ID, items[i].RotStage, want)
		}
	}
}
