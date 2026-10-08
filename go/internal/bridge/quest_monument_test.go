package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestMonumentBoundaryRejectsPartialTargets(t *testing.T) {
	m := &o.QuestMonument{MarkerId: proto.String("marker"), DefName: proto.String("MonumentMarker"), MapId: proto.Int32(0), Packed: proto.Bool(true), Installed: proto.Bool(false), Pieces: []*o.QuestMonumentPiece{{DefName: proto.String("Wall"), Offset: &c.Cell{X: proto.Int32(0), Z: proto.Int32(0)}, Rotation: proto.Int32(0), Footprint: []*c.Cell{{X: proto.Int32(0), Z: proto.Int32(0)}}}}}
	if validatedQuestMonument(m) == nil {
		t.Fatal("complete marker rejected")
	}
	copy := validatedQuestMonument(m)
	copy.Pieces[0].DefName = proto.String("Changed")
	if m.Pieces[0].GetDefName() != "Wall" {
		t.Fatal("boundary aliases native input")
	}
	m.Pieces[0].Footprint = nil
	if validatedQuestMonument(m) != nil {
		t.Fatal("missing footprint admitted")
	}
}
