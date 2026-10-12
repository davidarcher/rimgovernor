package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Herd sites. The barn is a reservation sized by the herd plan, grouped in
// units: the misc unit holds every animal that is no herd's, and each herd
// (HerdPlan.Herds) gets a unit of its own, sized from its policy ceiling. A
// unit is a barn (roofed, an animal sleeping spot per animal, the bedroom of
// every animal of the unit, bonded ones included). The hospital, not a room of
// the herd's, treats a sick animal (herd_ward.go). A reservation never changes
// or moves: a herd its unit cannot hold gets another barn beside the same unit
// (herd_rooms.go builds and furnishes them).
//
// A unit is matched to its herd by key: every reservation of a herd's
// unit, a top-up included, carries LayoutReservation.Herd, the herd's race;
// the misc unit's carry none. The plan is a saved blob, so the key is stored.
// Saves regenerate, so a plan without keys is not matched: its units all join
// the misc unit.

const (
	// ReserveBarn is the roofed barn where the herd sleeps.
	ReserveBarn ReservationKind = "barn"
	// ReservePen is a pen beside a barn (herd_pen.go): an outdoor room sharing
	// one wall with it, keyed to the barn's unit.
	ReservePen ReservationKind = "pen"
)

const (
	// penAnimalsFloor is the smallest herd the misc unit is sized for, before
	// the plan has a target.
	penAnimalsFloor = herdUnplannedFloor
)

// PenAnimals is the misc unit's herd: the plan's ceiling summed over
// the races that are no herd of their own (founders and companions have none
// and live elsewhere), at least penAnimalsFloor.
func (p HerdPlan) PenAnimals() int {
	n := int64(0)
	for race, ceiling := range p.Policy.PopulationMax {
		if !slices.Contains(p.Herds, race) {
			n += ceiling
		}
	}
	return max(int(n), penAnimalsFloor)
}

// HerdCeiling is one herd's unit size: the race (the key its reservations
// carry, LayoutReservation.Herd) and its policy ceiling.
type HerdCeiling struct {
	Race    string
	Animals int
}

// HerdUnits are the herds' units, one per herd race in name order:
// each gets a barn of its own, sized from its ceiling.
func (p HerdPlan) HerdUnits() []HerdCeiling {
	herds := slices.Sorted(slices.Values(p.Herds))
	out := make([]HerdCeiling, len(herds))
	for i, race := range herds {
		out[i] = HerdCeiling{Race: string(race), Animals: int(p.Policy.PopulationMax[race])}
	}
	return out
}

// walledSide is the outline of a room whose interior is w by h.
func walledSide(w, h int32) (int32, int32) { return w + 2, h + 2 }

// PlanHerdSites tops plan up with the herd units: the misc unit for
// animals animals and one unit per herds ceiling (HerdPlan.HerdUnits), each
// with the barn beds the unit's existing rooms cannot hold. Nothing placed
// moves; sites that do not fit are left out. A unit is matched to its race by
// the key on its reservations (see the file comment for plans saved without
// one). Zero animals plans no misc unit. plan holds its core zones.
func PlanHerdSites(plan LayoutPlan, animals int, herds ...HerdCeiling) LayoutPlan {
	if animals <= 0 && len(herds) == 0 || len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Reservations = slices.Clone(plan.Reservations)
	planHerdSites(newUtilityGrid(plan), &plan, animals, herds)
	return plan
}

// herdUnit is one unit's reservations: its barns, in plan order.
type herdUnit struct {
	barns []Rectangle
	// herd is the race the unit's reservations are keyed to; empty for the
	// misc unit and for a plan saved before keys.
	herd string
}

// centre is the middle of the unit's first barn, the core when it has none.
func (h herdUnit) centre(u *utilityGrid) (int32, int32) {
	if len(h.barns) > 0 {
		return h.barns[0].X + h.barns[0].Width/2, h.barns[0].Z + h.barns[0].Height/2
	}
	return u.cx, u.cz
}

// herdUnits groups the plan's barn reservations into units by their herd key,
// in order of first appearance.
func (p LayoutPlan) herdUnits() []herdUnit {
	var units []herdUnit
	at := map[string]int{}
	for _, r := range p.Reservations {
		if r.Kind != ReserveBarn {
			continue
		}
		k, seen := at[r.Herd]
		if !seen {
			k = len(units)
			at[r.Herd] = k
			units = append(units, herdUnit{herd: r.Herd})
		}
		units[k].barns = append(units[k].barns, r.Area.Bounds())
	}
	return units
}

