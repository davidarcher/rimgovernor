package policy

import (
	"maps"
	"reflect"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func seatWant(n int) RegionWant {
	return RegionWant{Slot: "bench_chair", Def: "WorkChair", Size: domain.Cell{X: 1, Z: 1}, Count: n, OnInteraction: "bench"}
}

// A seat stands on the interaction cell of each bench, the bench keeps its
// place, and the layout without the seats is the layout with them.
func TestPlaceInRegionSeatsBenchesOnTheirInteractionCell(t *testing.T) {
	region := regionOfRows("########", "########", "########", "########")
	req := RegionPlacement{Region: region, Doors: []domain.Cell{{X: 3, Z: -1}}, Wants: benchWants(2)}
	bare, err := PlaceInRegion(req)
	if err != nil {
		t.Fatal(err)
	}
	for name, search := range map[string]*InteriorSearch{"greedy": nil, "searched": {Candidates: req.Doors, Shapes: testShapes, Seed: 7}} {
		t.Run(name, func(t *testing.T) {
			seated := req
			seated.Search = search
			seated.Wants = append(benchWants(2), seatWant(2))
			pieces, err := PlaceInRegion(seated)
			if err != nil {
				t.Fatal(err)
			}
			again, _ := PlaceInRegion(seated)
			if !reflect.DeepEqual(pieces, again) {
				t.Fatal("not deterministic")
			}
			var benches, chairs []InteriorPiece
			for _, p := range pieces {
				switch p.Def {
				case "Bench":
					benches = append(benches, p)
				case "WorkChair":
					chairs = append(chairs, p)
				}
			}
			if len(benches) != 2 || len(chairs) != 2 {
				t.Fatalf("%d benches, %d chairs", len(benches), len(chairs))
			}
			for i, b := range benches {
				cell, ok := b.Interaction()
				if !ok || chairs[i].Rect != (Rectangle{X: cell.X, Z: cell.Z, Width: 1, Height: 1}) || chairs[i].Slot != b.Slot+"_chair" {
					t.Errorf("%s: chair %+v %s not on its interaction cell %v", b.Slot, chairs[i].Rect, chairs[i].Slot, cell)
				}
			}
			if search == nil && !reflect.DeepEqual(bare, pieces[:len(bare)]) {
				t.Error("the seats moved a bench")
			}
			// The objective scores the floor the seats stand on, not the seats.
			floor := slices.DeleteFunc(slices.Clone(pieces), func(p InteriorPiece) bool { return p.Def == "WorkChair" })
			score := ScoreInterior(InteriorObjective{Region: region, Doors: req.Doors, Pieces: floor, Shapes: testShapes})
			if len(score.Violations) != 0 {
				t.Errorf("violations %v", score.Violations)
			}
		})
	}
}

// A bench with no interaction cell, or a seat that is not a single cell, gets
// no chair, and a want with a minimum fails when it cannot be met.
func TestPlaceInRegionSeatNeedsACellToStandOn(t *testing.T) {
	region := regionOfRows("######", "######", "######")
	door := []domain.Cell{{X: 2, Z: -1}}
	plain := []RegionWant{{Slot: "bench", Def: "Bench", Size: domain.Cell{X: 2, Z: 1}, Count: 1, Min: 1}}
	pieces, err := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: append(plain, seatWant(1))})
	if err != nil || len(pieces) != 1 {
		t.Fatalf("pieces %+v err %v", pieces, err)
	}
	big := seatWant(1)
	big.Size = domain.Cell{X: 2, Z: 1}
	if pieces, _ := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: append(benchWants(1), big)}); len(pieces) != 1 {
		t.Errorf("a 2x1 seat stood: %+v", pieces)
	}
	need := seatWant(1)
	need.Min = 1
	if _, err := PlaceInRegion(RegionPlacement{Region: region, Doors: door, Wants: append(plain, need)}); err == nil {
		t.Error("a required seat with nowhere to stand did not fail")
	}
}

func chairShapes() PieceShapes {
	out := PieceShapes{Defs: maps.Clone(testShapes.Defs), Furniture: testShapes.Furniture}
	out.Defs["WorkChair"] = InteriorPieceDef{Def: "WorkChair", Size: domain.Cell{X: 1, Z: 1}}
	return out
}

// A room with a chosen chair for its bench definition stands one on each
// bench's cell, in every role that works a bench; with none chosen the plan is
// the plan it was.
func TestRegionInteriorStandsTheChosenWorkChair(t *testing.T) {
	for _, role := range []RoomRole{RoomRoleWorkshop, RoomRoleLaboratory, RoomRoleKitchen} {
		t.Run(string(role), func(t *testing.T) {
			room, _, _ := sizedFixture(role, "chair", 9, 5, InteriorPieceDef{}).room()
			room.Shapes = chairShapes()
			bare, ok := PlanRoomInterior(room, InteriorPieceDef{})
			if !ok {
				t.Fatal("no plan")
			}
			bench, _ := room.benchDef(InteriorPieceDef{}, role)
			room.WorkChairs = map[string]string{bench.Def: "WorkChair"}
			seated, ok := PlanRoomInterior(room, InteriorPieceDef{})
			if !ok {
				t.Fatal("no plan with chairs")
			}
			benches, chairs := 0, 0
			cells := map[domain.Cell]bool{}
			for _, p := range seated.Pieces {
				switch p.Def {
				case bench.Def:
					benches++
					c, _ := p.Interaction()
					cells[c] = true
				case "WorkChair":
					chairs++
				}
			}
			if benches == 0 || chairs != benches {
				t.Fatalf("%d benches, %d chairs", benches, chairs)
			}
			for _, p := range seated.Pieces {
				if p.Def == "WorkChair" && !cells[domain.Cell{X: p.Rect.X, Z: p.Rect.Z}] {
					t.Errorf("chair %s at %+v is not on a bench's interaction cell", p.Slot, p.Rect)
				}
			}
			if len(bare.Pieces) != len(seated.Pieces)-chairs {
				t.Errorf("bare plan %d pieces, seated %d with %d chairs", len(bare.Pieces), len(seated.Pieces), chairs)
			}
			room.WorkChairs = map[string]string{bench.Def: "NoSuchChair"}
			unknown, _ := PlanRoomInterior(room, InteriorPieceDef{})
			if !reflect.DeepEqual(bare.Pieces, unknown.Pieces) {
				t.Error("a chair the catalog does not hold changed the plan")
			}
		})
	}
}

// The tiler stores the chair priced for a group's worked bench on its room; an
// unpriced choice, a choice of no chair and a node that is not worked add none.
func TestWorkChairsOfReadsThePricedWorkChoice(t *testing.T) {
	g := RoomGroup{Stations: []GroupStation{
		{Node: "bench", Def: "Bench", Purpose: PurposeWork},
		{Node: "bench2", Def: "Bench2", Purpose: PurposeWork},
		{Node: "bed", Def: "Bed", Purpose: PurposeSleep},
		{Node: "bench3", Def: "Bench3", Purpose: PurposeWork},
	}}
	comfort := []ComfortChoice{
		{Node: "bench", Option: ComfortOption{Def: "Chair"}, Priced: true},
		{Node: "bench2", Option: ComfortOption{Def: "Chair"}},
		{Node: "bed", Option: ComfortOption{Def: "BedFine"}, Priced: true},
		{Node: "bench3", Priced: true},
	}
	if got := workChairsOf(g, comfort); !reflect.DeepEqual(got, map[string]string{"Bench": "Chair"}) {
		t.Errorf("work chairs %v", got)
	}
	if workChairsOf(g, nil) != nil {
		t.Error("unknown comfort chose a chair")
	}
}
