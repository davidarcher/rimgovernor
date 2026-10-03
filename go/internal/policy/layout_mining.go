package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// Mine tiers order mining designations along the plan's mining zone (#792):
// ore first, then the rock under dug rooms (walls included) and the cooler
// exhaust shafts so the core's footprint is cleared ahead of growth, then the remaining stone as demand
// calls for it. A cell off the plan, or no plan, is MineTierStone.
const (
	MineTierOre   = 0
	MineTierCore  = 1
	MineTierStone = 2
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

// RoomDig lists the natural rock a planned room's shell waits on (#836):
// its interior and door cell and, for a cooled room, its cooler's wall cell
// and exhaust shaft, mined before the ring so the ring walls the cooler cell
// and the room is never left open. Rock on the rest of the ring stays and
// walls the room.
func (p LayoutPlan) RoomDig(room LayoutRoom, cells []SiteCell) []domain.Cell {
	want := map[domain.Cell]bool{room.Door: true}
	if shell, err := room.Footprint(); err == nil {
		want[shell.Threshold()] = true // a hallway cell still in rock
	}
	for c := range p.hallwayCells() {
		want[c] = true // the corridor is mined out whole
	}
	for _, c := range RectangleCells(room.Interior) {
		want[c] = true
	}
	if site, area, ok := p.CoolerExhaust(room); ok {
		want[site.Cell] = true
		for _, c := range RectangleCells(area) {
			want[c] = true
		}
	}
	return rockAmong(want, cells)
}

// ExhaustDig lists the rock in a standing room's planned exhaust shaft
// (#836). When CoolerCellRock also holds, mining the wall cell would open
// the room, so the caller mines it only in the plan that places the cooler
// there (#874).
func (p LayoutPlan) ExhaustDig(room LayoutRoom, cells []SiteCell) []domain.Cell {
	_, area, ok := p.CoolerExhaust(room)
	if !ok {
		return nil
	}
	want := map[domain.Cell]bool{}
	for _, c := range RectangleCells(area) {
		want[c] = true
	}
	return rockAmong(want, cells)
}

// CoolerCellRock reports that a room's planned cooler wall cell is known
// natural rock (#874): a standing room whose back wall is still rock.
func (p LayoutPlan) CoolerCellRock(room LayoutRoom, cells []SiteCell) bool {
	site, _, ok := p.CoolerExhaust(room)
	return ok && len(rockAmong(map[domain.Cell]bool{site.Cell: true}, cells)) > 0
}

// rockAmong is the cells of want known to be natural rock, in site order.
func rockAmong(want map[domain.Cell]bool, cells []SiteCell) []domain.Cell {
	var out []domain.Cell
	for _, c := range cells {
		if rock, known := c.NaturalRock.Value(); known && rock && want[c.Cell] {
			out = append(out, c.Cell)
		}
	}
	return out
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
