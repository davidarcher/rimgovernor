package policy

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// everywhereGrazes is grazing ground over the whole test map.
func everywhereGrazes() map[domain.Cell]bool {
	out := map[domain.Cell]bool{}
	for x := int32(0); x < 400; x++ {
		for z := int32(0); z < 400; z++ {
			out[domain.Cell{X: x, Z: z}] = true
		}
	}
	return out
}

func TestPenSideHoldsItsCells(t *testing.T) {
	for _, cells := range []int{1, 16, 17, 100, 900} {
		w, h := penSide(cells)
		if int(w*h) < cells || w < 1 || h < 1 || int(w*h) > cells+int(w) {
			t.Fatal("pen side", cells, w, h)
		}
	}
}

// A pen shares one wall with its barn, stands wholly on grazing ground, and the
// flap joining them is the barn's wall cell on the shared wall.
func TestPlanPenSitesPlacesAPenBesideTheBarn(t *testing.T) {
	core := herdTestPlan(t, 10)
	plan := PlanPenSites(core, everywhereGrazes(), PenWant{Cells: 40})
	pens := plan.HerdRooms(PlannedPen)
	if len(pens) != 1 {
		t.Fatal("one pen", len(pens))
	}
	if plan.penCells("") < 40 {
		t.Fatal("the pen holds its cells", plan.penCells(""))
	}
	if owed := PensOwed(plan, []PenWant{{Cells: 40}}); owed != 0 {
		t.Fatal("nothing owed once placed", owed)
	}
	if owed := PensOwed(plan, []PenWant{{Cells: 400}}); owed <= 0 {
		t.Fatal("a grown want is owed", owed)
	}
	flaps := plan.flaps()
	if len(flaps) != 1 {
		t.Fatal("one flap per pen", flaps)
	}
	barn := plan.HerdRooms(PlannedBarn)[0]
	pen := pens[0]
	if !slices.Contains(plan.FlapCells(barn), flaps[0]) || len(plan.FlapCells(pen)) != 0 {
		t.Fatal("the flap is a cell of the barn's ring", flaps)
	}
	if !pen.Outdoor {
		t.Fatal("a pen is an outdoor room")
	}
	if again := PlanPenSites(plan, everywhereGrazes(), PenWant{Cells: 40}); len(again.Reservations) != len(plan.Reservations) {
		t.Fatal("top-up is not idempotent")
	}
	for i, r := range core.Reservations {
		if !plan.Reservations[i].Equal(r) {
			t.Fatal("a placed reservation moved", r)
		}
	}
}

func TestPlanPenSitesNeedsGrazingGround(t *testing.T) {
	core := herdTestPlan(t, 10)
	if got := PlanPenSites(core, map[domain.Cell]bool{}, PenWant{Cells: 40}); len(got.Reservations) != len(core.Reservations) {
		t.Fatal("no grazing, no pen")
	}
	if got := PlanPenSites(core, everywhereGrazes(), PenWant{Race: "Nobody", Cells: 40}); len(got.Reservations) != len(core.Reservations) {
		t.Fatal("a unit without a barn gets no pen")
	}
}

// The pen owes its marker and ring, and stands once both do.
func TestHerdStepRaisesAPenAndPensStand(t *testing.T) {
	plan := PlanPenSites(herdTestPlan(t, 10), everywhereGrazes(), PenWant{Cells: 40})
	pen := plan.HerdRooms(PlannedPen)[0]
	f := testHerdFurniture
	f.Marker = InteriorPieceDef{Def: PenMarkerDefinition, Size: domain.Cell{X: 1, Z: 1}}
	step := NextHerdStep(plan, RoomObservation{}, GroundOf(nil), nil, nil, 0, 0, f)
	if step.Kind != HerdReconcile || step.Role != PlannedPen || step.Room.ID != pen.ID {
		t.Fatal("a bare pen is reconciled", step)
	}
	if herdTemplateCount(step, PenMarkerDefinition) != 1 {
		t.Fatal("the pen owes one marker", step.Template)
	}
	ground := penGround(plan, pen)
	if !plan.GroundMatches(pen, ground) {
		t.Fatal("a ringed pen matches its ground")
	}
	if PensStand(plan, ground, nil) {
		t.Fatal("a pen without its marker does not stand")
	}
	marker := step.Template[0]
	b, err := domain.NewBuilding(PenMarkerDefinition, marker.Minimum, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	built := []CurrentBuilding{{ID: "m", Building: b, Cells: []domain.Cell{marker.Minimum}}}
	if !PensStand(plan, ground, built) {
		t.Fatal("a ringed pen with its marker stands")
	}
	if PensStand(LayoutPlan{}, ground, built) {
		t.Fatal("no pen, none stand")
	}
	if got := NextHerdStep(plan, RoomObservation{}, ground, built, nil, 0, 0, f); got.Kind == HerdReconcile && got.Role == PlannedPen {
		t.Fatal("a standing pen owes nothing", got)
	}
}

// penGround is the barn's walls and flap standing, with the pen's fence and gate.
func penGround(plan LayoutPlan, pen PlannedRoom) GroundCensus {
	g := herdGround(plan, plan.HerdRooms(PlannedBarn)...)
	g.fences, g.gates = map[domain.Cell]bool{}, map[domain.Cell]bool{}
	gates := map[domain.Cell]bool{}
	for _, d := range plan.ShellDoors(pen) {
		gates[d] = true
	}
	for _, c := range pen.wallRing().Cells() {
		switch {
		case gates[c]:
			g.gates[c] = true
		case !g.walls[c] && !g.flaps[c]:
			g.fences[c] = true
		}
	}
	return g
}
