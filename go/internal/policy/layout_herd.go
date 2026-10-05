package policy

import (
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Herd sites (#1633). The animal pen, the barn and the vet room are
// reservations sized by the herd plan (#1628): pens hold its target herd at
// penCellsPerAnimal each, growing by one more pen when the target outgrows
// them; the barn (roofed, an animal sleeping spot per animal) and the vet
// room (clean, animal beds) stand beside the first pen inside the outer
// ring. A barn or vet room reservation is its walls and interior together
// and never changes: a herd the rooms cannot hold gets another reservation
// of the same kind (herd_rooms.go builds and furnishes them).

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

// PenAnimals is the herd the pens hold: the plan's ceiling summed over its
// races (founders and companions have none and live elsewhere), at least
// penAnimalsFloor.
func (p HerdPlan) PenAnimals() int {
	n := int64(0)
	for _, ceiling := range p.Policy.PopulationMax {
		n += ceiling
	}
	return max(int(n), penAnimalsFloor)
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

// PlanHerdSites tops plan up with the pens, barn and vet room an animals
// strong herd needs: pens, barn beds and vet beds for what the existing
// ones cannot hold. Nothing placed moves; sites that do not fit
// are left out. Zero animals plans nothing. plan holds its core zones.
func PlanHerdSites(plan LayoutPlan, animals int) LayoutPlan {
	if animals <= 0 || len(plan.Hallways()) == 0 {
		return plan
	}
	plan.Reservations = slices.Clone(plan.Reservations)
	planHerdSites(newUtilityGrid(plan), &plan, animals)
	return plan
}

func planHerdSites(u *utilityGrid, plan *LayoutPlan, animals int) {
	if animals <= 0 {
		return
	}
	var pen, barn Rectangle
	var hasPen, hasBarn bool
	held := 0
	for _, r := range plan.Reservations {
		switch r.Kind {
		case ReservePen:
			held += int(r.Area.Width * r.Area.Height)
			if !hasPen {
				pen, hasPen = r.Area, true
			}
		case ReserveBarn:
			barn, hasBarn = r.Area, true
		}
	}
	cx, cz := u.cx, u.cz
	if hasPen {
		cx, cz = pen.X+pen.Width/2, pen.Z+pen.Height/2
	}
	if short := animals*penCellsPerAnimal - held; short > 0 {
		w, h := penSide((short + penCellsPerAnimal - 1) / penCellsPerAnimal)
		site, ok := u.site(w, h, false, false, cx, cz)
		if !ok {
			return
		}
		u.reserve(plan, LayoutReservation{Kind: ReservePen, Area: site})
		if !hasPen {
			cx, cz = site.X+site.Width/2, site.Z+site.Height/2
		}
	}
	// A barn holds a sleeping spot per animal and a vet room VetBeds of
	// them; a herd the rooms outgrow gets another beside the first.
	if short := animals - plan.herdCapacity(PlannedBarn); short > 0 {
		beds := short
		if !hasBarn {
			beds = max(beds, herdBarnMinBeds)
		}
		w, h := walledSide(herdSide(beds))
		site, ok := u.site(w, h, false, false, cx, cz)
		if !ok {
			return
		}
		u.reserve(plan, LayoutReservation{Kind: ReserveBarn, Area: site})
		if !hasBarn {
			barn, hasBarn = site, true
		}
	}
	if hasBarn {
		cx, cz = barn.X+barn.Width/2, barn.Z+barn.Height/2
	}
	if short := VetBeds(animals) - plan.herdCapacity(PlannedVetRoom); short > 0 {
		w, h := walledSide(herdSide(short))
		// The vet room shares the barn's wall when a site beside it fits.
		site, ok := Rectangle{}, false
		if hasBarn {
			site, ok = u.vetBesideBarn(*plan, barn, w, h)
		}
		if !ok {
			site, ok = u.site(w, h, false, false, cx, cz)
		}
		if ok {
			u.reserve(plan, LayoutReservation{Kind: ReserveVetRoom, Area: site})
		}
	}
}

// sharedWallLink is the cell of the wall two reservations' outlines share
// where a door joins them: the middle of the run between the corners both
// walls stand on. False unless the outlines overlap in exactly one wall line.
func sharedWallLink(a, b Rectangle) (domain.Cell, bool) {
	run := func(a0, a1, b0, b1 int32) (int32, bool) {
		lo, hi := max(a0, b0)+1, min(a1, b1)-1
		return (lo + hi - 1) / 2, lo < hi
	}
	switch {
	case a.X+a.Width-1 == b.X || b.X+b.Width-1 == a.X:
		x := b.X
		if b.X+b.Width-1 == a.X {
			x = a.X
		}
		z, ok := run(a.Z, a.Z+a.Height, b.Z, b.Z+b.Height)
		return domain.Cell{X: x, Z: z}, ok
	case a.Z+a.Height-1 == b.Z || b.Z+b.Height-1 == a.Z:
		z := b.Z
		if b.Z+b.Height-1 == a.Z {
			z = a.Z
		}
		x, ok := run(a.X, a.X+a.Width, b.X, b.X+b.Width)
		return domain.Cell{X: x, Z: z}, ok
	}
	return domain.Cell{}, false
}

// vetBesideBarn is the nearest w x h outline sharing one wall with the barn
// (no wall material is wasted on a second parallel wall): every other cell
// must be free. The barn's own door never lands on the shared wall.
func (u *utilityGrid) vetBesideBarn(plan LayoutPlan, barn Rectangle, w, h int32) (Rectangle, bool) {
	barnRoom := plan.herdRoom(barn, PlannedBarn)
	cx, cz := barn.X+barn.Width/2, barn.Z+barn.Height/2
	best, bestCost, found := Rectangle{}, 0.0, false
	for _, side := range []struct{ dx, dz int32 }{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
		x0, z0 := barn.X, barn.Z
		switch {
		case side.dx > 0:
			x0 = barn.X + barn.Width - 1
		case side.dx < 0:
			x0 = barn.X - w + 1
		case side.dz > 0:
			z0 = barn.Z + barn.Height - 1
		default:
			z0 = barn.Z - h + 1
		}
		for off := int32(-h - barn.Height); off <= h+barn.Height+w+barn.Width; off++ {
			r := Rectangle{X: x0, Z: z0 + off, Width: w, Height: h}
			if side.dz != 0 {
				r = Rectangle{X: x0 + off, Z: z0, Width: w, Height: h}
			}
			link, ok := sharedWallLink(r, barn)
			if !ok || link == barnRoom.Door || !u.inset(r) || !u.freeBesideWall(r, barn) {
				continue
			}
			dx, dz := float64(r.X+w/2-cx), float64(r.Z+h/2-cz)
			if cost := math.Sqrt(dx*dx + dz*dz); !found || cost < bestCost {
				best, bestCost, found = r, cost, true
			}
		}
	}
	return best, found
}

// freeBesideWall reports every cell of r free, bar the cells of barn's own
// outline: the shared wall.
func (u *utilityGrid) freeBesideWall(r, barn Rectangle) bool {
	for z := r.Z; z < r.Z+r.Height; z++ {
		for x := r.X; x < r.X+r.Width; x++ {
			if x >= barn.X && x < barn.X+barn.Width && z >= barn.Z && z < barn.Z+barn.Height {
				// Only the barn's wall may be shared, never its interior.
				if x > barn.X && x < barn.X+barn.Width-1 && z > barn.Z && z < barn.Z+barn.Height-1 {
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
