package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Herd sites (#1633, #2122). The animal pen, the barn and the vet area are
// reservations sized by the herd plan (#1628), grouped in units: the misc unit
// holds every animal that is no herd's, and each herd (HerdPlan.Herds) gets a
// unit of its own, sized from its policy ceiling. A unit is a pen with a barn
// built against its wall (the two joined by an animal flap) and a vet area
// against the barn. Pens hold the unit's herd at penCellsPerAnimal each; the
// barn (roofed, an animal sleeping spot per animal) and the vet area (clean,
// animal beds, VetBeds per barn) are walls and interior together. A
// reservation never changes or moves: a herd its unit cannot hold gets another
// reservation of the same kind beside the same room (herd_rooms.go builds and
// furnishes them), or, boxed in, a whole second unit (#2212).

const (
	// ReserveBarn is the roofed barn where the herd sleeps.
	ReserveBarn ReservationKind = "barn"
	// ReserveVetRoom is the small clean room of animal beds beside
	// the barn: closed to animals except for surgery or treatment, so it is
	// never part of an animal allowed area.
	ReserveVetRoom ReservationKind = "vet_room"
)

const (
	// vetAnimalsPerBed: the vet room holds one animal bed per this many
	// kept animals, at least vetMinBeds.
	vetAnimalsPerBed = 10
	vetMinBeds       = 2
	// penAnimalsFloor is the smallest herd a pen is sized for, before the
	// plan has a target.
	penAnimalsFloor = herdUnplannedFloor
)

// PenAnimals is the misc unit's herd (#2122): the plan's ceiling summed over
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

// HerdUnits are the ceilings of the herds' units (#2122), one per herd race in
// name order: each gets a pen, barn and vet area of its own.
func (p HerdPlan) HerdUnits() []int {
	herds := slices.Sorted(slices.Values(p.Herds))
	out := make([]int, len(herds))
	for i, race := range herds {
		out[i] = int(p.Policy.PopulationMax[race])
	}
	return out
}

// VetBeds is the animal beds a herd of animals needs.
func VetBeds(animals int) int {
	return max(vetMinBeds, (animals+vetAnimalsPerBed-1)/vetAnimalsPerBed)
}

// AnimalAreas are the reservations animals may roam: the pens and the
// barn. The vet room is left out on purpose.
func (p LayoutPlan) AnimalAreas() []Rectangle {
	var out []Rectangle
	for _, r := range p.Reservations {
		if r.Kind == ReservePen || r.Kind == ReserveBarn {
			out = append(out, r.Area)
		}
	}
	return out
}

// walledSide is the outline of a room whose interior is w by h.
func walledSide(w, h int32) (int32, int32) { return w + 2, h + 2 }

// PlanHerdSites tops plan up with the herd units (#2122): the misc unit for
// animals animals and one unit per herds ceiling (HerdPlan.HerdUnits), each
// with the pens, barn beds and vet beds the unit's existing rooms cannot hold.
// Nothing placed moves; sites that do not fit are left out. A unit is matched
// to its race by order (misc first), so the matching is never stored. Zero
// animals plans no misc unit. plan holds its core zones.
func PlanHerdSites(plan LayoutPlan, animals int, herds ...int) LayoutPlan {
	if animals <= 0 && len(herds) == 0 || len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Reservations = slices.Clone(plan.Reservations)
	planHerdSites(newUtilityGrid(plan), &plan, animals, herds)
	return plan
}

// herdUnit is one unit's reservations: the pens and barns joined by a shared
// wall and the vet areas against those barns, in plan order.
type herdUnit struct{ pens, barns, vets []Rectangle }

// centre is the middle of the unit's first reservation, the core when it has
// none.
func (h herdUnit) centre(u *utilityGrid) (int32, int32) {
	for _, rs := range [][]Rectangle{h.pens, h.barns, h.vets} {
		if len(rs) > 0 {
			return rs[0].X + rs[0].Width/2, rs[0].Z + rs[0].Height/2
		}
	}
	return u.cx, u.cz
}

// herdUnits groups the plan's pen, barn and vet reservations into units by the
// walls they share, the first group being the misc unit. A barn is the hinge:
// the pens and vet areas stand against it.
func (p LayoutPlan) herdUnits() []herdUnit {
	var rs []LayoutReservation
	for _, r := range p.Reservations {
		if _, ok := herdRole(r.Kind); ok {
			rs = append(rs, r)
		}
	}
	group := make([]int, len(rs))
	for i := range group {
		group[i] = i
	}
	root := func(i int) int {
		for group[i] != i {
			i = group[i]
		}
		return i
	}
	for i, a := range rs {
		for j := i + 1; j < len(rs); j++ {
			b := rs[j]
			if a.Kind == b.Kind || a.Kind != ReserveBarn && b.Kind != ReserveBarn {
				continue
			}
			if _, joined := sharedWallLink(a.Area, b.Area); joined {
				group[max(root(i), root(j))] = min(root(i), root(j))
			}
		}
	}
	var units []herdUnit
	at := map[int]int{}
	for i, r := range rs {
		k, seen := at[root(i)]
		if !seen {
			k = len(units)
			at[root(i)] = k
			units = append(units, herdUnit{})
		}
		switch r.Kind {
		case ReservePen:
			units[k].pens = append(units[k].pens, r.Area)
		case ReserveBarn:
			units[k].barns = append(units[k].barns, r.Area)
		default:
			units[k].vets = append(units[k].vets, r.Area)
		}
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

// housedUnits are the plan's herd units matched to the n primary units (the
// misc unit, then one per herd): a group past the first n is the second unit a
// boxed-in unit founded, and it joins the primary unit nearest by centre (the
// lower one on a tie). Nothing is stored; the plan's order decides.
func (p LayoutPlan) housedUnits(u *utilityGrid, n int) []herdUnit {
	groups := p.herdUnits()
	if len(groups) <= n {
		return groups
	}
	out := slices.Clone(groups[:n])
	for _, g := range groups[n:] {
		gx, gz := g.centre(u)
		best, bestD := 0, math.MaxFloat64
		for i, primary := range out[:n] {
			px, pz := primary.centre(u)
			if d := math.Hypot(float64(px-gx), float64(pz-gz)); d < bestD {
				best, bestD = i, d
			}
		}
		out[best].pens = append(out[best].pens, g.pens...)
		out[best].barns = append(out[best].barns, g.barns...)
		out[best].vets = append(out[best].vets, g.vets...)
	}
	return out
}

func planHerdSites(u *utilityGrid, plan *LayoutPlan, misc int, herds []int) {
	primaries := 1 + len(herds)
	for i, animals := range append([]int{misc}, herds...) {
		if animals <= 0 {
			continue
		}
		// A unit founds a second one only once every primary unit stands, or
		// the second would be read as the next herd's own.
		founded := len(plan.herdUnits()) >= primaries
		units := plan.housedUnits(u, primaries)
		var unit herdUnit
		if i < len(units) {
			unit = units[i]
		}
		// A herd's unit prefers a site near the misc unit's.
		cx, cz := u.cx, u.cz
		if i > 0 && len(units) > 0 {
			cx, cz = units[0].centre(u)
		}
		planUnit(u, plan, unit, animals, cx, cz, i == 0, founded)
	}
}

// planUnit tops one unit up for animals: a pen beside its barn, a barn beside
// its pen, a vet area beside its barn. A herd the unit's rooms outgrow gets
// another reservation of the same kind beside the unit; none moves. Only a
// unit with nothing placed yet takes a site near cx, cz. A unit boxed in so
// that nothing fits beside it (and only when mayFound) founds a second whole
// unit, pen, barn and vet area, sized for the overflow; its shortfall is then
// met, so a repeat top-up is a no-op.
func planUnit(u *utilityGrid, plan *LayoutPlan, unit herdUnit, animals int, cx, cz int32, misc, mayFound bool) {
	pens, barns, vets := slices.Clone(unit.pens), slices.Clone(unit.barns), slices.Clone(unit.vets)
	place := func(kind ReservationKind, w, h int32, anchors []Rectangle) bool {
		site, ok := Rectangle{}, false
		for _, anchor := range anchors {
			if site, ok = u.besideReservation(*plan, anchor, kind, w, h); ok {
				break
			}
		}
		if !ok && len(pens)+len(barns)+len(vets) == 0 {
			site, ok = u.site(w, h, false, false, cx, cz)
		}
		if !ok {
			return false
		}
		u.reserve(plan, LayoutReservation{Kind: kind, Area: site})
		switch kind {
		case ReservePen:
			pens = append(pens, site)
		case ReserveBarn:
			barns = append(barns, site)
		default:
			vets = append(vets, site)
		}
		return true
	}
	held := 0
	for _, a := range pens {
		held += int(a.Width * a.Height)
	}
	// found raises the second unit for the animals the unit cannot hold, all
	// or nothing: a pen is never left without its barn and vet area.
	found := func() {
		if !mayFound || len(pens)+len(barns)+len(vets) == 0 {
			return
		}
		extra := max(1, animals-min(held/penCellsPerAnimal, plan.capacity(barns, PlannedBarn)))
		vetBeds := max(VetBeds(extra), VetBeds(animals)-plan.capacity(vets, PlannedVetRoom))
		used, n := slices.Clone(u.used), len(plan.Reservations)
		pw, ph := penSide(extra)
		bw, bh := walledSide(herdSide(extra))
		vw, vh := walledSide(herdSide(vetBeds))
		pen, ok := u.site(pw, ph, false, false, cx, cz)
		if ok {
			u.reserve(plan, LayoutReservation{Kind: ReservePen, Area: pen})
			var barn, vet Rectangle
			if barn, ok = u.besideReservation(*plan, pen, ReserveBarn, bw, bh); ok {
				u.reserve(plan, LayoutReservation{Kind: ReserveBarn, Area: barn})
				if vet, ok = u.besideReservation(*plan, barn, ReserveVetRoom, vw, vh); ok {
					u.reserve(plan, LayoutReservation{Kind: ReserveVetRoom, Area: vet})
				}
			}
		}
		if !ok {
			u.used, plan.Reservations = used, plan.Reservations[:n]
		}
	}
	if len(pens) > 0 {
		cx, cz = pens[0].X+pens[0].Width/2, pens[0].Z+pens[0].Height/2
	}
	if short := animals*penCellsPerAnimal - held; short > 0 {
		w, h := penSide((short + penCellsPerAnimal - 1) / penCellsPerAnimal)
		if !place(ReservePen, w, h, barns) {
			found()
			return
		}
		if len(pens) == 1 {
			cx, cz = pens[0].X+pens[0].Width/2, pens[0].Z+pens[0].Height/2
		}
	}
	// A barn holds a sleeping spot per animal and a vet area VetBeds of them.
	if short := animals - plan.capacity(barns, PlannedBarn); short > 0 {
		beds := short
		if misc && len(barns) == 0 {
			beds = max(beds, herdBarnMinBeds)
		}
		w, h := walledSide(herdSide(beds))
		if !place(ReserveBarn, w, h, slices.Concat(pens, vets)) {
			found()
			return
		}
	}
	if short := VetBeds(animals) - plan.capacity(vets, PlannedVetRoom); short > 0 {
		w, h := walledSide(herdSide(short))
		if !place(ReserveVetRoom, w, h, barns) {
			found()
		}
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
// cell must be free, and no room's door moves or lands on the shared link (the
// link takes a door, the vet area's, or the animal flap).
func (u *utilityGrid) besideReservation(plan LayoutPlan, anchor Rectangle, kind ReservationKind, w, h int32) (Rectangle, bool) {
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
			if _, ok := sharedWallLink(r, anchor); !ok || !u.inset(r) || !u.freeBesideWall(r, anchor) || !plan.doorsHold(LayoutReservation{Kind: kind, Area: r}) {
				continue
			}
			best, bestCost, found = r, cost, true
		}
	}
	return best, found
}

// doorsHold reports that adding cand to the plan leaves every herd room's door
// where it was and no two of the rooms' doors, vet links and animal flaps on one
// cell.
func (p LayoutPlan) doorsHold(cand LayoutReservation) bool {
	next := p
	next.Reservations = append(slices.Clone(p.Reservations), cand)
	seen := map[domain.Cell]bool{}
	for _, r := range next.Reservations {
		role, ok := herdRole(r.Kind)
		if !ok {
			continue
		}
		room := next.herdRoom(r.Area, role)
		if r != cand && p.herdRoom(r.Area, role).Door != room.Door {
			return false
		}
		cells := []domain.Cell{room.Door}
		if room.Link != nil {
			cells = append(cells, *room.Link)
		}
		for _, c := range cells {
			if seen[c] {
				return false
			}
			seen[c] = true
		}
	}
	flaps, doors := next.penBarnOpenings()
	for _, c := range append(flaps, doors...) {
		if seen[c] {
			return false
		}
		seen[c] = true
	}
	// A pen and barn that share a wall open through a flap and a door.
	for _, pen := range next.Reservations {
		for _, barn := range next.Reservations {
			if pen.Kind == ReservePen && barn.Kind == ReserveBarn && (pen == cand || barn == cand) {
				if run := sharedWallRun(pen.Area, barn.Area); len(run) == 1 {
					return false
				}
			}
		}
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
