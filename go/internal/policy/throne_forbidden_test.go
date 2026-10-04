package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// TestThroneForbiddenClassReportsANamedFailure (#1865): a building of a
// forbidden class inside the standing room blocks the step with a failure
// naming it, one outside does not, and planning never places a forbidden
// throne definition.
func TestThroneForbiddenClassReportsANamedFailure(t *testing.T) {
	plan, room, need := throneFixture()
	need.ForbiddenDefs = []string{"Bed", "TableButcher"}
	defs := throneDefs(Bounds{Width: 1, Height: 1})
	build := func(def string, cell domain.Cell) CurrentBuilding {
		b, err := domain.NewBuilding(def, cell, domain.South, "")
		if err != nil {
			t.Fatal(err)
		}
		return CurrentBuilding{ID: def + "_7", Building: b, Cells: []domain.Cell{cell}}
	}
	inside, outside := build("TableButcher", domain.Cell{X: 11, Z: 21}), build("Bed", domain.Cell{X: 40, Z: 40})
	step := NextThroneStep(plan, tombStanding(room), []CurrentBuilding{outside, inside}, need, defs, nil)
	if step.Kind != ThroneBlocked || step.Owed() || len(step.Intruders) != 1 || step.Intruders[0].ID != "TableButcher_7" {
		t.Fatalf("intruder in the room: %+v", step)
	}
	if got := step.Detail(); !strings.Contains(got, "Knight") || !strings.Contains(got, "TableButcher TableButcher_7") {
		t.Errorf("failure does not name the intruder: %q", got)
	}
	if step := NextThroneStep(plan, tombStanding(room), []CurrentBuilding{outside}, need, defs, nil); step.Kind != ThronePlace {
		t.Fatalf("a bed outside the room is no intrusion: %+v", step)
	}
	// A throne definition of a forbidden class is never placed; it fails by name (#1874).
	need.ForbiddenDefs = []string{"Throne"}
	if step := NextThroneStep(plan, tombStanding(room), nil, need, defs, nil); step.Kind != ThroneUnavailable {
		t.Fatalf("forbidden throne placed: %+v", step)
	}
}
