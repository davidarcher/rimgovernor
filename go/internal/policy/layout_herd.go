package policy

import (
	"log/slog"
	"slices"
)

// Herd sites (#1633). The animal pen, the barn and the vet room are
// reservations sized by the herd plan (#1628): pens hold its target herd at
// penCellsPerAnimal each, growing by one more pen when the target outgrows
// them; the barn (roofed, animal beds) and the vet room (clean, medical
// animal beds) stand beside the first pen inside the outer ring. A barn or
// vet room reservation is its walls and interior together, and is sized
// once, from the herd when it is first planned.

const (
	// ReserveBarn is the roofed barn where the herd sleeps.
	ReserveBarn ReservationKind = "barn"
	// ReserveVetRoom is the small clean room of medical animal beds beside
	// the barn: closed to animals except for surgery or treatment, so it is
	// never part of an animal allowed area.
	ReserveVetRoom ReservationKind = "vet_room"
)

const (
	// barnCellsPerAnimal is the barn interior per kept animal (a bed or
	// sleeping spot and room to reach it); barnMinCells is the smallest.
	barnCellsPerAnimal = 2
	barnMinCells       = 25
	// vetCellsPerBed is a medical animal bed and the floor to reach it; the
	// room holds one bed per vetAnimalsPerBed kept animals, at least vetMinBeds.
	vetCellsPerBed   = 6
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

// VetBeds is the medical animal beds a herd of animals needs.
func VetBeds(animals int) int {
	return max(vetMinBeds, (animals+vetAnimalsPerBed-1)/vetAnimalsPerBed)
}

// VetRoomBeds is the medical animal beds a vet room reservation holds.
func VetRoomBeds(area Rectangle) int {
	return int(max(area.Width-2, 0)*max(area.Height-2, 0)) / vetCellsPerBed
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

// walledSide is the near-square outline of a room whose interior holds at
// least cells cells.
func walledSide(cells int) (w, h int32) {
	w, h = squareSide(cells)
	return w + 2, h + 2
}

// PlanHerdSites tops plan up with the pens, barn and vet room an animals
// strong herd needs: pens for what the existing ones lack, and a barn and
// vet room if there are none. Nothing placed moves; sites that do not fit
// are left out. Zero animals plans nothing. plan holds its core zones.
func PlanHerdSites(plan LayoutPlan, animals int) LayoutPlan {
	if animals <= 0 || len(plan.Spine) == 0 {
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
	var hasPen, hasBarn, hasVet bool
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
		case ReserveVetRoom:
			hasVet = true
		}
	}
	cx, cz := u.fieldCentre()
	if hasPen {
		cx, cz = pen.X+pen.Width/2, pen.Z+pen.Height/2
	}
	if short := animals*penCellsPerAnimal - held; short > 0 {
		w, h := penSide((short + penCellsPerAnimal - 1) / penCellsPerAnimal)
		site, ok := u.site(w, h, false, true, cx, cz)
		if !ok {
			slog.Warn("layout: no room for the animal pen", "animals", animals, "width", w, "height", h)
			return
		}
		u.reserve(plan, LayoutReservation{Kind: ReservePen, Area: site})
		if !hasPen {
			cx, cz = site.X+site.Width/2, site.Z+site.Height/2
		}
	}
	if !hasBarn {
		w, h := walledSide(max(animals*barnCellsPerAnimal, barnMinCells))
		site, ok := u.site(w, h, false, true, cx, cz)
		if !ok {
			slog.Warn("layout: no room for the barn", "animals", animals, "width", w, "height", h)
			return
		}
		u.reserve(plan, LayoutReservation{Kind: ReserveBarn, Area: site})
		cx, cz = site.X+site.Width/2, site.Z+site.Height/2
	} else if !hasVet {
		cx, cz = barn.X+barn.Width/2, barn.Z+barn.Height/2
	}
	if !hasVet {
		w, h := walledSide(VetBeds(animals) * vetCellsPerBed)
		if site, ok := u.site(w, h, false, true, cx, cz); ok {
			u.reserve(plan, LayoutReservation{Kind: ReserveVetRoom, Area: site})
		} else {
			slog.Warn("layout: no room for the vet room", "animals", animals, "width", w, "height", h)
		}
	}
}
