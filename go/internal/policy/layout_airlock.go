package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// PlannedAirlock is a vestibule child room: one walled cell outside its
// parent's door with a second door beyond it, so the parent never opens
// straight onto the cold. The room's Region is the vestibule cell, its Door the
// outer door on the far side and DoorRot the way both doors face; the cells to
// the vestibule's left and right are walls. The parent's wall ring owns the
// walls and the outer door (airlockCells), so the child owes no ring of its own
// (Unfenced). Nothing here is specific to a shelter: any parent door takes one
// (attachAirlock).
const PlannedAirlock PlannedRole = "airlock"

// attachAirlock is the airlock for a room whose door is door facing rot.
func attachAirlock(door domain.Cell, rot domain.Rotation) PlannedRoom {
	v := outside(door, rot)
	return PlannedRoom{
		Role:    PlannedAirlock,
		Region:  RectRegion(Rectangle{X: v.X, Z: v.Z, Width: 1, Height: 1}),
		Door:    outside(v, rot),
		DoorRot: rot,
	}
}

// Vestibule is the airlock's one floor cell.
func (r PlannedRoom) Vestibule() domain.Cell {
	b := r.Region.Bounds()
	return domain.Cell{X: b.X, Z: b.Z}
}

// AirlockWalls are the two wall cells beside the vestibule; nil for any room
// but an airlock.
func (r PlannedRoom) AirlockWalls() []domain.Cell {
	if r.Role != PlannedAirlock {
		return nil
	}
	v := r.Vestibule()
	if r.DoorRot == domain.East || r.DoorRot == domain.West {
		return []domain.Cell{{X: v.X, Z: v.Z - 1}, {X: v.X, Z: v.Z + 1}}
	}
	return []domain.Cell{{X: v.X - 1, Z: v.Z}, {X: v.X + 1, Z: v.Z}}
}

// AirlockGround is the rectangle the airlock occupies beyond its parent's
// ring: the vestibule, its two walls and the outer door. Empty for any room but
// an airlock.
func (r PlannedRoom) AirlockGround() Rectangle {
	if r.Role != PlannedAirlock {
		return Rectangle{}
	}
	g := Rectangle{X: r.Door.X, Z: r.Door.Z, Width: 1, Height: 1}
	for _, c := range append(r.AirlockWalls(), r.Vestibule()) {
		g = unionRect(g, Rectangle{X: c.X, Z: c.Z, Width: 1, Height: 1})
	}
	return g
}

// airlockCells are the cells the airlock adds to its parent's ring: the two
// walls, then the outer door. Nil for any room but an airlock.
func (r PlannedRoom) airlockCells() []domain.Cell {
	if r.Role != PlannedAirlock {
		return nil
	}
	return append(r.AirlockWalls(), r.Door)
}

// childrenOfRole are the children of room id with role.
func (p LayoutPlan) childrenOfRole(id int32, role PlannedRole) []PlannedRoom {
	var out []PlannedRoom
	for _, c := range p.ChildrenOf(id) {
		if c.Role == role {
			out = append(out, c)
		}
	}
	return out
}

// airlockCells are the ring cells r's airlocks add: walls and outer doors.
func (p LayoutPlan) airlockCells(r PlannedRoom) []domain.Cell {
	var out []domain.Cell
	for _, a := range p.childrenOfRole(r.ID, PlannedAirlock) {
		out = append(out, a.airlockCells()...)
	}
	return out
}

// airlockPasses are the sitings tried for a shelter: with an airlock then
// without on a cold map, without alone elsewhere.
func airlockPasses(cold bool) []bool {
	if cold {
		return []bool{true, false}
	}
	return []bool{false}
}
