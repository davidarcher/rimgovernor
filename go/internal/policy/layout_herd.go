package policy

import (
	"log/slog"
	"slices"
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
			slog.Warn("layout: no room for the animal pen", "animals", animals, "width", w, "height", h)
			return
		}
		u.reserve(plan, LayoutReservation{Kind: ReservePen, Area: site})
		if !hasPen {
			cx, cz = site.X+site.Width/2, site.Z+site.Height/2
		}
	}
	// A barn holds a sleeping spot per animal and a vet room VetBeds of
	// them; a herd the rooms outgrow gets another beside the first.
	if short := animals - plan.herdCapacity(ModuleBarn); short > 0 {
		beds := short
		if !hasBarn {
			beds = max(beds, herdBarnMinBeds)
		}
		w, h := walledSide(herdSide(beds))
		site, ok := u.site(w, h, false, false, cx, cz)
		if !ok {
			slog.Warn("layout: no room for the barn", "animals", animals, "width", w, "height", h)
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
	if short := VetBeds(animals) - plan.herdCapacity(ModuleVetRoom); short > 0 {
		w, h := walledSide(herdSide(short))
		if site, ok := u.site(w, h, false, false, cx, cz); ok {
			u.reserve(plan, LayoutReservation{Kind: ReserveVetRoom, Area: site})
		} else {
			slog.Warn("layout: no room for the vet room", "animals", animals, "width", w, "height", h)
		}
	}
}
