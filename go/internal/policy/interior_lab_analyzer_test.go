package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The planned lab is sized for two hi-tech benches (5x2) and the analyzer's
// reserved slot, whichever wall its door is on: both benches stand in one row
// on the back wall, the analyzer is within its link distance of each, clear of
// every worker cell and of the entrance row, and a hi-tech bench placed into
// a lab already holding a simple one keeps the row and the analyzer.
func TestPlannedLabHoldsTwoHiTechBenchesAndTheAnalyzerSlot(t *testing.T) {
	size := coreRoomSize[PlannedLab]
	hiTech, _ := testShapes.Get(testFurniture.AdvancedLab)
	if int32(size[0]) < 2*hiTech.Size.X+1 {
		t.Fatalf("lab interior %v cannot hold two %s", size, hiTech.Def)
	}
	for _, standing := range [][]string{nil, {testResearch}, {testFurniture.AdvancedLab}} {
		for _, room := range interiorRoomsAround(RoomRoleLaboratory, size[0], size[1], 3) {
			room.Standing = standing
			plan, ok := PlanInterior(room, hiTech)
			if !ok {
				t.Fatalf("%v standing %v: no plan", room.Doors, standing)
			}
			assertInteriorRegular(t, plan)
			var benches, analyzers []InteriorPiece
			for _, p := range plan.Pieces {
				switch p.Def {
				case testFurniture.AdvancedLab:
					benches = append(benches, p)
				case testFurniture.Analyzer.Def:
					analyzers = append(analyzers, p)
				default:
					t.Fatalf("%v: unexpected %s", room.Doors, p.Def)
				}
			}
			if len(benches) != 2 || len(analyzers) != 1 {
				t.Fatalf("%v standing %v: %d benches %d analyzers, want 2 and 1", room.Doors, standing, len(benches), len(analyzers))
			}
			used := map[domain.Cell]bool{}
			for _, p := range plan.Pieces {
				for _, c := range rectCells(p.Rect) {
					used[c] = true
				}
			}
			for _, b := range benches {
				if c, ok := b.Interaction(); !ok || used[c] || !rectContains(plan.Room.Interior, c) {
					t.Errorf("%v: %s worker cell %v is not open floor", room.Doors, b.Slot, c)
				}
				if d := rectCentreDistance(analyzers[0].Rect, b.Rect); d > testFurniture.Analyzer.MaxDistance {
					t.Errorf("%v: analyzer %.1f cells from %s, link distance %.0f", room.Doors, d, b.Slot, testFurniture.Analyzer.MaxDistance)
				}
				// Nothing stands between them: the analyzer and a bench are
				// in different rows, with only the open worker row between.
				if analyzers[0].Rect.Z+analyzers[0].Rect.Height > b.Rect.Z && analyzers[0].Rect.Z < b.Rect.Z+b.Rect.Height && analyzers[0].Rect.X+analyzers[0].Rect.Width > b.Rect.X && analyzers[0].Rect.X < b.Rect.X+b.Rect.Width {
					t.Errorf("%v: analyzer overlaps %s", room.Doors, b.Slot)
				}
			}
		}
	}
}

// A lab too shallow to leave the entrance row open plans benches only, and a
// catalog with no analyzer plans no slot for it.
func TestLabPlansNoAnalyzerSlotWhereItCannotLink(t *testing.T) {
	shallow, ok := PlanInterior(interiorRoomsAround(RoomRoleLaboratory, 11, 5, 3)[0], InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	for _, p := range shallow.Pieces {
		if p.Def == testFurniture.Analyzer.Def {
			t.Errorf("a 5-deep lab planned the analyzer at %+v", p.Rect)
		}
	}
	room := interiorRoomsAround(RoomRoleLaboratory, 11, 6, 3)[0]
	room.Shapes.Furniture.Analyzer = FacilityLink{}
	plain, ok := PlanInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	for _, p := range plain.Pieces {
		if p.Def == testFurniture.Analyzer.Def {
			t.Errorf("planned the analyzer with none in the catalog")
		}
	}
}
