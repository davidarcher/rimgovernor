package policy

import (
	"fmt"
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
	step := NextThroneStep(plan, standing, nil, need, defs, nil)
	if step.Kind != ThronePlace || step.Piece.Slot != throneSlot {
		t.Fatalf("throne first: %+v", step)
	}
	built := []CurrentBuilding{standingThrone(t, step.Piece)}
	need.Titled = true
	owned := []RoyalThrone{{ID: "Throne_1", Def: "Throne", Owner: need.Holder}}
	// Each missing piece is planned from the first available definition of
	// its any-of list, until the room holds them all.
	counts := map[string]int{}
	for i := 0; i < 5; i++ {
		step = NextThroneStep(plan, standing, built, need, defs, owned)
		if step.Kind != ThronePlace {
			t.Fatalf("round %d: %+v", i, step)
		}
		if !rectInside(room.Interior, step.Piece.Rect) {
			t.Fatalf("piece outside the room: %+v", step.Piece)
		}
		counts[step.Piece.Def]++
		b, err := domain.NewBuilding(step.Piece.Def, step.Piece.Anchor(), step.Piece.Rot, "")
		if err != nil {
			t.Fatal(err)
		}
		built = append(built, CurrentBuilding{ID: fmt.Sprintf("p%d", i), Building: b, Cells: rectCells(step.Piece.Rect)})
	}
	if counts["Brazier"] != 2 || counts["Column"] != 2 || counts["Piano"] != 1 || counts["Torch"] != 0 {
		t.Fatalf("placed %v", counts)
	}
	if step = NextThroneStep(plan, standing, built, need, defs, owned); step.Kind != ThroneNone {
		t.Fatalf("room complete: %+v", step)
	}
	// Lose one column: only it is planned again.
	var rest []CurrentBuilding
	dropped := false
	for _, b := range built {
		if b.Building.Definition() == "Column" && !dropped {
			dropped = true
			continue
		}
		rest = append(rest, b)
	}
	if step = NextThroneStep(plan, standing, rest, need, defs, owned); step.Kind != ThronePlace || step.Piece.Def != "Column" {
		t.Fatalf("missing column: %+v", step)
	}
}
