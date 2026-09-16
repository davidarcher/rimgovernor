package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestTemperatureProjectionUsesOnlyEligibleBedsAndPreservesUnknown(t *testing.T) {
	sleeping := policy.SleepingObservation{Beds: []policy.SleepingBed{{ID: "bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false)}}}
	rooms := &o.RoomsSnapshot{Rooms: []*o.RoomState{{Id: proto.String("room"), ProperRoom: proto.Bool(true), TemperatureC: proto.Float64(5), Beds: []*o.BuildingState{{Building: &o.EntityRef{Id: proto.String("bed")}}}, Cells: []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}, Contents: []*o.Quantity{{DefName: proto.String("Campfire"), Units: proto.Int64(1)}}}}}
	for _, mode := range []string{"observed", "medical", "prisoner", "animal", "unknown-census", "unknown-bed", "missing-temperature", "open-roof", "edge"} {
		t.Run(mode, func(t *testing.T) {
			s := sleeping
			s.Beds = append([]policy.SleepingBed{}, s.Beds...)
			r := proto.Clone(rooms).(*o.RoomsSnapshot)
			switch mode {
			case "medical":
				s.Beds[0].Medical = domain.Known(true)
			case "prisoner":
				s.Beds[0].Prisoners = domain.Known(true)
			case "animal":
				s.Beds[0].Humanlike = domain.Known(false)
			case "unknown-bed":
				s.Beds[0].Prisoners = domain.Unknown[bool]()
			case "missing-temperature":
				r.Rooms[0].TemperatureC = nil
			case "open-roof":
				r.Rooms[0].OpenRoofCount = proto.Uint32(1)
			case "edge":
				r.Rooms[0].TouchesMapEdge = proto.Bool(true)
			}
			beds := domain.Known(s)
			if mode == "unknown-census" {
				beds = domain.Unknown[policy.SleepingObservation]()
			}
			result := temperatureRooms(r, beds)
			low, high := policy.TemperatureRange(result)
			l, lk := low.Value()
			h, hk := high.Value()
			if mode == "observed" {
				if !lk || !hk || l != 5 || h != 5 {
					t.Fatal(result, low, high)
				}
			} else if lk || hk {
				t.Fatal("missing or ineligible room proved temperature", low, high)
			}
		})
	}
}

func TestRoomProjectionCarriesNativeRoleAndComfortHostingNeedsBothCensuses(t *testing.T) {
	rooms := &o.RoomsSnapshot{Rooms: []*o.RoomState{
		{Id: proto.String("dining"), Role: proto.String("DiningRoom"), ProperRoom: proto.Bool(true), Cells: []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(1)}}},
		{Id: proto.String("unroled"), ProperRoom: proto.Bool(true)}}}
	census := temperatureRooms(rooms, domain.Unknown[policy.SleepingObservation]())
	v, known := census.Value()
	if !known || len(v.Rooms) != 2 || v.Rooms[0].Role != domain.Known(policy.RoomRoleDiningRoom) || v.Rooms[1].Role != domain.Unknown[policy.RoomRole]() {
		t.Fatal(census)
	}
	people := []policy.PawnID{"a"}
	comfort := policy.ComfortObservation{People: people, Dining: []policy.ComfortFacility{{ID: "hosted", RoomID: "dining", AccessibleTo: people}, {ID: "stray", RoomID: "unroled", AccessibleTo: people}}}
	hosted, known := hostedComfort(domain.Known(comfort), census).Value()
	if !known || len(hosted.Dining) != 1 || hosted.Dining[0].ID != "hosted" {
		t.Fatal(hosted, known)
	}
	if _, known := hostedComfort(domain.Known(comfort), domain.Unknown[policy.RoomObservation]()).Value(); known {
		t.Fatal("missing room census certified comfort")
	}
	if _, known := hostedComfort(domain.Unknown[policy.ComfortObservation](), census).Value(); known {
		t.Fatal("missing comfort census certified comfort")
	}
}
