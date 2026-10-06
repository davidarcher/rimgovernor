package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Each herd gets a barn and vet area of its own, sized from its ceiling,
// beside the misc unit's; the misc unit is sized as before and never moves.
func TestHerdUnitsAreSizedFromTheirCeilingAndLeaveTheMiscUnitAlone(t *testing.T) {
	misc := herdTestPlan(t, 10)
	if got := misc.herdUnits(); len(got) != 1 || len(got[0].barns) != 1 || len(got[0].vets) != 1 {
		t.Fatal("the misc unit is a barn and a vet area", got)
	}
	if alone := PlanHerdSites(misc, 10); len(alone.Reservations) != len(misc.Reservations) {
		t.Fatal("no herd, no new unit", len(alone.Reservations), len(misc.Reservations))
	}
	grown := PlanHerdSites(misc, 10, HerdCeiling{"A", 30}, HerdCeiling{"B", 12})
	for i, r := range misc.Reservations {
		if grown.Reservations[i] != r {
			t.Fatal("a placed reservation moved", r)
		}
	}
	units := grown.herdUnits()
	if len(units) != 3 {
		t.Fatal("the misc unit and one unit per herd", len(units))
	}
	if !slices.Equal(units[0].vets, misc.herdUnits()[0].vets) || !slices.Equal(units[0].barns, misc.herdUnits()[0].barns) {
		t.Fatal("the misc unit grew", units[0])
	}
	for i, ceiling := range []int{30, 12} {
		unit := units[i+1]
		if grown.capacity(unit.barns, PlannedBarn) < ceiling || grown.capacity(unit.vets, PlannedVetRoom) < VetBeds(ceiling) {
			t.Fatal("the unit holds less than its ceiling", ceiling, grown.capacity(unit.barns, PlannedBarn), grown.capacity(unit.vets, PlannedVetRoom))
		}
		for _, vet := range unit.vets {
			if !slices.ContainsFunc(unit.barns, func(barn Rectangle) bool { _, ok := sharedWallLink(barn, vet); return ok }) {
				t.Fatal("a vet area stands against a barn of its unit", vet)
			}
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
		if more.Reservations[i] != r {
			t.Fatal("a placed reservation moved", r)
		}
	}
}

// A barn's animal flap is the cell beside its own door on the same wall (the
// paddock side): a ring cell that takes a flap, never the door's own cell or
// a corner, and once it stands the ring matches only with that flap.
func TestBarnFlapBesideItsDoor(t *testing.T) {
	plan := PlanHerdSites(herdTestPlan(t, 10), 10, HerdCeiling{"A", 20})
	flaps := plan.flaps()
	if len(flaps) != 2 {
		t.Fatal("one flap per barn", flaps)
	}
	for _, unit := range plan.herdUnits() {
		barn := plan.herdRoom(unit.barns[0], PlannedBarn)
		flap, ok := plan.barnFlap(unit.barns[0])
		if !ok || !slices.Contains(plan.FlapCells(barn), flap) || !slices.Contains(plan.ShellDoors(barn), flap) {
			t.Fatal("the flap is a door cell of the barn's ring", flap, ok)
		}
		if flap == barn.Door || !onRing(flap, unit.barns[0]) {
			t.Fatal("the flap is beside the barn's door, on its ring", flap, barn.Door)
		}
		if dx, dz := flap.X-barn.Door.X, flap.Z-barn.Door.Z; dx*dx+dz*dz != 1 {
			t.Fatal("the flap is the cell next to the door", flap, barn.Door)
		}
		// The barn owes its door and the flap, and none once it stands.
		bare := Reconcile(ReconcileInput{Plan: plan, Room: barn, Ground: GroundOf(nil)})
		var doorIn []domain.Cell
		for _, op := range bare.Ready {
			if op.Kind == OpDoorIn {
				doorIn = op.Cells
			}
		}
		if !slices.Contains(doorIn, flap) || !slices.Contains(doorIn, barn.Door) {
			t.Fatal("the barn owes its door and the flap", doorIn)
		}
		g := ringWalls(plan, barn)
		if !plan.GroundMatches(barn, g) {
			t.Fatal("a barn ringed with its flap stands")
		}
		delete(g.flaps, flap)
		if plan.GroundMatches(barn, g) {
			t.Fatal("a barn with its flap missing does not match")
		}
		g.doors[flap] = true
		if plan.GroundMatches(barn, g) {
			t.Fatal("a plain door is no flap")
		}
	}
}
