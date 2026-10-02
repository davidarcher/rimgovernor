package bridge

import (
	"math"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func temperatureTestSnapshot() *o.RoomsSnapshot {
	cell := &c.Cell{X: proto.Int32(3), Z: proto.Int32(7)}
	return &o.RoomsSnapshot{Context: authorityTestContext(7), Completeness: emergencyCounts(1), Rooms: []*o.RoomState{{Id: proto.String("42"), ProperRoom: proto.Bool(true), Doorway: proto.Bool(false), Outdoors: proto.Bool(false), PsychologicallyOutdoors: proto.Bool(false), TouchesMapEdge: proto.Bool(false), OpenRoofCount: proto.Uint32(0), CellCount: proto.Uint32(1), TemperatureC: proto.Float64(5), Center: cell, Extents: &o.Rectangle{Minimum: cell, Maximum: cell}, GridRoom: proto.String("1403"), Contents: []*o.Quantity{{DefName: proto.String("SleepingSpot"), Units: proto.Int64(1)}}, Beds: []*o.EntityRef{{Id: proto.String("bed"), DefName: proto.String("SleepingSpot"), MapId: proto.Int32(0), Position: cell}}}}}
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
		"extents":          func(v *o.RoomsSnapshot) { v.Rooms[0].Extents.Minimum = &c.Cell{X: proto.Int32(4), Z: proto.Int32(7)} },
		"bed-outside": func(v *o.RoomsSnapshot) {
			v.Rooms[0].Beds[0].Position = &c.Cell{X: proto.Int32(8), Z: proto.Int32(7)}
		},
		"bed-duplicate": func(v *o.RoomsSnapshot) { v.Rooms[0].Beds = append(v.Rooms[0].Beds, v.Rooms[0].Beds[0]) },
		"bed-map":       func(v *o.RoomsSnapshot) { v.Rooms[0].Beds[0].MapId = proto.Int32(2) },
		"unresolved":    func(v *o.RoomsSnapshot) { v.Rooms[0].Id = proto.String("43") },
		"oversized":     func(v *o.RoomsSnapshot) { v.Rooms[0].CellCount = proto.Uint32(0) },
	} {
		t.Run(name, func(t *testing.T) {
			v := temperatureTestSnapshot()
			mutate(v)
			if err := ValidateTemperatureRooms(v, temperatureTestCells(), pbIdentity()); err == nil {
				t.Fatal(v)
			}
		})
	}
}

func temperatureTestCells() map[string][]domain.Cell {
	return map[string][]domain.Cell{"42": {{X: 3, Z: 7}}}
}

func TestTemperatureRoomsAcceptGridCells(t *testing.T) {
	if err := ValidateTemperatureRooms(temperatureTestSnapshot(), temperatureTestCells(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
}

// TestTemperatureRoomsAcceptFoggedRooms: fogged cells are absent from the
// grid, so a wholly fogged room (no grid key) has no cells and a partly
// fogged one may hide its centre and beds.
func TestTemperatureRoomsAcceptFoggedRooms(t *testing.T) {
	whole := temperatureTestSnapshot()
	whole.Rooms[0].GridRoom = nil
	if err := ValidateTemperatureRooms(whole, nil, pbIdentity()); err != nil {
		t.Fatalf("wholly fogged: %v", err)
	}
	partial := temperatureTestSnapshot()
	room := partial.Rooms[0]
	room.CellCount = proto.Uint32(2)
	room.Extents.Maximum = &c.Cell{X: proto.Int32(4), Z: proto.Int32(7)}
	if err := ValidateTemperatureRooms(partial, map[string][]domain.Cell{"42": {{X: 4, Z: 7}}}, pbIdentity()); err != nil {
		t.Fatalf("partly fogged: %v", err)
	}
}

// TestRoomCellsResolveTheGridRoomKey: a room's cells are the grid cells
// carrying its grid_room key; a room the grid does not key is absent.
func TestRoomCellsResolveTheGridRoomKey(t *testing.T) {
	cells := map[domain.Cell]policy.SiteCell{}
	for x := int32(0); x < 4; x++ {
		room := domain.Known("0")
		if x >= 2 {
			room = domain.Known("2")
		}
		cells[domain.Cell{X: x}] = policy.SiteCell{Cell: domain.Cell{X: x}, Room: room}
	}
	grid, err := cellgrid.FromCells(cells, cellgrid.MaxCells)
	if err != nil {
		t.Fatal(err)
	}
	rooms := &o.RoomsSnapshot{Rooms: []*o.RoomState{{Id: proto.String("a"), GridRoom: proto.String("2")}, {Id: proto.String("b"), GridRoom: proto.String("9")}, {Id: proto.String("c")}}}
	got := RoomCells(rooms, grid)
	if len(got) != 1 || !slices.Equal(got["a"], []domain.Cell{{X: 2}, {X: 3}}) {
		t.Fatal(got)
	}
}
