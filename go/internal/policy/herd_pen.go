package policy

import (
	"math"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The pen. A unit with roaming animals (RequiresPen: the races the game walks
// off the map unless they are fully enclosed) gets a pen reservation beside its
// barn, sharing one wall with it. The animal flap in that shared wall joins
// the two, so the barn and the pen are one enclosure; the pen's own fence has
// a gate for colonists. The pen is sited on ground that grows grazing and sized
// by the game's pen food sum (PenCells over the herd's PenDemand), and one
// PenMarker inside it claims the enclosure for the game's pen logic. Pen
// membership comes from the native enclosed-pen lookup, so a marker standing is
// not an animal contained.

const (
	// PenFenceDefinition and PenGateDefinition are the defs of an outdoor
	// room's ring (the pen, the materials yard).
	PenFenceDefinition = "Fence"
	PenGateDefinition  = "FenceGate"
	// PenMarkerDefinition is the pen's one piece.
	PenMarkerDefinition = "PenMarker"
	// penMarkerSlot is the marker's template slot.
	penMarkerSlot = "pen.marker"
)

// PenWant is the pen a unit asks for: Race is the unit's key (the herd's race,
// empty for the misc unit; LayoutReservation.Herd) and Cells the interior its
// animals' grazing needs.
type PenWant struct {
	Race  string
	Cells int
}

// PenWants are the pens the herd plan asks for, the misc unit's first and then
// each herd's in name order. A unit asks for one only for the races whose
// animals are held in a pen (RequiresPen), sized for the larger of the race's
// ceiling and the animals kept, by PenCells over the weakest grazing terrain
// of pasture (the pen's site is not chosen yet). A unit whose feed is unread
// asks for none, as does a pasture table that grows nothing.
func (p HerdPlan) PenWants(animals []UpkeepAnimal, races AnimalRaceCatalog, pasture PastureTable) []PenWant {
	perCell, grows := pasture.Weakest()
	if !grows {
		return nil
	}
	kept := map[Resource]int64{}
	for _, a := range animals {
		if pen, known := a.RequiresPen.Value(); known && pen {
			kept[a.Definition]++
		}
	}
	units := map[string]map[Resource]int64{}
	for race, n := range kept {
		unit := ""
		if slices.Contains(p.Herds, race) {
			unit = string(race)
		}
		if units[unit] == nil {
			units[unit] = map[Resource]int64{}
		}
		units[unit][race] = max(p.Policy.PopulationMax[race], n)
	}
	keys := make([]string, 0, len(units))
	for unit := range units {
		keys = append(keys, unit)
	}
	sort.Strings(keys)
	var out []PenWant
	for _, unit := range keys {
		if demand, known := PenDemand(races, units[unit]); known {
			out = append(out, PenWant{Race: unit, Cells: PenCells(demand, perCell)})
		}
	}
	return out
}

// PenWants are the pens the layout gives the herd, from the routine reading;
// none while the animal census or the pasture table is unread.
func (f RoundsFacts) PenWants() []PenWant {
	animals, known := f.AnimalUpkeep.Animals.Value()
	pasture, pastureKnown := f.Pasture.Value()
	if !known || !pastureKnown {
		return nil
	}
	return PlanHerd(f.HerdPlanInput()).PenWants(animals, f.AnimalUpkeep.AnimalRaces, pasture)
}

// GrazingGround are the survey cells a pen may stand on: walkable open ground
// whose terrain grows grazing, with no rock, tree, ore, ruin, prop or edifice
// on it and no hazard.
func GrazingGround(s MapSurvey, pasture PastureTable) map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for _, c := range s.Cells {
		if _, grows := pasture.PerCell(c.Terrain); grows && c.Walkable && !c.Rock && !c.Hazard && !c.Tree && !c.Ore && !c.Ruin && !c.Prop && !c.Built {
			out[c.Cell] = true
		}
	}
	return out
}

// penSide is the near-square interior holding at least cells cells.
func penSide(cells int) (w, h int32) {
	w = int32(math.Ceil(math.Sqrt(float64(max(cells, 1)))))
	h = int32((cells + int(w) - 1) / int(w))
	return w, max(h, 1)
}

// penCells is the interior cells of the pens of the unit keyed herd.
func (p LayoutPlan) penCells(herd string) int {
	n := 0
	for _, r := range p.Reservations {
		if r.Kind == ReservePen && r.Herd == herd {
			n += int(pad(r.Area.Bounds(), -1).Width * pad(r.Area.Bounds(), -1).Height)
		}
	}
	return n
}

// PensOwed is the cells the pens still lack, summed over the wants: the replan
// trigger for a herd whose grazing outgrew its pens.
func PensOwed(plan LayoutPlan, wants []PenWant) int {
	n := 0
	for _, w := range wants {
		n += max(w.Cells-plan.penCells(w.Race), 0)
	}
	return n
}

// PlanPenSites tops plan up with the pens wants ask for: each unit's shortfall
// in cells becomes a pen sharing one wall with the unit's first barn that has
// ground for it, wholly on grazing ground. Nothing placed moves; a pen that fits
// nowhere is left out, and a unit without a barn gets none. plan holds its core
// zones.
func PlanPenSites(plan LayoutPlan, grazing map[domain.Cell]bool, wants ...PenWant) LayoutPlan {
	if len(wants) == 0 || len(grazing) == 0 || len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Reservations = slices.Clone(plan.Reservations)
	u := newUtilityGrid(plan)
	for _, want := range wants {
		short := want.Cells - plan.penCells(want.Race)
		if short <= 0 {
			continue
		}
		iw, ih := penSide(short)
		w, h := walledSide(iw, ih)
		var barns []Rectangle
		for _, r := range plan.Reservations {
			if r.Kind == ReserveBarn && r.Herd == want.Race {
				barns = append(barns, r.Area.Bounds())
			}
		}
		for _, barn := range barns {
			site, ok := u.besideReservation(plan, barn, ReservePen, w, h, func(r Rectangle) bool {
				for _, c := range rectCells(pad(r, -1)) {
					if !grazing[c] {
						return false
					}
				}
				return true
			})
			if ok {
				u.reserve(&plan, LayoutReservation{Kind: ReservePen, Area: RectRegion(site), Herd: want.Race})
				break
			}
		}
	}
	return plan
}

// penLink is the cell of the wall pen shares with its unit's barn, where the
// animal flap joins them: the barn's wall cell, never a corner. False when the
// pen shares no wall with a barn of its unit.
func (p LayoutPlan) penLink(pen LayoutReservation) (domain.Cell, bool) {
	for _, r := range p.Reservations {
		if r.Kind != ReserveBarn || r.Herd != pen.Herd {
			continue
		}
		if c, ok := sharedWallLink(pen.Area.Bounds(), r.Area.Bounds()); ok {
			return c, true
		}
	}
	return domain.Cell{}, false
}

// flaps are the animal flap cells, one per pen, in plan order.
func (p LayoutPlan) flaps() []domain.Cell {
	var out []domain.Cell
	for _, r := range p.Reservations {
		if r.Kind != ReservePen {
			continue
		}
		if c, ok := p.penLink(r); ok {
			out = append(out, c)
		}
	}
	return out
}

// FlapCells are the cells of r's ring that take an animal flap: the link of a
// pen in the wall of its barn, none for any other room (a pen's own ring holds
// the barn's wall there).
func (p LayoutPlan) FlapCells(r PlannedRoom) []domain.Cell {
	return p.ringCells(r, p.flaps())
}

func (p LayoutPlan) ringCells(r PlannedRoom, cells []domain.Cell) []domain.Cell {
	if r.Outdoor {
		return nil
	}
	ring := r.wallRing()
	var out []domain.Cell
	for _, c := range cells {
		if ring.Contains(c) {
			out = append(out, c)
		}
	}
	return out
}

// PenMarkerStanding reports a PenMarker standing in room.
func PenMarkerStanding(room PlannedRoom, built []CurrentBuilding) bool {
	for _, b := range built {
		if b.Building.Definition() == PenMarkerDefinition && len(b.Cells) > 0 && room.Region.Contains(b.Cells[0]) {
			return true
		}
	}
	return false
}

// PensStand reports the plan holding at least one pen and every pen enclosed
// with its marker standing: the fence and gate as planned, the barn's wall and
// flap beside it, one PenMarker inside.
func PensStand(plan LayoutPlan, ground GroundCensus, built []CurrentBuilding) bool {
	pens := plan.HerdRooms(PlannedPen)
	for _, room := range pens {
		if !plan.GroundMatches(room, ground) || !PenMarkerStanding(room, built) {
			return false
		}
	}
	return len(pens) > 0
}

// penMarkerPiece is the wanted marker for pen: the one standing in it, else
// the free interior cell nearest its centre that the marker's whole shape fits
// on. False when the pen has no room for it.
func penMarkerPiece(room PlannedRoom, marker InteriorPieceDef, built []CurrentBuilding, taken map[domain.Cell]bool) (WantedPiece, bool) {
	for _, b := range built {
		if b.Building.Definition() == PenMarkerDefinition && len(b.Cells) > 0 && room.Region.Contains(b.Cells[0]) {
			r := cellsRectangle(b.Cells)
			return WantedPiece{DefName: PenMarkerDefinition, Minimum: domain.Cell{X: r.X, Z: r.Z}, Maximum: domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}}, true
		}
	}
	size := marker.Size
	if marker.Def == "" {
		return WantedPiece{}, false
	}
	if size.X < 1 || size.Z < 1 {
		size = domain.Cell{X: 1, Z: 1}
	}
	in := room.Region.Bounds()
	cx, cz := in.X+in.Width/2, in.Z+in.Height/2
	var best domain.Cell
	bestDist, found := int32(0), false
	for _, c := range room.Region.Cells() {
		fits := true
		for _, m := range rectCells(Rectangle{X: c.X, Z: c.Z, Width: size.X, Height: size.Z}) {
			fits = fits && room.Region.Contains(m) && !taken[m]
		}
		if d := (c.X-cx)*(c.X-cx) + (c.Z-cz)*(c.Z-cz); fits && (!found || d < bestDist) {
			best, bestDist, found = c, d, true
		}
	}
	if !found {
		return WantedPiece{}, false
	}
	return WantedPiece{DefName: PenMarkerDefinition, Minimum: best, Maximum: domain.Cell{X: best.X + size.X - 1, Z: best.Z + size.Z - 1}, Slot: penMarkerSlot, Size: size, Rot: domain.North}, true
}
