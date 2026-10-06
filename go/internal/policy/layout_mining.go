package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Mine tiers order mining designations along the plan's mining zone (#792):
// ore first, then the rock under dug rooms (walls included) and the cooler
// exhaust shafts so the core's footprint is cleared ahead of growth, then the remaining stone as demand
// calls for it. A dug store room (storeRoom) ranks between ore and the other
// dug rooms: its zone waits on the dig and no stand-in store holds supplies
// meanwhile (#2190). A cell off the plan, or no plan, is MineTierStone.
const (
	MineTierOre   = 0
	MineTierStore = 1
	MineTierCore  = 2
	MineTierStone = 3
)

// MineTier ranks cell under plan; a cell in the zone zoning marked Ore ranks
// first.
func (p LayoutPlan) MineTier(cell domain.Cell) int {
	for _, z := range p.Zones {
		if z.Kind == ZoneMining && z.Ore && zoneHas(z, cell) {
			return MineTierOre
		}
	}
	for _, r := range p.AllRooms() {
		if r.Dug && IsStoreRoom(r.Role) && inRoomRing(r.Interior, cell) {
			return MineTierStore
		}
	}
	for _, r := range p.AllRooms() {
		in := r.Interior
		if r.Dug && cell.X >= in.X-1 && cell.X <= in.X+in.Width && cell.Z >= in.Z-1 && cell.Z <= in.Z+in.Height {
			return MineTierCore
		}
	}
	for _, e := range p.Reservations {
		if e.Kind == ReserveExhaust && cell.X >= e.Area.X && cell.X < e.Area.X+e.Area.Width && cell.Z >= e.Area.Z && cell.Z < e.Area.Z+e.Area.Height {
			return MineTierCore
		}
	}
	return MineTierStone
}

func inRoomRing(in Rectangle, cell domain.Cell) bool {
	return cell.X >= in.X-1 && cell.X <= in.X+in.Width && cell.Z >= in.Z-1 && cell.Z <= in.Z+in.Height
}

// RoomRock is the shared rock step (RockStep) over the cells a planned
// room's shell waits on (#836): its door and interior and, for a cooled
// room, its cooler's wall cell and exhaust shaft, mined before the ring so
// the ring walls the cooler cell and the room is never left open, then the
// cell outside the door and the corridor outward from it. All of them are
// built on or walked, so each needs a floor; rock on the rest of the ring
// stays and walls the room. Dig is in that priority order.
func (p LayoutPlan) RoomRock(room PlannedRoom, cells []SiteCell) RockStepResult {
	var planned []RoleCell
	add := func(c domain.Cell) { planned = append(planned, RoleCell{Cell: c, Role: RockNeedsFloor}) }
	add(room.Door)
	for _, d := range room.Doors {
		add(d.Cell)
	}
	for _, c := range RectangleCells(room.Interior) {
		add(c)
	}
	if site, area, ok := p.CoolerExhaust(room); ok {
		add(site.Cell)
		for _, c := range RectangleCells(area) {
			add(c)
		}
	}
	if shell, err := room.Footprint(); err == nil {
		from := shell.Threshold()
		// A room entered through a neighbour's wall (the butchery behind the
		// freezer) is reached only by walking that neighbour: its interior,
		// door and the corridor from its own threshold are mined too, or the
		// miners have no way in to the link.
		if through, ok := p.linkedNeighbour(room); ok {
			for _, c := range RectangleCells(through.Interior) {
				add(c)
			}
			add(through.Door)
			if outer, err := through.Footprint(); err == nil {
				from = outer.Threshold()
			}
		}
		add(from)
		for _, d := range shell.ExtraDoors() {
			add(d.Threshold())
		}
		hall := p.hallwayCells()
		corridor := make([]domain.Cell, 0, len(hall))
		for c := range hall {
			corridor = append(corridor, c)
		}
		sort.Slice(corridor, func(i, j int) bool {
			di, dj := manhattan(corridor[i], from), manhattan(corridor[j], from)
			if di != dj {
				return di < dj
			}
			if corridor[i].Z != corridor[j].Z {
				return corridor[i].Z < corridor[j].Z
			}
			return corridor[i].X < corridor[j].X
		})
		for _, c := range corridor {
			add(c)
		}
	}
	return RockStep(planned, cells)
}

// linkedNeighbour is the other room whose wall holds room's Link door.
func (p LayoutPlan) linkedNeighbour(room PlannedRoom) (PlannedRoom, bool) {
	if room.Link == nil {
		return PlannedRoom{}, false
	}
	for _, o := range p.AllRooms() {
		if o.Interior != room.Interior && inWall(o.Interior, *room.Link) {
			return o, true
		}
	}
	return PlannedRoom{}, false
}

func manhattan(a, b domain.Cell) int32 {
	dx, dz := a.X-b.X, a.Z-b.Z
	if dx < 0 {
		dx = -dx
	}
	if dz < 0 {
		dz = -dz
	}
	return dx + dz
}

// ExhaustRock is the shared rock step over a standing room's planned cooler
// wall cell and exhaust shaft (#836). The cooler cell is in Dig while the
// room's back wall is still rock: mining it would open the room, so the
// caller mines it only in the plan that places the cooler there (#874).
func (p LayoutPlan) ExhaustRock(room PlannedRoom, cells []SiteCell) (RockStepResult, bool) {
	site, area, ok := p.CoolerExhaust(room)
	if !ok {
		return RockStepResult{}, false
	}
	planned := []RoleCell{{Cell: site.Cell, Role: RockNeedsFloor}}
	for _, c := range RectangleCells(area) {
		planned = append(planned, RoleCell{Cell: c, Role: RockNeedsFloor})
	}
	return RockStep(planned, cells), true
}

func zoneHas(z LayoutZone, cell domain.Cell) bool {
	for _, run := range z.Runs {
		if run.Z == cell.Z && cell.X >= run.X && cell.X < run.X+run.Length {
			return true
		}
	}
	return false
}

// hallwayCells is every cell of the plan's hallways, SpineWidth across.
func (p LayoutPlan) hallwayCells() map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, s := range p.Hallways() {
		lo, hi := s.From, s.To
		if lo.X > hi.X || lo.Z > hi.Z {
			lo, hi = hi, lo
		}
		alongX := lo.Z == hi.Z
		for x := lo.X; x <= hi.X; x++ {
			for z := lo.Z; z <= hi.Z; z++ {
				for d := -SpineWidth / 2; d <= SpineWidth/2; d++ {
					if alongX {
						out[domain.Cell{X: x, Z: z + d}] = true
					} else {
						out[domain.Cell{X: x + d, Z: z}] = true
					}
				}
			}
		}
	}
	return out
}