// capacity is the 1x1 beds the rooms of role hold on the areas.
func (p LayoutPlan) capacity(areas []Rectangle, role PlannedRole) int {
	n := 0
	for _, a := range areas {
		n += herdRoomBeds(p.herdRoom(a, role), PieceShapes{}, InteriorPieceDef{Def: "bed", Size: domain.Cell{X: 1, Z: 1}})
	}
	return n
}

// housedUnits are the plan's herd units matched to the primary units (the misc
// unit, then one per herd): a unit goes to the herd its reservations are keyed
// to, so an unkeyed one, or one of a herd no longer planned, joins the misc
// unit.
func (p LayoutPlan) housedUnits(herds []HerdCeiling) []herdUnit {
	out := make([]herdUnit, 1+len(herds))
	for _, g := range p.herdUnits() {
		k := slices.IndexFunc(herds, func(h HerdCeiling) bool { return h.Race == g.herd })
		if g.herd == "" {
			k = -1
		}
		out[k+1].barns = append(out[k+1].barns, g.barns...) // -1, misc or a gone herd's unit
	}
	return out
}

func planHerdSites(u *utilityGrid, plan *LayoutPlan, misc int, herds []HerdCeiling) {
	units := plan.housedUnits(herds)
	for i := range units {
		race, animals := "", misc
		if i > 0 {
			race, animals = herds[i-1].Race, herds[i-1].Animals
		}
		if animals <= 0 {
			continue
		}
		// A herd's unit prefers a site near the misc unit's.
		cx, cz := u.cx, u.cz
		if i > 0 {
			cx, cz = units[0].centre(u)
		}
		planUnit(u, plan, units[i], race, animals, cx, cz, i == 0)
	}
}

// planUnit tops one unit up for animals: a barn holding a sleeping spot per
// animal. A herd the unit's barns outgrow gets another barn beside the unit,
// else on free ground near it; none moves. Only a unit with nothing placed yet
// takes a site near cx, cz. Every reservation it adds carries herd, the key of
// the unit's race (empty for misc). The shortfall is then met, so a repeat
// top-up is a no-op.
func planUnit(u *utilityGrid, plan *LayoutPlan, unit herdUnit, herd string, animals int, cx, cz int32, misc bool) {
	if len(unit.barns) > 0 {
		cx, cz = unit.barns[0].X+unit.barns[0].Width/2, unit.barns[0].Z+unit.barns[0].Height/2
	}
	short := animals - plan.capacity(unit.barns, PlannedBarn)
	if short <= 0 {
		return
	}
	beds := short
	if misc && len(unit.barns) == 0 {
		beds = max(beds, herdBarnMinBeds)
	}
	w, h := walledSide(herdSide(beds))
	site, ok := Rectangle{}, false
	for _, anchor := range unit.barns {
		if site, ok = u.besideReservation(*plan, anchor, ReserveBarn, w, h, nil); ok {
			break
		}
	}
	if !ok {
		site, ok = u.site(w, h, false, false, cx, cz)
	}
	if ok {
		u.reserve(plan, LayoutReservation{Kind: ReserveBarn, Area: RectRegion(site), Herd: herd})
	}
}

// sharedWallLink is the cell of the wall two reservations' outlines share
// where a door joins them: the middle of the run between the corners both
// walls stand on. False unless the outlines overlap in exactly one wall line.
func sharedWallLink(a, b Rectangle) (domain.Cell, bool) {
	run := sharedWallRun(a, b)
	if len(run) == 0 {
		return domain.Cell{}, false
	}
	return run[(len(run)-1)/2], true
}

