package policy

import "testing"

// VetRoomBeds is the animal beds a vet room reservation holds.
func VetRoomBeds(area Rectangle) int {
	return herdGridBeds(max(area.Width-2, 0), max(area.Height-2, 0))
}

func herdReservations(p LayoutPlan, kind ReservationKind) []LayoutReservation {
	var out []LayoutReservation
	for _, r := range p.Reservations {
		if r.Kind == kind {
			out = append(out, r)
		}
	}
	return out
}

func penCells(p LayoutPlan) int {
	n := 0
	for _, r := range herdReservations(p, ReservePen) {
		n += int(r.Area.Width * r.Area.Height)
	}
	return n
}

func TestPenAnimalsFollowsThePlanCeilings(t *testing.T) {
	plan := HerdPlan{Policy: HerdPolicy{PopulationMax: map[Resource]int64{"Cow": 14, "Alpaca": 8}}}
	if got := plan.PenAnimals(); got != 22 {
		t.Fatal("sum of ceilings", got)
	}
	if got := (HerdPlan{}).PenAnimals(); got != penAnimalsFloor {
		t.Fatal("no plan sizes the floor", got)
	}
}

func TestHerdSitesBarnAndVetRoom(t *testing.T) {
	core := corePlan(utilityTestZones(), 3, BuildTierCamp)
	p := PlanUtilities(core, UtilityWants{PenAnimals: 20})
	barns, vets := herdReservations(p, ReserveBarn), herdReservations(p, ReserveVetRoom)
	if len(barns) != 1 || len(vets) != 1 {
		t.Fatal("want one barn and one vet room", len(barns), len(vets))
	}
	if barns[0].Area.Width < 7 || barns[0].Area.Height < 7 {
		t.Fatal("barn below the minimum", barns[0].Area)
	}
	vet := vets[0].Area
	if VetRoomBeds(vet) < 2 {
		t.Fatal("vet room holds fewer than two beds", vet, VetRoomBeds(vet))
	}
	for _, r := range p.Reservations {
		// The barn's wall is the one thing the vet room may share.
		if _, shared := sharedWallLink(r.Area, vet); r.Kind != ReserveVetRoom && rectsOverlap(r.Area, vet) && !(r.Kind == ReserveBarn && shared) {
			t.Fatal("vet room overlaps", r.Kind)
		}
	}
	for _, a := range p.AnimalAreas() {
		if _, shared := sharedWallLink(a, vet); rectsOverlap(a, vet) && !shared {
			t.Fatal("vet room is inside an animal area", a)
		}
	}
	if len(p.AnimalAreas()) != len(herdReservations(p, ReservePen))+1 {
		t.Fatal("animal areas are the pens and the barn")
	}
	found := map[string]bool{}
	for _, l := range p.Overlay(Bounds{Width: 400, Height: 400}).Layers {
		found[l.Label] = true
	}
	if !found["barn"] || !found["vet room"] {
		t.Fatal("overlay lacks the barn or vet room", found)
	}
}

func TestVetBedsScaleWithTheHerd(t *testing.T) {
	if VetBeds(0) != 2 || VetBeds(10) != 2 {
		t.Fatal("at least two beds", VetBeds(0), VetBeds(10))
	}
	if VetBeds(45) <= VetBeds(10) {
		t.Fatal("more beds for a bigger herd", VetBeds(45))
	}
	core := corePlan(utilityTestZones(), 3, BuildTierCamp)
	small := herdReservations(PlanUtilities(core, UtilityWants{PenAnimals: 10}), ReserveVetRoom)
	large := herdReservations(PlanUtilities(core, UtilityWants{PenAnimals: 45}), ReserveVetRoom)
	if len(small) != 1 || len(large) != 1 || VetRoomBeds(large[0].Area) < VetBeds(45) || VetRoomBeds(large[0].Area) <= VetRoomBeds(small[0].Area) {
		t.Fatal("vet room does not scale", small, large)
	}
}

func TestLargerHerdLargerPenAndTopUpAddsAPen(t *testing.T) {
	core := corePlan(utilityTestZones(), 3, BuildTierCamp)
	small := PlanUtilities(core, UtilityWants{PenAnimals: 10})
	large := PlanUtilities(core, UtilityWants{PenAnimals: 30})
	if penCells(large) <= penCells(small) || penCells(small) < 10*penCellsPerAnimal {
		t.Fatal("pen does not follow the herd", penCells(small), penCells(large))
	}
	// The plan outgrows its pen: a second pen is added, nothing moves.
	grown := PlanHerdSites(small, 30)
	if len(herdReservations(grown, ReservePen)) != 2 || penCells(grown) < 30*penCellsPerAnimal {
		t.Fatal("no extra pen", len(herdReservations(grown, ReservePen)), penCells(grown))
	}
	for i, r := range small.Reservations {
		if grown.Reservations[i] != r {
			t.Fatal("a placed reservation moved", r)
		}
	}
	if grown.herdCapacity(ModuleBarn) < 30 || grown.herdCapacity(ModuleVetRoom) < VetBeds(30) {
		t.Fatal("barn and vet beds follow the herd", grown.herdCapacity(ModuleBarn))
	}
	if again := PlanHerdSites(grown, 30); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
	if shrunk := PlanHerdSites(grown, 4); len(shrunk.Reservations) != len(grown.Reservations) {
		t.Fatal("a smaller herd removes nothing")
	}
	if none := PlanHerdSites(small, 0); len(none.Reservations) != len(small.Reservations) {
		t.Fatal("zero animals plans nothing")
	}
}
