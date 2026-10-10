package policy

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var testPenMarker = InteriorPieceDef{Def: PenMarkerDefinition, Size: domain.Cell{X: 1, Z: 1}}

func penBuilding(t *testing.T, def string, c domain.Cell) CurrentBuilding {
	t.Helper()
	b, err := domain.NewBuilding(def, c, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	return CurrentBuilding{ID: fmt.Sprintf("%s@%d,%d", def, c.X, c.Z), Building: b, Cells: []domain.Cell{c}}
}

// The paddock marker goes on a free cell of the wall's yard: inside the
// ring, under no reservation, nothing built on it; a standing marker makes
// the next pass a no-op.
func TestPaddockMarkerOnFreeYardCell(t *testing.T) {
	p := perimeterPlan(t, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	step, ok := NextPaddockStep(p, GroundOf(nil), nil, testPenMarker)
	if !ok || step.Marker || len(step.Candidates) == 0 {
		t.Fatalf("a closed-ring plan owes a marker: %+v %v", step, ok)
	}
	wi, _ := planInterior(p, 0)
	covered := map[domain.Cell]bool{}
	for _, r := range p.Reservations {
		if r.Kind != ReserveCoverClear {
			for _, c := range rectCells(r.Area) {
				covered[c] = true
			}
		}
	}
	for _, c := range step.Candidates {
		if i, in := wi.at(c); !in || !wi.in[i] || covered[c] {
			t.Fatalf("candidate %v is not a free yard cell", c)
		}
	}
	marker := penBuilding(t, PenMarkerDefinition, step.Candidates[0])
	again, ok := NextPaddockStep(p, GroundOf([]CurrentBuilding{marker}), []CurrentBuilding{marker}, testPenMarker)
	if !ok || !again.Marker || len(again.Candidates) != 0 {
		t.Fatalf("a standing marker plans nothing: %+v", again)
	}
	// A built cell is no candidate.
	wall := penBuilding(t, "Wall", step.Candidates[0])
	other, _ := NextPaddockStep(p, GroundOf([]CurrentBuilding{wall}), []CurrentBuilding{wall}, testPenMarker)
	for _, c := range other.Candidates {
		if c == step.Candidates[0] {
			t.Fatal("a built cell offered")
		}
	}
	if _, ok := NextPaddockStep(LayoutPlan{}, GroundOf(nil), nil, testPenMarker); ok {
		t.Fatal("a plan without a ring has no yard")
	}
}
