package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// besideKitchen places the freezer against the kitchen's side wall (#819):
// its hallway door takes the haulers, so they never cross the kitchen, and
// a Link door in the shared wall takes the cook straight to the shelf. It
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
		ix, wall := k.X+k.Width+1, k.X+k.Width
		if !east {
			ix, wall = k.X-1-w, k.X-1
		}
		room := coreRoom(role, ix, z0, w, d, k.Z > z0)
		if !g.fits(room, *seg) || overlapsRooms(room, rooms) {
			continue
		}
		lo, hi := max(room.Interior.Z, k.Z), min(room.Interior.Z+room.Interior.Height, k.Z+k.Height)
		if lo >= hi {
			continue
		}
		room.Link = &domain.Cell{X: wall, Z: (lo + hi - 1) / 2}
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
