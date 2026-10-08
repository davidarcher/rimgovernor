package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestMonumentProjectionUsesCardinalRotations(t *testing.T) {
	for i, want := range []domain.Rotation{domain.North, domain.East, domain.South, domain.West} {
		m := &o.QuestMonument{Pieces: []*o.QuestMonumentPiece{{DefName: proto.String("Wall"), Offset: &c.Cell{}, Rotation: proto.Int32(int32(i))}}}
		row, known := questMonument(m).Value()
		if !known || row.Pieces[0].Rotation != want {
			t.Fatalf("native rotation %d: %+v", i, row)
		}
		if _, err := domain.NewBuilding(row.Pieces[0].Def, domain.Cell{X: 1, Z: 1}, row.Pieces[0].Rotation, ""); err != nil {
			t.Fatal(err)
		}
	}
}
