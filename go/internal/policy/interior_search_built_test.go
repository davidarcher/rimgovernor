package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// doorFlow is one gated edge from the door to a piece slot.
type doorFlow struct{ slot string }

func (f doorFlow) Edges([]InteriorPiece) []FlowEdge {
	return []FlowEdge{{From: FlowEnd{Door: true}, To: FlowEnd{Slot: f.slot}, Weight: 1}}
}

// A built piece moves when it is minifiable and holds no state, and only when
// that strictly improves a gated term; a non-minifiable or stateful piece
// stays pinned. Every result is deterministic and no gated term is worse than
// the seed (the built layout).
func TestInteriorSearchMovesBuiltMinifiableStatelessPieces(t *testing.T) {
	region := regionOfRows("######", "######", "######", "######")
	req := RegionPlacement{Region: region, Doors: []domain.Cell{{X: 2, Z: -1}}}
	front := domain.Cell{X: 0, Z: -1}
	far := NewInteriorPiece("stove", testStove, domain.Cell{X: 3, Z: 1}, domain.North, domain.Cell{X: 0, Z: 3})
	far.InteractionOffset = &front
	search := func(b BuiltPiece) InteriorSearchResult {
		s := InteriorSearch{Shapes: testShapes, Dining: testDining, Flows: doorFlow{"stove"}, Built: []BuiltPiece{b}, Seed: 3}
		res, err := SearchInterior(req, s)
		if err != nil {
			t.Fatal(err)
		}
		again, _ := SearchInterior(req, s)
		if !reflect.DeepEqual(res, again) {
			t.Fatal("not deterministic")
		}
		if !res.Score.Feasible() || len(res.Score.WorseTerms(res.Seed)) > 0 {
			t.Fatalf("score %+v worse than seed %+v", res.Score, res.Seed)
		}
		return res
	}

	moved := search(BuiltPiece{Piece: far, Minifiable: true})
	if reflect.DeepEqual(moved.Pieces[0].Rect, far.Rect) || len(moved.Moves) == 0 || len(moved.Seed.WorseTerms(moved.Score)) == 0 {
		t.Fatalf("minifiable stateless stove did not move to a better cell: %+v moves %v", moved.Pieces[0].Rect, moved.Moves)
	}
	if got := moved.Stood(); len(got) != 1 || got[0].Def != testStove {
		t.Fatalf("a moved built piece is a stood piece: %+v", got)
	}

	for name, b := range map[string]BuiltPiece{
		"not minifiable": {Piece: far},
		"holds state":    {Piece: far, Minifiable: true, HoldsState: true},
	} {
		res := search(b)
		if !reflect.DeepEqual(res.Pieces[0], far) || len(res.Moves) != 0 || len(res.Stood()) != 0 {
			t.Errorf("%s: piece moved: %+v", name, res.Pieces)
		}
	}

	// Searched again from where it ended, nothing is strictly better: no move
	// is kept.
	near := moved.Pieces[0]
	if res := search(BuiltPiece{Piece: near, Minifiable: true}); len(res.Moves) != 0 || !reflect.DeepEqual(res.Pieces[0], near) {
		t.Errorf("a piece with nothing better moved: %+v", res.Moves)
	}
}
