package bridge

import (
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestBlightCensusBindsPlantSnapshot(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.BlightedPlants = []*o.BlightedPlant{{Plant: &o.EntityRef{Id: proto.String("Plant_Rice1"), DefName: proto.String("Plant_Rice"), MapId: base.Context.Identity.MapId, Position: proto.Clone(base.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("Plant_Rice1"), Token: proto.String("cut-a"), Context: proto.Clone(base.Context).(*c.ObservationContext)}}, Designated: proto.Bool(false), ZoneId: proto.String("7"), Growth: proto.Float64(0.5)}}
	base.Issues = nil
	if err := validateColonyBlight(base); err != nil {
		t.Fatal(err)
	}
	if err := ValidateColonyFacts(base, base.Context.Identity); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*o.ColonyFactsSnapshot){
		func(v *o.ColonyFactsSnapshot) {
			v.BlightedPlants[0].Plant.Snapshot.Context.Tick = proto.Int64(v.Context.GetTick() + 1)
		},
		func(v *o.ColonyFactsSnapshot) { v.BlightedPlants[0].Plant.Snapshot.EntityId = proto.String("other") },
		func(v *o.ColonyFactsSnapshot) { v.BlightedPlants[0].Plant.Snapshot = nil },
		func(v *o.ColonyFactsSnapshot) { v.BlightedPlants[0].Designated = nil },
		func(v *o.ColonyFactsSnapshot) { v.BlightedPlants[0].Growth = proto.Float64(2) },
		func(v *o.ColonyFactsSnapshot) { v.BlightedPlants = append(v.BlightedPlants, v.BlightedPlants[0]) },
		func(v *o.ColonyFactsSnapshot) { v.Issues = []*o.ReadIssue{{Field: proto.String("blighted_plants")}} },
	} {
		v := proto.Clone(base).(*o.ColonyFactsSnapshot)
		change(v)
		if validateColonyBlight(v) == nil {
			t.Fatal("malformed blight census accepted", v)
		}
	}
}
