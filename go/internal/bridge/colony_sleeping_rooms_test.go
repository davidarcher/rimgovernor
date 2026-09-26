package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func roomComplete(n uint64) *o.Completeness {
	return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
}

func roomQualityWire() *o.UpkeepFacts {
	v := sleepingWire()
	v.People[0].PartnerIds = []string{"lover"}
	v.People[0].BedSharingAllowed = proto.Bool(true)
	v.People[0].Title = &o.RoyalTitleFacts{DefName: proto.String("Knight"), Seniority: proto.Int32(100), BedroomMinArea: proto.Int32(24), BedroomMinImpressiveness: proto.Int32(40), BedroomFloored: proto.Bool(true), BedroomThings: []*o.BedroomThingRequirement{{AnyOf: []string{"DoubleBed", "RoyalBed"}, Count: proto.Int32(1)}}}
	v.Beds[0].RoomId = proto.String("7")
	v.Beds[0].Quality = proto.String("Good")
	v.Rooms = &o.UpkeepRoomsSection{Outcome: &o.UpkeepRoomsSection_Observed{Observed: &o.UpkeepRoomsFacts{
		Rooms:        []*o.UpkeepRoom{{RoomId: proto.String("7"), Role: proto.String("Bedroom"), Quality: &o.RoomQuality{Impressiveness: proto.Float64(35), Wealth: proto.Float64(900), Beauty: proto.Float64(-0.5), Space: proto.Float64(20), Cleanliness: proto.Float64(-0.1)}, CellCount: proto.Uint32(16), BedIds: []string{"bed"}}},
		Completeness: roomComplete(1),
	}}}
	return v
}

func TestRoomQualityBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(roomQualityWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	// The colony facts projection carries the rooms section too.
	if err := validateColonyUpkeep(roomQualityWire(), size); err != nil && err.Error() == contract("unsupported upkeep projection").Error() {
		t.Fatal(err)
	}
	room := func(v *o.UpkeepFacts) *o.UpkeepRoom { return v.Rooms.GetObserved().Rooms[0] }
	for name, mutate := range map[string]func(*o.UpkeepFacts){
		"duplicate partner": func(v *o.UpkeepFacts) { v.People[0].PartnerIds = []string{"lover", "lover"} },
		"blank title":       func(v *o.UpkeepFacts) { v.People[0].Title.DefName = nil },
		"negative area":     func(v *o.UpkeepFacts) { v.People[0].Title.BedroomMinArea = proto.Int32(-1) },
		"empty any-of":      func(v *o.UpkeepFacts) { v.People[0].Title.BedroomThings[0].AnyOf = nil },
		"zero count":        func(v *o.UpkeepFacts) { v.People[0].Title.BedroomThings[0].Count = proto.Int32(0) },
		"blank bed room":    func(v *o.UpkeepFacts) { v.Beds[0].RoomId = proto.String("") },
		"duplicate room": func(v *o.UpkeepFacts) {
			f := v.Rooms.GetObserved()
			f.Rooms = append(f.Rooms, f.Rooms[0])
			f.Completeness = roomComplete(2)
		},
		"NaN stat":           func(v *o.UpkeepFacts) { room(v).Quality.Beauty = proto.Float64(math.NaN()) },
		"negative wealth":    func(v *o.UpkeepFacts) { room(v).Quality.Wealth = proto.Float64(-1) },
		"duplicate bed":      func(v *o.UpkeepFacts) { room(v).BedIds = []string{"bed", "bed"} },
		"incomplete census":  func(v *o.UpkeepFacts) { v.Rooms.GetObserved().Completeness = roomComplete(2) },
		"rows with an issue": func(v *o.UpkeepFacts) { v.Issues = []*o.ReadIssue{{Field: proto.String("rooms")}} },
	} {
		v := roomQualityWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("accepted", name)
		}
	}
	// Past the 256-room bound the producer reports an issue and no section.
	v := roomQualityWire()
	v.Rooms = nil
	v.Issues = []*o.ReadIssue{{Field: proto.String("rooms")}}
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("unavailable room census rejected", err)
	}
}
