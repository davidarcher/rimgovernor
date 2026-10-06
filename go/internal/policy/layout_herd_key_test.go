package policy

import (
	"slices"
	"testing"
)

// herdKeys are the Herd keys of the plan's herd reservations, in plan order.
func herdKeys(p LayoutPlan) []string {
	var out []string
	for _, r := range p.Reservations {
		if _, ok := herdRole(r.Kind); ok {
			out = append(out, r.Herd)
		}
	}
	return out
}

// A herd's second unit belongs to that herd by key (#2226): with units [misc,
// A, A2], a new herd B sorting after A founds its own unit and does not take
// A2, and A does not found a third unit.
func TestHerdKeyKeepsASecondUnitWithItsHerd(t *testing.T) {
	misc := herdTestPlan(t, 10)
	withA := PlanHerdSites(misc, 10, HerdCeiling{"A", 30})
	for _, r := range withA.Reservations[len(misc.Reservations):] {
		if r.Herd != "A" {
			t.Fatal("a herd's reservations carry its key", r)
		}
	}
	for _, r := range misc.Reservations {
		if r.Herd != "" {
			t.Fatal("the misc unit is unkeyed", r)
		}
	}
	// A boxed-in unit founds a second one (#2212): key both units A, as a herd
	// that founded its second unit is, so the plan holds [A, A2].
	grown := PlanHerdSites(herdTestPlan(t, 10), 30)
	if len(grown.herdUnits()) != 2 {
		t.Fatal("the unit is boxed in and founds a second", len(grown.herdUnits()))
	}
	for i, r := range grown.Reservations {
		if _, ok := herdRole(r.Kind); ok {
			grown.Reservations[i].Herd = "A"
		}
	}
	a := HerdCeiling{"A", 30}
	if again := PlanHerdSites(grown, 0, a); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("A does not found a third unit")
	}
	withB := PlanHerdSites(grown, 0, a, HerdCeiling{"B", 12})
	units := withB.herdUnits()
	if len(units) != 3 {
		t.Fatal("B founds a unit of its own and A no third", len(units))
	}
	if units[2].herd != "B" {
		t.Fatal("the new unit is B's", units[2].herd)
	}
	for _, r := range grown.Reservations {
		if !slices.Contains(withB.Reservations, r) {
			t.Fatal("a placed reservation moved or changed", r)
		}
	}
	if again := PlanHerdSites(withB, 0, a, HerdCeiling{"B", 12}); len(again.Reservations) != len(withB.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
}

// A plan saved before keys is matched by order once, and the next top-up keys
// it (#2226).
func TestHerdKeysMigrateAPlanSavedWithoutThem(t *testing.T) {
	herds := []HerdCeiling{{"A", 30}, {"B", 12}}
	keyed := PlanHerdSites(herdTestPlan(t, 10), 10, herds...)
	legacy := keyed
	legacy.Reservations = slices.Clone(keyed.Reservations)
	for i := range legacy.Reservations {
		legacy.Reservations[i].Herd = ""
	}
	housed, isLegacy := legacy.housedUnits(newUtilityGrid(legacy), herds)
	if !isLegacy || len(housed) != 3 || len(housed[1].pens) == 0 || len(housed[2].pens) == 0 {
		t.Fatal("units match by order", isLegacy, housed)
	}
	next := PlanHerdSites(legacy, 10, herds...)
	if len(next.Reservations) != len(legacy.Reservations) || !slices.Equal(herdKeys(next), herdKeys(keyed)) {
		t.Fatal("the top-up keys the plan as a fresh one", herdKeys(next), herdKeys(keyed))
	}
}
