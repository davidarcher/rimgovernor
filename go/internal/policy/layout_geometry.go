package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// inWall reports c in the ring of walls round r, corners excluded.
func inWall(r Rectangle, c domain.Cell) bool {
	onX := c.X == r.X-1 || c.X == r.X+r.Width
	onZ := c.Z == r.Z-1 || c.Z == r.Z+r.Height
	return onX && c.Z >= r.Z && c.Z < r.Z+r.Height || onZ && c.X >= r.X && c.X < r.X+r.Width
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

// transposeRoom swaps X and Z; a door facing south faces west once
// transposed, north east, and back.
func transposeRoom(r PlannedRoom) PlannedRoom {
	in := r.Region.Bounds()
	r.Region = RectRegion(Rectangle{X: in.Z, Z: in.X, Width: in.Height, Height: in.Width})
	r.Door = transposeCell(r.Door)
	r.DoorRot = transposeRot(r.DoorRot)
	r.Facing = transposeRot(r.Facing)
	return r
}

func rectsOverlap(a, b Rectangle) bool {
	return a.X < b.X+b.Width && b.X < a.X+a.Width && a.Z < b.Z+b.Height && b.Z < a.Z+a.Height
}
