package bridge

import (
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func roomQualityWire() *o.UpkeepFacts {
	v := sleepingWire()
	v.People[0].PartnerIds = []string{"lover"}
	v.People[0].BedSharingAllowed = proto.Bool(true)
	v.People[0].Title = &o.RoyalTitleFacts{DefName: proto.String("Knight"), Seniority: proto.Int32(100), BedroomMinArea: proto.Int32(24), BedroomMinImpressiveness: proto.Int32(40), BedroomFloored: proto.Bool(true), BedroomThings: []*o.BedroomThingRequirement{{AnyOf: []string{"DoubleBed", "RoyalBed"}, Count: proto.Int32(1)}}}
	v.Beds[0].RoomId = proto.String("7")
	v.Beds[0].Quality = proto.String("Good")
	return v
}

func TestRoomQualityBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(roomQualityWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	// The colony facts projection carries the upkeep section too.
	if err := validateColonyUpkeep(roomQualityWire(), size); err != nil && err.Error() == contract("unsupported upkeep projection").Error() {
		t.Fatal(err)
	}
	for name, mutate := range map[string]func(*o.UpkeepFacts){
		"duplicate partner": func(v *o.UpkeepFacts) { v.People[0].PartnerIds = []string{"lover", "lover"} },
		"blank title":       func(v *o.UpkeepFacts) { v.People[0].Title.DefName = nil },
		"negative area":     func(v *o.UpkeepFacts) { v.People[0].Title.BedroomMinArea = proto.Int32(-1) },
		"empty any-of":      func(v *o.UpkeepFacts) { v.People[0].Title.BedroomThings[0].AnyOf = nil },
		"zero count":        func(v *o.UpkeepFacts) { v.People[0].Title.BedroomThings[0].Count = proto.Int32(0) },
		"blank bed room":    func(v *o.UpkeepFacts) { v.Beds[0].RoomId = proto.String("") },
	} {
		v := roomQualityWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("accepted", name)
		}
	}
}
