package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// besideKitchen places the freezer against the kitchen's side wall with its
// only door in that shared wall (#819), so the cook steps straight from the
// stove to the shelf and the freezer never opens onto the warm hallway. It
// tries the kitchen's east side, then west, and reports false for any other
// role, without a kitchen, or when neither side fits (the caller then places
// the freezer on the hallway like any room).
func (g coreGrid) besideKitchen(seg *SpineSegment, rooms []LayoutRoom, role ModuleRole) (LayoutRoom, bool) {
	if role != ModuleFreezer {
		return LayoutRoom{}, false
	}
	var k Rectangle
	found := false
	for _, r := range rooms {
		if r.Role == ModuleKitchen {
			k, found = r.Interior, true
			break
		}
	}
	if !found {
		return LayoutRoom{}, false
	}
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	z0 := seg.From.Z
	for _, east := range []bool{true, false} {
		ix, wall, rot := k.X+k.Width+1, k.X+k.Width, domain.West
		if !east {
			ix, wall, rot = k.X-1-w, k.X-1, domain.East
		}
		room := coreRoom(role, ix, z0, w, d, k.Z > z0)
		if !g.fits(room, *seg) || overlapsRooms(room, rooms) {
			continue
		}
		lo, hi := max(room.Interior.Z, k.Z), min(room.Interior.Z+room.Interior.Height, k.Z+k.Height)
		if lo >= hi {
			continue
		}
		room.Door, room.DoorRot = domain.Cell{X: wall, Z: (lo + hi - 1) / 2}, rot
		room.Dug = g.dug(room)
		seg.From.X = min(seg.From.X, room.Interior.X-1)
		seg.To.X = max(seg.To.X, room.Interior.X+room.Interior.Width)
		return room, true
	}
	return LayoutRoom{}, false
}

// overlapsRooms reports r's interior within a cell of any room's interior;
// a single shared wall between them is fine.
func overlapsRooms(r LayoutRoom, rooms []LayoutRoom) bool {
	a := r.Interior
	for _, o := range rooms {
		b := o.Interior
		if a.X-1 < b.X+b.Width && b.X-1 < a.X+a.Width && a.Z-1 < b.Z+b.Height && b.Z-1 < a.Z+a.Height {
			return true
		}
	}
	return false
}

// southOfSpine reports r on the spine's south side. A hallway door says so
// directly; the freezer's door opens sideways into the kitchen (#819), so
// the spine row decides.
func southOfSpine(plan LayoutPlan, r LayoutRoom) bool {
	switch {
	case r.DoorRot == domain.North || r.DoorRot == domain.South:
		return r.DoorRot == domain.North
	case len(plan.Spine) > 0:
		return r.Interior.Z < plan.Spine[0].From.Z
	}
	return false
}
