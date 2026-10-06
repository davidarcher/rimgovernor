package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestAcquisitionCensusBindsSourceSnapshotAndYield(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.Acquisition = []*o.AcquisitionFacts{{Source: NewRef("plant"), SourceSnapshot: &o.SnapshotRef{EntityId: proto.String("plant"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0), Taken: proto.Bool(false)}}
	base.Issues = nil
	withHuntCensus(base)
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
	plantation := proto.Clone(base).(*o.ColonyFactsSnapshot)
	plantation.Acquisition[0].Plantation, plantation.Acquisition[0].Growth = proto.Bool(true), proto.Float64(0.5)
	if err := validateColonyAcquisition(plantation); err != nil {
		t.Fatal(err)
	}
	designated := proto.Clone(base).(*o.ColonyFactsSnapshot)
	designated.Acquisition[0].Designated, designated.Acquisition[0].DesignatedTick = proto.Bool(true), proto.Int64(designated.Context.GetTick())
	if err := validateColonyAcquisition(designated); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*o.ColonyFactsSnapshot){
		// #1043: a designated row without its first-seen tick, a tick on an
		// undesignated row, a tick past the read, and a missing taken.
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Designated = proto.Bool(true) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].DesignatedTick = proto.Int64(0) },
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Designated, v.Acquisition[0].DesignatedTick = proto.Bool(true), proto.Int64(v.Context.GetTick()+1)
		},
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Taken = nil },
		// #2292: a plantation row needs a tree and an in-range growth; growth is bounded on every row.
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Plantation = proto.Bool(true) },
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Plantation, v.Acquisition[0].Growth = proto.Bool(true), proto.Float64(1.5)
		},
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Growth = proto.Float64(-0.1) },
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Tree, v.Acquisition[0].Plantation, v.Acquisition[0].Growth = proto.Bool(false), proto.Bool(true), proto.Float64(0.5)
		},
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].SourceSnapshot.Context.Tick = proto.Int64(v.Context.GetTick() + 1)
		},
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].SourceSnapshot.EntityId = proto.String("other") },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Yield = proto.Float64(math.NaN()) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Food = nil },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition = append(v.Acquisition, v.Acquisition[0]) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].NutritionYield = proto.Float64(1) },
		func(v *o.ColonyFactsSnapshot) { v.Issues = []*o.ReadIssue{{Field: proto.String("acquisition")}} },
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
	base.Acquisition = []*o.AcquisitionFacts{{Taken: proto.Bool(false), Source: NewRef("beaver"), SourceSnapshot: &o.SnapshotRef{EntityId: proto.String("beaver"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}, RevengeChance: proto.Float64(0.1), HerdSize: proto.Uint32(3), MeleeOnly: proto.Bool(false), Downed: proto.Bool(false), Resource: proto.String("Corpse_Alphabeaver"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(0)}}
	base.Issues = nil
	withHuntCensus(base)
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
}

func TestHuntCostsRequireCompleteBoundedFacts(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.Acquisition = []*o.AcquisitionFacts{{Taken: proto.Bool(false), Source: NewRef("deer"), SourceSnapshot: &o.SnapshotRef{EntityId: proto.String("deer"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}, Resource: proto.String("Corpse_Deer"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(true), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(10), RevengeChance: proto.Float64(0), HerdSize: proto.Uint32(1), MeleeOnly: proto.Bool(true), Downed: proto.Bool(false)}}
	base.Issues = nil
	withHuntCensus(base)
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.AcquisitionFacts){
		func(r *o.AcquisitionFacts) { r.RevengeChance = nil },
		func(r *o.AcquisitionFacts) { r.RevengeChance = proto.Float64(math.NaN()) },
		func(r *o.AcquisitionFacts) { r.RevengeChance = proto.Float64(1.1) },
		func(r *o.AcquisitionFacts) { r.HerdSize = proto.Uint32(0) },
		func(r *o.AcquisitionFacts) { r.MeleeOnly = nil },
		func(r *o.AcquisitionFacts) { r.Downed = nil },
		func(r *o.AcquisitionFacts) { r.Fogged = nil },
	} {
		v := proto.Clone(base).(*o.ColonyFactsSnapshot)
		mutate(v.Acquisition[0])
		if validateColonyAcquisition(v) == nil {
			t.Fatal("accepted invalid hunt cost", v.Acquisition[0])
		}
	}
}

// withHuntCensus gives every hunt row its raw prey flags and the snapshot an empty hunt census.
func withHuntCensus(v *o.ColonyFactsSnapshot) {
	v.HuntCensus = &o.HuntCensus{}
	for _, row := range v.Acquisition {
		row.Fogged, row.InMentalState = proto.Bool(false), proto.Bool(false)
	}
}
