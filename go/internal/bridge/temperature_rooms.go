package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// RoomCells resolves each room of v to its cells in the frame grid
// (#1346): the held cells carrying its grid_room key, row-major. A room
// without a key, or whose key the grid does not carry, is absent.
func RoomCells(v *o.RoomsSnapshot, grid *cellgrid.Grid) map[string][]domain.Cell {
	out := map[string][]domain.Cell{}
	if v == nil || grid == nil {
		return out
	}
	byKey := map[string][]domain.Cell{}
	for _, cell := range grid.Cells() {
		if key, known := cell.Room.Value(); known {
			byKey[key] = append(byKey[key], cell.Cell)
		}
	}
	for _, room := range v.Rooms {
		if cells := byKey[room.GetGridRoom()]; room.GridRoom != nil && len(cells) > 0 {
			out[room.GetId()] = cells
		}
	}
	return out
}

// ValidateTemperatureRooms checks every fact consumed by temperature
// planning, each room against its grid cells (RoomCells): held cells
// inside its extents, no more than its count, holding its centre and
// beds when none is fogged. A wholly fogged room has no grid key and no
// cells. Other typed room details (beauty, wealth, labels) convey no
// thermal authority.
func ValidateTemperatureRooms(v *o.RoomsSnapshot, roomCells map[string][]domain.Cell, identity *c.Identity) error {
	if v == nil || ValidateContext(v.Context) != nil || !sameIdentity(v.Context.Identity, identity) {
		return contract("invalid room context")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	size := &o.MapSize{Width: proto.Uint32(4096), Height: proto.Uint32(4096)}
	rooms, beds := map[string]bool{}, map[string]bool{}
	for _, room := range v.Rooms {
		cells := roomCells[room.GetId()]
		if room == nil || validID(room.GetId()) != nil || rooms[room.GetId()] || room.ProperRoom == nil || room.Doorway == nil || room.PsychologicallyOutdoors == nil || room.Outdoors == nil || room.TouchesMapEdge == nil || room.OpenRoofCount == nil || room.CellCount == nil || room.GetCellCount() == 0 || (len(cells) == 0 && room.GridRoom != nil) || uint32(len(cells)) > room.GetCellCount() || room.GetOpenRoofCount() > room.GetCellCount() || room.GetDoorway() || room.GetPsychologicallyOutdoors() {
			return contract("invalid indoor room")
		}
		rooms[room.GetId()] = true
		if room.TemperatureC != nil && (math.IsNaN(room.GetTemperatureC()) || math.IsInf(room.GetTemperatureC(), 0)) {
			return contract("invalid room temperature")
		}
		if err := pawnsIssues(room.Issues, room.ProtoReflect()); err != nil {
			return err
		}
		if err := colonyQuantities(room.Contents); err != nil {
			return err
		}
		for _, q := range room.Contents {
			if q.Units == nil {
				return contract("missing room content count")
			}
		}
		if room.Extents == nil || !colonyCell(room.Extents.Minimum, size) || !colonyCell(room.Extents.Maximum, size) || !colonyCell(room.Center, size) {
			return contract("inconsistent room geometry")
		}
		lo, hi := room.Extents.Minimum, room.Extents.Maximum
		local := map[domain.Cell]bool{}
		for _, cell := range cells {
			if cell.X < lo.GetX() || cell.Z < lo.GetZ() || cell.X > hi.GetX() || cell.Z > hi.GetZ() {
				return contract("inconsistent room geometry")
			}
			local[cell] = true
		}
		// Fogged cells are absent from the grid, so a partly fogged room
		// may hide its centre and beds; a wholly fogged one has no key.
		fogged := uint32(len(cells)) < room.GetCellCount()
		if !fogged && !local[domain.Cell{X: room.Center.GetX(), Z: room.Center.GetZ()}] {
			return contract("inconsistent room geometry")
		}
		for _, bed := range room.Beds {
			if bed == nil || pawnsEntity(bed, v.Context) != nil || bed.DefName == nil || bed.MapId == nil || !colonyCell(bed.Position, size) || !fogged && !local[domain.Cell{X: bed.Position.GetX(), Z: bed.Position.GetZ()}] || beds[bed.GetId()] {
				return contract("invalid room bed")
			}
			beds[bed.GetId()] = true
		}
	}
	return nil
}
