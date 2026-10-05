package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// beside places role against its neighbour's side wall (besideRoles), on
// the hallway side the neighbour stands on: its hallway door takes the
// haulers, the Link the short trip. It tries the neighbour's east side, then
// west, and reports false for any other role, without the neighbour, or when
// neither side fits (the caller then places the room on the hallway like
// any other).
func (g coreGrid) beside(seg *SpineSegment, rooms []LayoutRoom, role ModuleRole) (LayoutRoom, bool) {
	rule, ok := besideRoles[role]
	if !ok {
		return LayoutRoom{}, false
	}
	var k Rectangle
	found := false
	for _, r := range rooms {
		if r.Role == rule.neighbour {
			k, found = r.Interior, true
			break
		}
	}
	if !found {
		return LayoutRoom{}, false
	}
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	z0 := seg.From.Z
	if rule.backFirst {
		if room, ok := g.behind(seg, rooms, k, role, rule); ok {
			return room, true
		}
	}
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
		if !rule.unlinked {
			room.Link = &domain.Cell{X: wall, Z: (lo + hi - 1) / 2}
		}
		room.Dug = g.dug(room)
		seg.From.X = min(seg.From.X, room.Interior.X-1)
		seg.To.X = max(seg.To.X, room.Interior.X+room.Interior.Width)
		return room, true
	}
	if rule.backLast {
		return g.behind(seg, rooms, k, role, rule)
	}
	return LayoutRoom{}, false
}

// mealClosetSizes are the closet interiors tried, widest first: width along
// the dining room's back wall, depth away from it.
var mealClosetSizes = [][2]int32{{2, 2}, {2, 1}}

// mealCloset places the closet behind the first dining room's back wall,
// centred on it, once no freezer shares a door with a dining room and no
// closet is planned yet. Its walls must stand on core candidates.
func (g coreGrid) mealCloset(rooms []LayoutRoom) (LayoutRoom, bool) {
	var dining *LayoutRoom
	for i := range rooms {
		switch rooms[i].Role {
		case ModuleMealCloset:
			return LayoutRoom{}, false
		case ModuleDining:
			if dining == nil {
				dining = &rooms[i]
			}
		}
	}
	if dining == nil || (LayoutPlan{Rooms: rooms}).FreezerOpensInto(*dining) {
		return LayoutRoom{}, false
	}
	d := dining.Interior
	for _, size := range mealClosetSizes {
		w, h := size[0], size[1]
		ix := d.X + d.Width/2 - w/2
		room := LayoutRoom{Role: ModuleMealCloset, DoorRot: dining.DoorRot}
		if dining.DoorRot == domain.North {
			room.Interior = Rectangle{X: ix, Z: d.Z - 1 - h, Width: w, Height: h}
			room.Door = domain.Cell{X: ix + w/2, Z: d.Z - 1}
		} else {
			room.Interior = Rectangle{X: ix, Z: d.Z + d.Height + 1, Width: w, Height: h}
			room.Door = domain.Cell{X: ix + w/2, Z: d.Z + d.Height}
		}
		walls := roomWalls(room)
		fits := !overlapsRooms(room, rooms)
		for x := walls.X; fits && x < walls.X+walls.Width; x++ {
			for z := walls.Z; fits && z < walls.Z+walls.Height; z++ {
				fits = g.core[domain.Cell{X: x, Z: z}]
			}
		}
		if fits {
			room.Dug = g.dug(room)
			return room, true
		}
	}
	return LayoutRoom{}, false
}

// behind places role against its neighbour's back wall, the one opposite the
// hallway, entered only through a Link door in that wall. The butchery sits
// behind the freezer: the butcher walks through the freezer, and carcasses
// stay in the cold. A gear room (#1773) takes the back wall when the side
// walls are taken. The room's own Door is the Link. False when the ground
// behind the neighbour does not fit; the caller then takes the hallway.
func (g coreGrid) behind(seg *SpineSegment, rooms []LayoutRoom, k Rectangle, role ModuleRole, rule relationRule) (LayoutRoom, bool) {
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	north := k.Z > seg.From.Z
	// The freezer's cooler takes the middle of that wall and vents straight
	// out behind it, so the butchery overlaps only its east or west end cell;
	// a gear room lines up with an edge of its neighbour.
	columns := []int32{k.X, k.X + k.Width - w}
	if rule.endCell {
		columns = []int32{k.X + k.Width - 1, k.X - w + 1}
	}
	for _, ix := range columns {
		room := LayoutRoom{Role: role, Interior: Rectangle{X: ix, Z: k.Z - 1 - d, Width: w, Height: d}, DoorRot: domain.North}
		wall := k.Z - 1
		if north {
			room.Interior.Z, room.DoorRot, wall = k.Z+k.Height+1, domain.South, k.Z+k.Height
		}
		if !g.fits(room, *seg) || overlapsRooms(room, rooms) {
			continue
		}
		lo, hi := max(ix, k.X), min(ix+w, k.X+k.Width)
		if lo >= hi {
			continue
		}
		link := domain.Cell{X: (lo + hi - 1) / 2, Z: wall}
		room.Door, room.Link = link, &link
		room.Dug = g.dug(room)
		return room, true
	}
	return LayoutRoom{}, false
}
