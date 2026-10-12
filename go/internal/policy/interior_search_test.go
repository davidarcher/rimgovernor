package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The search is deterministic for equal inputs, bounded by its budget, keeps
// pinned pieces where they stand, stays feasible and no worse than its seed on
// irregular regions, and is reachable through PlaceInRegion.
func TestInteriorSearchDeterministicPinnedAndIrregular(t *testing.T) {
	for name, region := range map[string]Region{
		"rect": regionOfRows("######", "######", "######", "######"),
		"L":    regionOfRows("####..", "####..", "######", "######"),
		"T":    regionOfRows("######", "######", "..##..", "..##.."),
	} {
		t.Run(name, func(t *testing.T) {
			var pinned []InteriorPiece
			pin := NewInteriorPiece("pinned", "Bench", domain.Cell{X: 2, Z: 1}, domain.North, domain.Cell{X: 4, Z: 3})
			if region.Contains(domain.Cell{X: 4, Z: 3}) && region.Contains(domain.Cell{X: 5, Z: 3}) {
				off := domain.Cell{X: 0, Z: -1}
				pin.InteractionOffset = &off
				pinned = []InteriorPiece{pin}
			}
			req := RegionPlacement{Region: region, Doors: []domain.Cell{{X: 2, Z: -1}}, Wants: benchWants(3)}
			s := InteriorSearch{Shapes: testShapes, Dining: testDining, Pinned: pinned, Seed: 7}
			a, err := SearchInterior(req, s)
			if err != nil {
				t.Fatal(err)
			}
			b, _ := SearchInterior(req, s)
			if !reflect.DeepEqual(a, b) {
				t.Fatal("not deterministic")
			}
			if !a.Score.Feasible() || len(a.Moves) > DefaultInteriorSearchIterations || len(a.Score.WorseTerms(a.Seed)) > 0 {
				t.Fatalf("score %+v moves %d", a.Score, len(a.Moves))
			}
			if len(pinned) == 1 && !reflect.DeepEqual(a.Pieces[0], pin) {
				t.Fatalf("pinned piece moved: %+v", a.Pieces[0])
			}
			req.Search = &s
			via, err := PlaceInRegion(req)
			if err != nil || !reflect.DeepEqual(via, a.Pieces[len(pinned):]) {
				t.Fatalf("PlaceInRegion with Search differs: %v", err)
			}
		})
	}
}