// sharedWallRun are the cells of the wall two outlines share between the
// corners both walls stand on, in order; nil unless they overlap in exactly one
// wall line.
func sharedWallRun(a, b Rectangle) []domain.Cell {
	run := func(a0, a1, b0, b1 int32, at func(int32) domain.Cell) []domain.Cell {
		var out []domain.Cell
		for v := max(a0, b0) + 1; v < min(a1, b1)-1; v++ {
			out = append(out, at(v))
		}
		return out
	}
	switch {
	case a.X+a.Width-1 == b.X || b.X+b.Width-1 == a.X:
		x := b.X
		if b.X+b.Width-1 == a.X {
			x = a.X
		}
		return run(a.Z, a.Z+a.Height, b.Z, b.Z+b.Height, func(z int32) domain.Cell { return domain.Cell{X: x, Z: z} })
	case a.Z+a.Height-1 == b.Z || b.Z+b.Height-1 == a.Z:
		z := b.Z
		if b.Z+b.Height-1 == a.Z {
			z = a.Z
		}
		return run(a.X, a.X+a.Width, b.X, b.X+b.Width, func(x int32) domain.Cell { return domain.Cell{X: x, Z: z} })
	}
	return nil
}

// besideReservation is the nearest w x h outline of kind sharing one wall with
// anchor (no wall material is wasted on a second parallel wall): every other
// cell must be free, no room's door moves or lands on the shared link (the
// link takes a door, or a pen's animal flap), and accept, when set, takes the
// outline.
func (u *utilityGrid) besideReservation(plan LayoutPlan, anchor Rectangle, kind ReservationKind, w, h int32, accept func(Rectangle) bool) (Rectangle, bool) {
	cx, cz := anchor.X+anchor.Width/2, anchor.Z+anchor.Height/2
	best, bestCost, found := Rectangle{}, 0.0, false
	for _, side := range []struct{ dx, dz int32 }{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
		x0, z0 := anchor.X, anchor.Z
		switch {
		case side.dx > 0:
			x0 = anchor.X + anchor.Width - 1
		case side.dx < 0:
			x0 = anchor.X - w + 1
		case side.dz > 0:
			z0 = anchor.Z + anchor.Height - 1
		default:
			z0 = anchor.Z - h + 1
		}
		for off := int32(-h - anchor.Height); off <= h+anchor.Height+w+anchor.Width; off++ {
			r := Rectangle{X: x0, Z: z0 + off, Width: w, Height: h}
			if side.dz != 0 {
				r = Rectangle{X: x0 + off, Z: z0, Width: w, Height: h}
			}
			dx, dz := float64(r.X+w/2-cx), float64(r.Z+h/2-cz)
			cost := math.Sqrt(dx*dx + dz*dz)
			if found && cost >= bestCost {
				continue
			}
			if _, ok := sharedWallLink(r, anchor); !ok || accept != nil && !accept(r) || !u.inset(r) || !u.freeBesideWall(r, anchor) || !plan.doorsHold(LayoutReservation{Kind: kind, Area: RectRegion(r)}) {
				continue
			}
			best, bestCost, found = r, cost, true
		}
	}
	return best, found
}

// doorsHold reports that adding cand to the plan leaves every herd room's door
// where it was and no two of the rooms' doors, links and pens' animal flaps on
// one cell.
func (p LayoutPlan) doorsHold(cand LayoutReservation) bool {
	next := p
	next.Reservations = append(slices.Clone(p.Reservations), cand)
	seen := map[domain.Cell]bool{}
	for _, r := range next.Reservations {
		role, ok := herdRole(r.Kind)
		if !ok {
			continue
		}
		room := next.herdRoom(r.Area.Bounds(), role)
		if !r.Equal(cand) && p.herdRoom(r.Area.Bounds(), role).Door != room.Door {
			return false
		}
		for _, c := range next.DoorsOf(room) {
			if seen[c] {
				return false
			}
			seen[c] = true
		}
	}
	for _, c := range next.flaps() {
		if seen[c] {
			return false
		}
		seen[c] = true
	}
	return true
}

// freeBesideWall reports every cell of r free, bar the cells of anchor's own
// outline: the shared wall.
func (u *utilityGrid) freeBesideWall(r, anchor Rectangle) bool {
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if x >= anchor.X && x < anchor.X+anchor.Width && z >= anchor.Z && z < anchor.Z+anchor.Height {
				// Only the anchor's wall may be shared, never its interior.
				if x > anchor.X && x < anchor.X+anchor.Width-1 && z > anchor.Z && z < anchor.Z+anchor.Height-1 {
					return false
				}
				continue
			}
			if !u.freeWhere(Rectangle{X: x, Z: z, Width: 1, Height: 1}, false, true) {
				return false
			}
		}
	}
	return true
}
