package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func roomQualityWire() *o.UpkeepFacts {
	v := sleepingWire()
	v.People[0].Partners = NewRefs([]string{"lover"})
	v.People[0].BedSharingAllowed = proto.Bool(true)
	v.People[0].RoyalTitle = proto.String("Knight")
	v.People[0].Precepts = []string{"Bedroom_Ascetic"}
	v.Beds[0].Room = &commonpb.Ref{Id: proto.String("7")}
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
		"duplicate partner": func(v *o.UpkeepFacts) { v.People[0].Partners = NewRefs([]string{"lover", "lover"}) },
		"blank title":       func(v *o.UpkeepFacts) { v.People[0].RoyalTitle = proto.String("") },
		"blank precept":     func(v *o.UpkeepFacts) { v.People[0].Precepts = []string{""} },
		"blank bed room":    func(v *o.UpkeepFacts) { v.Beds[0].Room = &commonpb.Ref{Id: proto.String("")} },
	} {
		v := roomQualityWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("accepted", name)
		}
	}
}
