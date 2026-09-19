package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestColonyBlightCensusIsKnownOnlyWithoutIssue(t *testing.T) {
	t.Parallel()
	v := &o.ColonyFactsSnapshot{}
	if plants, known := colonyBlight(v).Value(); !known || len(plants) != 0 {
		t.Fatal("an empty census without an issue is the settled state")
	}
	v.BlightedPlants = []*o.BlightedPlant{{Plant: &o.EntityRef{Id: proto.String("Plant_Rice1"), DefName: proto.String("Plant_Rice"),
		Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}, Snapshot: &o.SnapshotRef{EntityId: proto.String("Plant_Rice1"), Token: proto.String("cut-a")}},
		Designated: proto.Bool(true), ZoneId: proto.String("7")}}
	plants, known := colonyBlight(v).Value()
	if !known || len(plants) != 1 || plants[0].ID != "Plant_Rice1" || plants[0].Token != "cut-a" || !plants[0].Designated || plants[0].Zone != "7" || plants[0].Cell != (domain.Cell{X: 3, Z: 4}) {
		t.Fatal(plants)
	}
	v.Issues = []*o.ReadIssue{{Field: proto.String("blighted_plants")}}
	if _, known := colonyBlight(v).Value(); known {
		t.Fatal("a blighted_plants issue withholds the census")
	}
}
