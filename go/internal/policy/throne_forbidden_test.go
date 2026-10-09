package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestThroneForbiddenClassIsPacked: a building of a forbidden
// class inside the room is packed by the reconcile instead of blocking the step,
// one outside is left alone, and planning never places a forbidden throne
// definition.
func TestThroneForbiddenClassIsPacked(t *testing.T) {
	plan, room, need := throneFixture()
	need.ForbiddenDefs = []string{"Bed", "TableButcher"}
	defs := throneDefs(Bounds{Width: 1, Height: 1})
	ground := ringWalls(plan, room)
	build := func(def string, cell domain.Cell) CurrentBuilding {
		b, err := domain.NewBuilding(def, cell, domain.South, "")
		if err != nil {
			t.Fatal(err)
		}
		return CurrentBuilding{ID: def + "_7", Building: b, Cells: []domain.Cell{cell}}
	}
	inside, outside := build("TableButcher", domain.Cell{X: 11, Z: 21}), build("Bed", domain.Cell{X: 40, Z: 40})
	step := NextThroneStep(plan, tombStanding(room), ground, []CurrentBuilding{outside, inside}, need, defs, nil)
	if step.Kind != ThroneReconcile || !step.Owed() || step.Failed() {
		t.Fatalf("intruder in the room: %+v", step)
	}
	// The reconcile packs the intruder and builds the throne alongside.
	row := playerRow("TableButcher_7", "TableButcher", "other", domain.Cell{X: 11, Z: 21}, domain.Cell{X: 11, Z: 21}, false)
	row.Packable = true
	in := ReconcileInput{Plan: plan, Room: room, Ground: ground, Rooms: tombStanding(room), Furniture: step.Template, Rows: OwnRows([]ClearanceTarget{row}, step.Template, need.Forbids)}
	var kinds []OpKind
	for _, op := range ReconcileRoom(in) {
		kinds = append(kinds, op.Kind)
	}
	if !kindsEqual(kinds, OpPack, OpBuild) {
		t.Fatalf("pack the intruder: %v", kinds)
	}
	if step := NextThroneStep(plan, tombStanding(room), ground, []CurrentBuilding{outside}, need, defs, nil); step.Kind != ThroneReconcile || len(step.Template) != 1 {
		t.Fatalf("a bed outside the room is no intrusion: %+v", step)
	}
	// A throne definition of a forbidden class is never placed; it fails by name.
	need.ForbiddenDefs = []string{"Throne"}
	if step := NextThroneStep(plan, tombStanding(room), ground, nil, need, defs, nil); step.Kind != ThroneUnavailable {
		t.Fatalf("forbidden throne placed: %+v", step)
	}
}
