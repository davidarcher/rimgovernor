package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestTemperatureProjectionUsesOnlyEligibleBedsAndPreservesUnknown(t *testing.T) {
	sleeping := policy.SleepingObservation{Beds: []policy.SleepingBed{{ID: "bed", Humanlike: domain.Known(true), Medical: domain.Known(false), Prisoners: domain.Known(false)}}}
	rooms := &o.RoomsSnapshot{Rooms: []*o.RoomState{{Id: proto.String("room"), ProperRoom: proto.Bool(true), OpenRoofCount: proto.Uint32(0), TemperatureC: proto.Float64(5), Beds: []*c.Ref{{Id: proto.String("bed")}}, Contents: []*o.Quantity{{DefName: proto.String("Campfire"), Units: proto.Int64(1)}}}}}
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
			result := temperatureRooms(r, map[string][]domain.Cell{"room": {{X: 1, Z: 2}}}, beds)
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
		{Id: proto.String("dining"), Role: proto.String("DiningRoom"), ProperRoom: proto.Bool(true)},
		{Id: proto.String("unroled"), ProperRoom: proto.Bool(true)}}}
	census := temperatureRooms(rooms, map[string][]domain.Cell{"dining": {{X: 1, Z: 1}}}, domain.Unknown[policy.SleepingObservation]())
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

func TestTemperatureRoomsDecodesCleanlinessStat(t *testing.T) {
	rooms := &o.RoomsSnapshot{Rooms: []*o.RoomState{
		{Id: proto.String("clean"), ProperRoom: proto.Bool(true), Stats: []*o.RoomStat{{DefName: proto.String("Cleanliness"), Value: proto.Float64(-2.5)}}},
		{Id: proto.String("unavailable"), ProperRoom: proto.Bool(true), Stats: []*o.RoomStat{{DefName: proto.String("Cleanliness"), Unavailable: &c.Unavailable{}}}},
		{Id: proto.String("missing"), ProperRoom: proto.Bool(true), Stats: []*o.RoomStat{{DefName: proto.String("Impressiveness"), Value: proto.Float64(1)}}},
	}}
	census, known := temperatureRooms(rooms, nil, domain.Unknown[policy.SleepingObservation]()).Value()
	if !known || len(census.Rooms) != 3 {
		t.Fatal(census, known)
	}
	if v, ok := census.Rooms[0].Cleanliness.Value(); !ok || v != -2.5 {
		t.Fatal(census.Rooms[0])
	}
	for _, room := range census.Rooms[1:] {
		if _, ok := room.Cleanliness.Value(); ok {
			t.Fatal(room)
		}
	}
}

// A roofed room with a door east onto the outdoors, one north into another
// room and one with an unread far side, beside an unroofed room (#1323).
func TestTemperatureRoomsMapsRoofAndDoors(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	rooms := &o.RoomsSnapshot{Rooms: []*o.RoomState{
		{Id: proto.String("roofed"), ProperRoom: proto.Bool(true), OpenRoofCount: proto.Uint32(0), Doors: []*o.RoomDoor{
			{Cell: cell(5, 2), Outside: cell(6, 2), Outdoors: proto.Bool(true)},
			{Cell: cell(3, 5), Outside: cell(3, 6), Outdoors: proto.Bool(false)},
			{Cell: cell(0, 2), Outside: cell(-1, 2)},
		}},
		{Id: proto.String("open"), ProperRoom: proto.Bool(true), OpenRoofCount: proto.Uint32(4)},
		{Id: proto.String("unread"), ProperRoom: proto.Bool(true)},
	}}
	census, known := temperatureRooms(rooms, nil, domain.Unknown[policy.SleepingObservation]()).Value()
	if !known || len(census.Rooms) != 3 {
		t.Fatal(census, known)
	}
	if v, ok := census.Rooms[0].Roofed.Value(); !ok || !v {
		t.Fatal("roofed room", census.Rooms[0].Roofed)
	}
	if v, ok := census.Rooms[1].Roofed.Value(); !ok || v {
		t.Fatal("unroofed room", census.Rooms[1].Roofed)
	}
	if _, ok := census.Rooms[2].Roofed.Value(); ok {
		t.Fatal("missing roof count must stay unknown")
	}
	doors := census.Rooms[0].Doors
	if len(doors) != 3 || doors[0].Cell != (domain.Cell{X: 5, Z: 2}) || doors[0].Outside != (domain.Cell{X: 6, Z: 2}) {
		t.Fatal(doors)
	}
	if !doors[0].EnemyFacing || doors[1].EnemyFacing || doors[2].EnemyFacing {
		t.Fatal("without a plan only outdoor-opening doors face the enemy", doors)
	}
	if _, ok := doors[2].Outdoors.Value(); ok {
		t.Fatal("unread far side must stay unknown")
	}
}
