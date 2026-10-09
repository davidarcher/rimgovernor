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
	core := corePlan(utilityTestZones(), 3, TechTierCamp)
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
	core := corePlan(utilityTestZones(), 3, TechTierCamp)
	small := herdReservations(PlanUtilities(core, UtilityWants{PenAnimals: 10}), ReserveVetRoom)
	large := herdReservations(PlanUtilities(core, UtilityWants{PenAnimals: 45}), ReserveVetRoom)
	if len(small) != 1 || len(large) != 1 || VetRoomBeds(large[0].Area) < VetBeds(45) || VetRoomBeds(large[0].Area) <= VetRoomBeds(small[0].Area) {
		t.Fatal("vet room does not scale", small, large)
	}
}

// A unit boxed in by the core has no room beside it: the outgrown herd founds
// one second unit (barn and vet area), the nearest unit takes it as its
// own, and a repeat top-up is a no-op (#2212).
func TestBoxedInUnitFoundsASecondUnit(t *testing.T) {
	small := boxedInHerdPlan(t, 10)
	grown := PlanHerdSites(small, 30)
	if groups := grown.herdUnits(); len(groups) != 2 {
		t.Fatal("want the first unit and one second unit", len(groups))
	}
	if housed := grown.housedUnits(nil); len(housed) != 1 || len(housed[0].barns) != 2 || len(housed[0].vets) != 2 {
		t.Fatal("the second unit is the first one's overflow", housed)
	}
	if grown.herdCapacity(PlannedBarn) < 30 || grown.herdCapacity(PlannedVetRoom) < VetBeds(30) {
		t.Fatal("the herd is not housed", grown.herdCapacity(PlannedBarn), grown.herdCapacity(PlannedVetRoom))
	}
	if again := PlanHerdSites(grown, 30); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
	// A bigger herd grows the second unit or adds one, never a unit per animal.
	more := PlanHerdSites(grown, 31)
	if len(more.herdUnits()) > 3 {
		t.Fatal("a unit per animal", len(more.herdUnits()))
	}
	for i, r := range small.Reservations {
		if grown.Reservations[i] != r {
			t.Fatal("a placed reservation moved", r)
		}
	}
}

// No unit stands without both parts: every barn has a vet area against it,
// however the herd outgrows its rooms.
func TestUnitsAreAlwaysBarnAndVet(t *testing.T) {
	for _, animals := range []int{10, 30, 31, 60} {
		p := herdTestPlan(t, 10)
		p = PlanHerdSites(p, animals)
		for k, g := range p.herdUnits() {
			if len(g.barns) == 0 || len(g.vets) == 0 {
				t.Fatal("a unit lacks a part", animals, k, len(g.barns), len(g.vets))
			}
		}
	}
	// A second unit that cannot fit whole is left out, not founded in parts.
	p := herdTestPlan(t, 10)
	p = PlanHerdSites(p, 5000)
	for k, g := range p.herdUnits() {
		if len(g.barns) == 0 || len(g.vets) == 0 {
			t.Fatal("a detached part", k, len(g.barns), len(g.vets))
		}
	}
}

func TestTopUpAddsABarnBesideTheUnit(t *testing.T) {
	small := herdTestPlan(t, 10)
	// The plan outgrows its barn: a second barn is added, nothing moves.
	grown := PlanHerdSites(small, 30)
	if units := grown.herdUnits(); len(units) != 1 {
		t.Fatal("the extra reservations stand beside the unit", len(units))
	}
	if len(herdReservations(grown, ReserveBarn)) != 2 {
		t.Fatal("no extra barn", len(herdReservations(grown, ReserveBarn)))
	}
	for i, r := range small.Reservations {
		if grown.Reservations[i] != r {
			t.Fatal("a placed reservation moved", r)
		}
	}
	if grown.herdCapacity(PlannedBarn) < 30 || grown.herdCapacity(PlannedVetRoom) < VetBeds(30) {
		t.Fatal("barn and vet beds follow the herd", grown.herdCapacity(PlannedBarn))
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
