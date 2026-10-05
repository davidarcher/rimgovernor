package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

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
