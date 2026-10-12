package policy

import "testing"

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

func TestHerdSitesABarnAndNoVetRoom(t *testing.T) {
	core := corePlan(utilityTestZones(), 3, TechTierCamp)
	p := PlanUtilities(core, UtilityWants{PenAnimals: 20})
	barns := herdReservations(p, ReserveBarn)
	if len(barns) != 1 {
		t.Fatal("want one barn", len(barns))
	}
	if barns[0].Area.Bounds().Width < 7 || barns[0].Area.Bounds().Height < 7 {
		t.Fatal("barn below the minimum", barns[0].Area)
	}
	found := map[string]bool{}
	for _, l := range p.Overlay(Bounds{Width: 400, Height: 400}).Layers {
		found[l.Label] = true
	}
	if !found["barn"] || found["vet room"] {
		t.Fatal("overlay lacks the barn or still draws a vet room", found)
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
		if !grown.Reservations[i].Equal(r) {
			t.Fatal("a placed reservation moved", r)
		}
	}
	if grown.herdCapacity(PlannedBarn) < 30 {
		t.Fatal("barn beds follow the herd", grown.herdCapacity(PlannedBarn))
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
