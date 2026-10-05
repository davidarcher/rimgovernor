package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// ModuleMealCloset is the dining room's cold meal closet (#936): a 2x2 (or
// 1x2) room behind the dining room's back wall, its only door in that wall,
// cooled by one cooler venting outdoors. The plan adds it only when no
// freezer shares a door with the dining room; the Critical meal stockpile
// then moves into it, where meals never rot.
const ModuleMealCloset ModuleRole = "meal_closet"

// besideRoles pairs a role with the neighbour whose side wall it takes, a
// Link door in the shared wall: the freezer beside the kitchen (#819), so
// the cook steps straight to the shelf, and the dining room beside the
// freezer (#936), so the meal stockpile sits in the cold one door from the
// table, the armory beside the storage room and the wardrobe beside the workshop
// (#1773).
var besideRoles = map[ModuleRole]ModuleRole{ModuleFreezer: ModuleKitchen, ModuleDining: ModuleFreezer, ModuleButchery: ModuleFreezer, ModuleArmory: ModuleStorage, ModuleWardrobe: ModuleWorkshop}

// unlinkedBeside are the besideRoles rooms that share only the wall: the
// armory has no door into the storage room, so haulers never cross its stockpile.
var unlinkedBeside = map[ModuleRole]bool{ModuleArmory: true}

// beside places role against its neighbour's side wall (besideRoles), on
// the hallway side the neighbour stands on: its hallway door takes the
// haulers, the Link the short trip. It tries the neighbour's east side, then
// west, and reports false for any other role, without the neighbour, or when
// neither side fits (the caller then places the room on the hallway like
// any other).
func (g coreGrid) beside(seg *SpineSegment, rooms []LayoutRoom, role ModuleRole) (LayoutRoom, bool) {
	neighbour, ok := besideRoles[role]
	if !ok {
		return LayoutRoom{}, false
	}
	var k Rectangle
	found := false
	for _, r := range rooms {
		if r.Role == neighbour {
			k, found = r.Interior, true
			break
		}
	}
	if !found {
		return LayoutRoom{}, false
	}
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	z0 := seg.From.Z
	if role == ModuleButchery {
		if room, ok := g.behind(seg, rooms, k, role); ok {
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
		if !unlinkedBeside[role] {
			room.Link = &domain.Cell{X: wall, Z: (lo + hi - 1) / 2}
		}
		room.Dug = g.dug(room)
		seg.From.X = min(seg.From.X, room.Interior.X-1)
		seg.To.X = max(seg.To.X, room.Interior.X+room.Interior.Width)
		return room, true
	}
	if role == ModuleArmory || role == ModuleWardrobe {
		return g.behind(seg, rooms, k, role)
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

// MealClosetOwed is the planned meal closet with nothing standing on it
// while the planned dining room stands (#936); MaintainRefrigeration shells
// it.
func (p LayoutPlan) MealClosetOwed(rooms RoomObservation) (LayoutRoom, bool) {
	var closet, dining *LayoutRoom
	for i := range p.Rooms {
		switch r := &p.Rooms[i]; {
		case r.Role == ModuleMealCloset && closet == nil:
			closet = r
		case r.Role == ModuleDining && dining == nil:
			dining = r
		}
	}
	if closet == nil || dining == nil {
		return LayoutRoom{}, false
	}
	if _, ok := PlannedRoomStanding(*dining, rooms); !ok {
		return LayoutRoom{}, false
	}
	if _, ok := PlannedRoomStanding(*closet, rooms); ok {
		return LayoutRoom{}, false
	}
	return *closet, true
}

// FreezerOpensInto reports a planned freezer sharing a door with dining:
// a Link of either room in the wall between them (#936).
func (p LayoutPlan) FreezerOpensInto(dining LayoutRoom) bool {
	_, _, ok := p.FreezerDoorInto(dining)
	return ok
}

// FreezerDoorInto is the first planned freezer sharing a door with dining,
// and that door.
func (p LayoutPlan) FreezerDoorInto(dining LayoutRoom) (LayoutRoom, domain.Cell, bool) {
	for _, f := range p.Rooms {
		if f.Role != ModuleFreezer {
			continue
		}
		for _, link := range []*domain.Cell{f.Link, dining.Link} {
			if link != nil && inWall(f.Interior, *link) && inWall(dining.Interior, *link) {
				return f, *link, true
			}
		}
	}
	return LayoutRoom{}, domain.Cell{}, false
}

// inWall reports c in the ring of walls round r, corners excluded.
func inWall(r Rectangle, c domain.Cell) bool {
	onX := c.X == r.X-1 || c.X == r.X+r.Width
	onZ := c.Z == r.Z-1 || c.Z == r.Z+r.Height
	return onX && c.Z >= r.Z && c.Z < r.Z+r.Height || onZ && c.X >= r.X && c.X < r.X+r.Width
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

// behind places role against its neighbour's back wall, the one opposite the
// hallway, entered only through a Link door in that wall. The butchery sits
// behind the freezer: the butcher walks through the freezer, and carcasses
// stay in the cold. A gear room (#1773) takes the back wall when the side
// walls are taken. The room's own Door is the Link. False when the ground
// behind the neighbour does not fit; the caller then takes the hallway.
func (g coreGrid) behind(seg *SpineSegment, rooms []LayoutRoom, k Rectangle, role ModuleRole) (LayoutRoom, bool) {
	w, d := coreRoomSize[role][0], coreRoomSize[role][1]
	north := k.Z > seg.From.Z
	// The freezer's cooler takes the middle of that wall and vents straight
	// out behind it, so the butchery overlaps only its east or west end cell;
	// a gear room lines up with an edge of its neighbour.
	columns := []int32{k.X, k.X + k.Width - w}
	if role == ModuleButchery {
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
