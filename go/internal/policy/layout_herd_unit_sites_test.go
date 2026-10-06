package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func unitCells(rs []Rectangle) int {
	n := 0
	for _, r := range rs {
		n += int(r.Width * r.Height)
	}
	return n
}

// Each herd gets a pen, barn and vet area of its own, sized from its ceiling,
// beside the misc unit's; the misc unit is sized as before and never moves.
func TestHerdUnitsAreSizedFromTheirCeilingAndLeaveTheMiscUnitAlone(t *testing.T) {
	misc := herdTestPlan(t, 10)
	if got := misc.herdUnits(); len(got) != 1 || len(got[0].pens) == 0 || len(got[0].barns) != 1 || len(got[0].vets) != 1 {
		t.Fatal("the misc unit is a pen, a barn and a vet area", got)
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
	if !slices.Equal(units[0].pens, misc.herdUnits()[0].pens) || !slices.Equal(units[0].barns, misc.herdUnits()[0].barns) {
		t.Fatal("the misc unit grew", units[0])
	}
	for i, ceiling := range []int{30, 12} {
		unit := units[i+1]
		if unitCells(unit.pens) < ceiling*penCellsPerAnimal || grown.capacity(unit.barns, PlannedBarn) < ceiling || grown.capacity(unit.vets, PlannedVetRoom) < VetBeds(ceiling) {
			t.Fatal("the unit holds less than its ceiling", ceiling, unitCells(unit.pens), grown.capacity(unit.barns, PlannedBarn), grown.capacity(unit.vets, PlannedVetRoom))
		}
		for _, barn := range unit.barns {
			if !slices.ContainsFunc(unit.pens, func(pen Rectangle) bool { _, ok := sharedWallLink(pen, barn); return ok }) {
				t.Fatal("a barn stands against a pen of its unit", barn)
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

// The wall a pen shares with its barn is the barn's: a flap in the middle of
// the shared run, a wall elsewhere, never a fence or a gate.
func TestPenBarnSharedWallIsAFlapOrWallNeverAFence(t *testing.T) {
	plan := PlanHerdSites(herdTestPlan(t, 10), 10, HerdCeiling{"A", 20})
	flaps := plan.flaps()
	if len(flaps) != 2 {
		t.Fatal("one flap per unit", flaps)
	}
	for _, unit := range plan.herdUnits() {
		pen, barn := plan.herdRoom(unit.pens[0], PlannedPen), plan.herdRoom(unit.barns[0], PlannedBarn)
		link, _ := sharedWallLink(unit.pens[0], unit.barns[0])
		if !slices.Contains(plan.FlapCells(barn), link) || !slices.Contains(plan.ShellDoors(barn), link) || plan.FlapCells(pen) != nil || slices.Contains(plan.ShellDoors(pen), link) {
			t.Fatal("the flap is the barn's door cell and no gate of the pen", link)
		}
		if barn.Door == link || pen.Door == link || !plan.sharedRing(pen)[link] {
			t.Fatal("the flap is no room's own door, and a shared pen cell", link, barn.Door, pen.Door)
		}
		shared := plan.sharedRing(pen)
		ring := roomWalls(pen)
		for c := range shared {
			if !onRing(c, ring) || !onRing(c, unit.barns[0]) {
				t.Fatal("shared cell is not on both rings", c)
			}
		}
		// A pen fenced everywhere but the barn's wall matches; a fence there
		// is no part of the plan, and the barn's wall is not the pen's to raise.
		built := penRing(t, plan, pen)
		if !plan.GroundMatches(pen, GroundOf(built)) {
			t.Fatal("a pen ringed but for the barn's wall stands")
		}
		rec := Reconcile(penInput(plan, PenStep{Room: pen}, nil))
		for _, op := range rec.Owed {
			if op.Kind == OpWallIn && slices.ContainsFunc(op.Cells, func(c domain.Cell) bool { return shared[c] }) {
				t.Fatal("the pen fences the barn's wall", op.Cells)
			}
		}
		// The barn owes the flap with its other door, and none once it stands.
		bare := Reconcile(ReconcileInput{Plan: plan, Room: barn, Ground: GroundOf(nil)})
		var doorIn []domain.Cell
		for _, op := range bare.Ready {
			if op.Kind == OpDoorIn {
				doorIn = op.Cells
			}
		}
		_, people := plan.penBarnOpenings()
		var door domain.Cell
		for _, c := range people {
			if onRing(c, unit.barns[0]) && onRing(c, unit.pens[0]) {
				door = c
			}
		}
		if door == link || !plan.sharedRing(pen)[door] || slices.Contains(plan.FlapCells(barn), door) || !slices.Contains(plan.ShellDoors(barn), door) || slices.Contains(plan.ShellDoors(pen), door) {
			t.Fatal("beside the flap is a colonist door of the barn", link, door)
		}
		if !slices.Contains(doorIn, link) || !slices.Contains(doorIn, door) || !slices.Contains(doorIn, barn.Door) {
			t.Fatal("the barn owes its door, the pen door and the flap", doorIn)
		}
		g := ringWalls(plan, barn)
		if !plan.GroundMatches(barn, g) {
			t.Fatal("a barn ringed with its flap stands")
		}
		delete(g.flaps, link)
		if plan.GroundMatches(barn, g) {
			t.Fatal("a barn with its flap missing does not match")
		}
		g.doors[link] = true
		if plan.GroundMatches(barn, g) {
			t.Fatal("a plain door is no flap")
		}
		g = ringWalls(plan, barn)
		delete(g.doors, door)
		if plan.GroundMatches(barn, g) {
			t.Fatal("a barn with no pen door does not match")
		}
	}
}
