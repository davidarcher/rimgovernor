package bridge

import (
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"math"
	"testing"
)

func TestAcquisitionCensusBindsSourceSnapshotAndYield(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.Acquisition = []*o.AcquisitionFacts{{Source: &o.EntityRef{Id: proto.String("plant"), DefName: proto.String("Oak"), MapId: base.Context.Identity.MapId, Position: proto.Clone(base.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("plant"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)}}
	base.Issues = nil
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*o.ColonyFactsSnapshot){
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Source.Snapshot.Context.Tick = proto.Int64(v.Context.GetTick() + 1)
		},
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Source.Snapshot.EntityId = proto.String("other") },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Yield = proto.Float64(math.NaN()) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Food = nil },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition = append(v.Acquisition, v.Acquisition[0]) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].NutritionYield = proto.Float64(1) },
		func(v *o.ColonyFactsSnapshot) { v.Issues = []*o.ReadIssue{{Field: proto.String("acquisition")}} },
		// An inedible hunt of anything but a recognised pest.
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Hunt, v.Acquisition[0].Tree, v.Acquisition[0].Yield = proto.Bool(true), proto.Bool(false), proto.Float64(1)
			v.Acquisition[0].Source.DefName = proto.String("Muffalo")
		},
	} {
		v := proto.Clone(base).(*o.ColonyFactsSnapshot)
		change(v)
		if validateColonyAcquisition(v) == nil {
			t.Fatal("malformed acquisition accepted", v)
		}
	}
}

func TestAcquisitionCensusAcceptsAnInediblePestHunt(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.Acquisition = []*o.AcquisitionFacts{{Source: &o.EntityRef{Id: proto.String("beaver"), DefName: proto.String("Alphabeaver"), MapId: base.Context.Identity.MapId, Position: proto.Clone(base.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("beaver"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}}, Resource: proto.String("Corpse_Alphabeaver"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(0)}}
	base.Issues = nil
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
}
