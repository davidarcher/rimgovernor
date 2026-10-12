package policy

import (
	"slices"
	"testing"
)

// Each herd gets a barn of its own, sized from its ceiling,
// beside the misc unit's; the misc unit is sized as before and never moves.
func TestHerdUnitsAreSizedFromTheirCeilingAndLeaveTheMiscUnitAlone(t *testing.T) {
	misc := herdTestPlan(t, 10)
	if got := misc.herdUnits(); len(got) != 1 || len(got[0].barns) != 1 {
		t.Fatal("the misc unit is a barn", got)
	}
	if alone := PlanHerdSites(misc, 10); len(alone.Reservations) != len(misc.Reservations) {
		t.Fatal("no herd, no new unit", len(alone.Reservations), len(misc.Reservations))
	}
	grown := PlanHerdSites(misc, 10, HerdCeiling{"A", 30}, HerdCeiling{"B", 12})
	for i, r := range misc.Reservations {
		if !grown.Reservations[i].Equal(r) {
			t.Fatal("a placed reservation moved", r)
		}
	}
	units := grown.herdUnits()
	if len(units) != 3 {
		t.Fatal("the misc unit and one unit per herd", len(units))
	}
	if !slices.Equal(units[0].barns, misc.herdUnits()[0].barns) {
		t.Fatal("the misc unit grew", units[0])
	}
	for i, ceiling := range []int{30, 12} {
		unit := units[i+1]
		if grown.capacity(unit.barns, PlannedBarn) < ceiling {
			t.Fatal("the unit holds less than its ceiling", ceiling, grown.capacity(unit.barns, PlannedBarn))
		}
	}
	if again := PlanHerdSites(grown, 10, HerdCeiling{"A", 30}, HerdCeiling{"B", 12}); len(again.Reservations) != len(grown.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
	// A herd that outgrows its unit gets another reservation of the same kind
	// beside it, and nothing placed moves.
	more := PlanHerdSites(grown, 10, HerdCeiling{"A", 30}, HerdCeiling{"B", 40})
	if len(more.herdUnits()) != 3 || more.capacity(more.herdUnits()[2].barns, PlannedBarn) < 40 {
		t.Fatal("an outgrown herd adds to its own unit", len(more.herdUnits()))
	}
	for i, r := range grown.Reservations {
		if !more.Reservations[i].Equal(r) {
			t.Fatal("a placed reservation moved", r)
		}
	}
}
