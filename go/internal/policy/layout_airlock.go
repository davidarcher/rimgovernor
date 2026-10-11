package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Airlock is a cold-map shelter's vestibule: one walled cell outside the
// shelter's door with a second door beyond it, so the heated room never
// opens straight onto the cold. Vestibule is the cell outside the shelter
// door (the ring's door cell leads into it) and Outer is the door on its far
// side; the cells to its left and right are walls. The shelter's own door
// faces the same way (PlannedRoom.DoorRot).
type Airlock struct {
	Vestibule domain.Cell
	Outer     domain.Cell
}

// newAirlock is the vestibule for a room whose door is door facing rot.
func newAirlock(door domain.Cell, rot domain.Rotation) *Airlock {
	v := outside(door, rot)
	return &Airlock{Vestibule: v, Outer: outside(v, rot)}
}

// AirlockWalls are the two wall cells beside the vestibule; nil without an
// airlock.
func (r PlannedRoom) AirlockWalls() []domain.Cell {
	if r.Airlock == nil {
		return nil
	}
	v := r.Airlock.Vestibule
	if r.DoorRot == domain.East || r.DoorRot == domain.West {
		return []domain.Cell{{X: v.X, Z: v.Z - 1}, {X: v.X, Z: v.Z + 1}}
	}
	return []domain.Cell{{X: v.X - 1, Z: v.Z}, {X: v.X + 1, Z: v.Z}}
}

// AirlockGround is the rectangle the airlock occupies beyond the ring: the
// vestibule, its two walls and the outer door. Empty without an airlock.
func (r PlannedRoom) AirlockGround() Rectangle {
	if r.Airlock == nil {
		return Rectangle{}
	}
	g := Rectangle{X: r.Airlock.Outer.X, Z: r.Airlock.Outer.Z, Width: 1, Height: 1}
	for _, c := range append(r.AirlockWalls(), r.Airlock.Vestibule) {
		g = unionRect(g, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
	}
	return g
}

// airlockCells are the cells the airlock adds to the room's ring: the two
// walls, then the outer door.
func (r PlannedRoom) airlockCells() []domain.Cell {
	if r.Airlock == nil {
		return nil
	}
	return append(r.AirlockWalls(), r.Airlock.Outer)
}

// airlockPasses are the sitings tried for a shelter: with an airlock then
// without on a cold map, without alone elsewhere.
func airlockPasses(cold bool) []bool {
	if cold {
		return []bool{true, false}
	}
	return []bool{false}
}
