package policy

import (
	"slices"
	"testing"
)

// A herd's extra barns belong to that herd by key: a new herd B sorting after
// A founds its own unit and does not take A's second barn, and A does not
// found another unit.
func TestHerdKeyKeepsASecondBarnWithItsHerd(t *testing.T) {
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
	a := HerdCeiling{"A", 60}
	grown := PlanHerdSites(withA, 10, a)
	if len(grown.herdUnits()) != 2 {
		t.Fatal("A grows beside itself, never a second unit", len(grown.herdUnits()))
	}
	if again := PlanHerdSites(grown, 10, a); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("A does not found another unit")
	}
	withB := PlanHerdSites(grown, 10, a, HerdCeiling{"B", 12})
	units := withB.herdUnits()
	if len(units) != 3 {
		t.Fatal("B founds a unit of its own and A no other", len(units))
	}
	if units[2].herd != "B" {
		t.Fatal("the new unit is B's", units[2].herd)
	}
	for _, r := range grown.Reservations {
		if !slices.ContainsFunc(withB.Reservations, r.Equal) {
			t.Fatal("a placed reservation moved or changed", r)
		}
	}
	if again := PlanHerdSites(withB, 10, a, HerdCeiling{"B", 12}); len(again.Reservations) != len(withB.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
}
