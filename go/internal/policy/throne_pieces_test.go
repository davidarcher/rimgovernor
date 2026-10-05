package policy

import (
	"fmt"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestThroneRoomMissingPiecesPlansThem(t *testing.T) {
	plan, room, need := throneFixture()
	need.AnyOfCounts = []ThingAnyOfCount{{Things: []string{"Torch", "Brazier"}, Count: 2}}
	need.Counts = []ThingCount{{Def: "Column", Count: 2}}
	need.AnyOf = [][]string{{"Harp", "Piano"}}
	one := domain.Known(Bounds{Width: 1, Height: 1})
	defs := append(throneDefs(Bounds{Width: 1, Height: 1}),
		FurnitureDefinition{Name: "Torch", Available: domain.Known(false), Size: one},
		FurnitureDefinition{Name: "Brazier", Available: domain.Known(true), Size: one},
		FurnitureDefinition{Name: "Column", Available: domain.Known(true), Size: one},
		FurnitureDefinition{Name: "Piano", Available: domain.Known(true), Size: domain.Known(Bounds{Width: 2, Height: 1})})
	standing := tombStanding(room)
	ground := ringWalls(plan, room)
	rect := func(p WantedPiece) Rectangle {
		return Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: p.Maximum.X - p.Minimum.X + 1, Height: p.Maximum.Z - p.Minimum.Z + 1}
	}
	// The template wants every missing piece at once, each from the first
	// available definition of its any-of list.
	step := NextThroneStep(plan, standing, ground, nil, need, defs, nil)
	counts := map[string]int{}
	for _, p := range step.Template {
		counts[p.DefName]++
		if !rectInside(room.Interior, rect(p)) {
			t.Fatalf("piece outside the room: %+v", p)
		}
	}
	if step.Kind != ThroneReconcile || counts["Throne"] != 1 || counts["Brazier"] != 2 || counts["Column"] != 2 || counts["Piano"] != 1 || counts["Torch"] != 0 {
		t.Fatalf("template %v: %+v", counts, step)
	}
	// The throne and the pieces stand: the room is complete.
	var built []CurrentBuilding
	for i, p := range step.Template {
		b, err := domain.NewBuilding(p.DefName, p.Anchor(), p.Rot, "")
		if err != nil {
			t.Fatal(err)
		}
		built = append(built, CurrentBuilding{ID: fmt.Sprintf("p%d", i), Building: b, Cells: rectCells(rect(p))})
	}
	need.Titled = true
	owned := []RoyalThrone{{ID: "p0", Def: "Throne", Owner: need.Holder}}
	if step = NextThroneStep(plan, standing, ground, built, need, defs, owned); step.Kind != ThroneNone {
		t.Fatalf("room complete: %+v", step)
	}
	// Lose one column: the standing pieces stay in the template where they
	// stand and only the lost column is built.
	var rest []CurrentBuilding
	dropped := false
	for _, b := range built {
		if b.Building.Definition() == "Column" && !dropped {
			dropped = true
			continue
		}
		rest = append(rest, b)
	}
	step = NextThroneStep(plan, standing, ground, rest, need, defs, owned)
	columns := 0
	for _, p := range step.Template {
		if p.DefName == "Column" {
			columns++
		}
	}
	if step.Kind != ThroneReconcile || columns != 2 || len(step.Template) != len(built) {
		t.Fatalf("missing column: %+v", step)
	}
	in := ReconcileInput{Plan: plan, Room: room, Ground: ground, Rooms: standing, Furniture: step.Template}
	for i, b := range rest {
		r := cellsRectangle(b.Cells)
		in.Rows = append(in.Rows, playerRow(fmt.Sprintf("p%d", i), b.Building.Definition(), "other", domain.Cell{X: r.X, Z: r.Z}, domain.Cell{X: r.X + r.Width - 1, Z: r.Z + r.Height - 1}, false))
	}
	ops := ReconcileRoom(in)
	if len(ops) != 1 || ops[0].Kind != OpBuild || len(ops[0].Pieces) != 1 || ops[0].Pieces[0].DefName != "Column" {
		t.Fatalf("only the lost column is built: %+v", ops)
	}
}

func TestThroneRequirementWithoutAnAvailableDefFailsByName(t *testing.T) {
	plan, room, need := throneFixture()
	need.Counts = []ThingCount{{Def: "Column", Count: 2}}
	need.AnyOf = [][]string{{"Harp", "Piano"}}
	one := domain.Known(Bounds{Width: 1, Height: 1})
	defs := append(throneDefs(Bounds{Width: 1, Height: 1}),
		FurnitureDefinition{Name: "Column", Available: domain.Known(false), Size: one},
		FurnitureDefinition{Name: "Piano", Available: domain.Known(true), Size: domain.Unknown[Bounds]()})
	step := NextThroneStep(plan, tombStanding(room), ringWalls(plan, room), nil, need, defs, nil)
	if step.Kind != ThroneUnavailable || step.Owed() || !step.Failed() || len(step.Missing) != 2 || step.Missing[0] != "Column" || step.Missing[1] != "Harp or Piano" {
		t.Fatalf("unavailable requirements: %+v", step)
	}
	if d := step.Detail(); !strings.Contains(d, "Knight") || !strings.Contains(d, "Column; Harp or Piano") {
		t.Fatalf("detail: %s", d)
	}
}
