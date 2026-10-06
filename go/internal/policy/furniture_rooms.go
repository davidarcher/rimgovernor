package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FurniturePiece is one installed building inside a room: its thing id, its
// definition, its North size, rotation and world footprint.
type FurniturePiece struct {
	Thing string
	Def   string
	Size  domain.Cell
	Rot   domain.Rotation
	Rect  Rectangle
}

// FurnitureRoom is a room the furniture levers measure: its census id, its
// plan input and every building standing on its floor.
type FurnitureRoom struct {
	ID     string
	Room   InteriorRoom
	Pieces []FurniturePiece
}

// FurnitureRooms reads the rectangular census rooms that have a derived
// interior plan as furniture items: the doors are the observed doorways and
// the pieces every census building whose footprint lies on the floor.
func FurnitureRooms(rooms RoomObservation, census CurrentConstruction, cells []SiteCell) []FurnitureRoom {
	var doorways []domain.Cell
	for _, c := range cells {
		if positive(c.Doorway) {
			doorways = append(doorways, c.Cell)
		}
	}
	var out []FurnitureRoom
	for _, room := range rooms.Rooms {
		role, known := room.Role.Value()
		if !known || room.ID == "" {
			continue
		}
		input, ok := InteriorRoomFromCensus(room, role, doorways, rooms.Shapes)
		if !ok {
			continue
		}
		input.Dining = rooms.Dining
		t := FurnitureRoom{ID: room.ID}
		for _, b := range census.Buildings {
			if b.ID == "" || len(b.Cells) == 0 {
				continue
			}
			rect := cellsRectangle(b.Cells)
			if !rectInside(input.Interior, rect) {
				continue
			}
			rot := b.Building.Rotation()
			size := domain.Cell{X: rect.Width, Z: rect.Height}
			if rot == domain.East || rot == domain.West {
				size = domain.Cell{X: rect.Height, Z: rect.Width}
			}
			t.Pieces = append(t.Pieces, FurniturePiece{Thing: b.ID, Def: b.Building.Definition(), Size: size, Rot: rot, Rect: rect})
		}
		sort.Slice(t.Pieces, func(i, j int) bool { return t.Pieces[i].Thing < t.Pieces[j].Thing })
		// The plan takes the room's standing definitions, so a bedroom is
		// planned around the bed it has (a DoubleBed stays on plan).
		standing := map[string]bool{}
		for _, p := range t.Pieces {
			if !standing[p.Def] {
				standing[p.Def] = true
				input.Standing = append(input.Standing, p.Def)
			}
		}
		sort.Strings(input.Standing)
		if _, ok := PlanInterior(input, InteriorPieceDef{}); !ok {
			continue
		}
		t.Room = input
		out = append(out, t)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ID < out[j].ID })
	return out
}

func rectInside(outer, inner Rectangle) bool {
	return inner.X >= outer.X && inner.Z >= outer.Z && inner.X+inner.Width <= outer.X+outer.Width && inner.Z+inner.Height <= outer.Z+outer.Height
}
