package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// inWall reports c in the ring of walls round r, corners excluded.
func inWall(r Rectangle, c domain.Cell) bool {
	onX := c.X == r.X-1 || c.X == r.X+r.Width
	onZ := c.Z == r.Z-1 || c.Z == r.Z+r.Height
	return onX && c.Z >= r.Z && c.Z < r.Z+r.Height || onZ && c.X >= r.X && c.X < r.X+r.Width
}

// overlapsRooms reports r's interior within a cell of any room's interior;
// a single shared wall between them is fine.
func overlapsRooms(r PlannedRoom, rooms []PlannedRoom) bool {
	a := r.Interior
	for _, o := range rooms {
		b := o.Interior
		if a.X-1 < b.X+b.Width && b.X-1 < a.X+a.Width && a.Z-1 < b.Z+b.Height && b.Z-1 < a.Z+a.Height {
			return true
		}
	}
	return false
}

func transposeCell(c domain.Cell) domain.Cell { return domain.Cell{X: c.Z, Z: c.X} }

func transposeRot(rot domain.Rotation) domain.Rotation {
	switch rot {
	case domain.North:
		return domain.East
	case domain.East:
		return domain.North
	case domain.South:
		return domain.West
	case domain.West:
		return domain.South
	}
	return rot
}

// transposeRoom swaps X and Z; a door facing its hallway to the south faces
// it to the west once transposed, north to east, and back.
func transposeRoom(r PlannedRoom) PlannedRoom {
	in := r.Interior
	r.Interior = Rectangle{X: in.Z, Z: in.X, Width: in.Height, Height: in.Width}
	r.Door = transposeCell(r.Door)
	if r.Link != nil {
		l := transposeCell(*r.Link)
		r.Link = &l
	}
	r.DoorRot = transposeRot(r.DoorRot)
	if len(r.Doors) > 0 {
		doors := make([]Door, len(r.Doors))
		for i, d := range r.Doors {
			doors[i] = Door{Cell: transposeCell(d.Cell), Rot: transposeRot(d.Rot)}
		}
		r.Doors = doors
	}
	return r
}
