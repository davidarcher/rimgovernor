package policy

import (
	"slices"
	"testing"
)

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
	grown := PlanHerdSites(boxedInHerdPlan(t, 10), 30)
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
