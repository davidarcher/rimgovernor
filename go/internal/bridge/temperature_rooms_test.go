package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func temperatureTestSnapshot() *o.RoomsSnapshot {
	cell := &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}
	return &o.RoomsSnapshot{Context: authorityTestContext(7), Completeness: emergencyCounts(1), Rooms: []*o.RoomState{{Id: proto.String("42"), ProperRoom: proto.Bool(true), Doorway: proto.Bool(false), Outdoors: proto.Bool(false), PsychologicallyOutdoors: proto.Bool(false), TouchesMapEdge: proto.Bool(false), OpenRoofCount: proto.Uint32(0), CellCount: proto.Uint32(1), TemperatureC: proto.Float64(5), Center: cell, Extents: &o.Rectangle{Minimum: cell, Maximum: cell}, Cells: []*c.Cell{cell}, Contents: []*o.Quantity{{DefName: proto.String("SleepingSpot"), Units: proto.Int64(1)}}, Beds: []*o.BuildingState{{Building: &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("SleepingSpot"), MapId: proto.Int32(0), Position: cell}, Status: o.BuildingStatus_BUILDING_STATUS_BUILT.Enum()}}}}}
}

func TestTemperatureRoomsRejectMalformedEvidence(t *testing.T) {
	for name, mutate := range map[string]func(*o.RoomsSnapshot){
		"world":            func(v *o.RoomsSnapshot) { v.Context.Identity.LoadToken = proto.String("other") },
		"unknown-fields":   func(v *o.RoomsSnapshot) { v.ProtoReflect().SetUnknown([]byte{0x98, 0x06, 0x01}) },
		"unknown-quantity": func(v *o.RoomsSnapshot) { v.Rooms[0].Contents[0].Units = nil },
		"nan":              func(v *o.RoomsSnapshot) { v.Rooms[0].TemperatureC = proto.Float64(math.NaN()) },
		"negative":         func(v *o.RoomsSnapshot) { v.Rooms[0].Contents[0].Units = proto.Int64(-1) },
		"roof":             func(v *o.RoomsSnapshot) { v.Rooms[0].OpenRoofCount = proto.Uint32(2) },
		"outdoors":         func(v *o.RoomsSnapshot) { v.Rooms[0].PsychologicallyOutdoors = proto.Bool(true) },
		"extents":          func(v *o.RoomsSnapshot) { v.Rooms[0].Extents.Maximum = &c.Cell{X: proto.Int32(8), Z: proto.Int32(7)} },
		"bed-outside": func(v *o.RoomsSnapshot) {
			v.Rooms[0].Beds[0].Building.Position = &c.Cell{X: proto.Int32(8), Z: proto.Int32(7)}
		},
		"bed-duplicate": func(v *o.RoomsSnapshot) { v.Rooms[0].Beds = append(v.Rooms[0].Beds, v.Rooms[0].Beds[0]) },
		"bed-map":       func(v *o.RoomsSnapshot) { v.Rooms[0].Beds[0].Building.MapId = proto.Int32(2) },
		"bed-status":    func(v *o.RoomsSnapshot) { v.Rooms[0].Beds[0].Status = o.BuildingStatus_BUILDING_STATUS_BLUEPRINT.Enum() },
	} {
		t.Run(name, func(t *testing.T) {
			v := temperatureTestSnapshot()
			mutate(v)
			if err := ValidateTemperatureRooms(v, pbIdentity()); err == nil {
				t.Fatal(v)
			}
		})
	}
}
