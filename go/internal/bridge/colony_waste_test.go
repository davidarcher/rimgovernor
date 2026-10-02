package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestColonyWasteCorpseOfIsOneOfThreeClasses(t *testing.T) {
	reply := func(of c.CorpseClass) *o.WasteReply {
		return &o.WasteReply{Outcome: &o.WasteReply_Observed{Observed: &o.WasteSnapshot{
			Items: []*o.WasteItem{{
				Thing:       &o.EntityRef{Id: proto.String("Corpse_1"), DefName: proto.String("Corpse_Human"), MapId: proto.Int32(0), Position: &c.Cell{X: proto.Int32(5), Z: proto.Int32(6)}},
				Kind:        o.WasteKind_WASTE_KIND_CORPSE.Enum(),
				State:       o.WasteLocation_WASTE_LOCATION_EXPOSED.Enum(),
				CorpseClass: of.Enum(),
			}},
		}}}
	}
	size := &o.MapSize{Width: proto.Uint32(250), Height: proto.Uint32(250)}
	for _, of := range []c.CorpseClass{c.CorpseClass_CORPSE_CLASS_COLONIST, c.CorpseClass_CORPSE_CLASS_STRANGER, c.CorpseClass_CORPSE_CLASS_ANIMAL} {
		if err := validateColonyWaste(reply(of), size, 0); err != nil {
			t.Errorf("%s: %v", of, err)
		}
	}
	if err := validateColonyWaste(reply(c.CorpseClass_CORPSE_CLASS_UNSPECIFIED), size, 0); err == nil {
		t.Error("an unspecified corpse class passed")
	}
}
