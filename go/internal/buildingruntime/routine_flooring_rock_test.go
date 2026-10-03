package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Rock on a floor cell is left and never previewed; the open cell beside it
// is floored. A cell the census does not list still goes to the native
// preview.
func TestFlooringLeavesRockCellsWithoutPreview(t *testing.T) {
	t.Parallel()
	p, _, n, s, site, _ := rockCoolerStep(t)
	rock := site.Cell // (1,3) is rock in the fixture
	for i, c := range s.facts.Cells {
		if c.Cell == rock {
			s.facts.Cells[i].Occupied, s.facts.Cells[i].Walkable, s.facts.Cells[i].Roof = domain.Known(true), domain.Known(false), domain.Known("RoofRockThick")
		}
	}
	open, unlisted := domain.Cell{X: 1, Z: 1}, domain.Cell{X: 40, Z: 40}
	p.flooring = &policy.FlooringProposal{Method: policy.FlooringBuild, Definition: "WoodPlankFloor", Cells: []domain.Cell{rock, open, unlisted}}
	before := n.previews
	if _, _, _, err := p.previewFlooring(context.Background(), s.state.Snapshot, s.facts, nil, func() error { return nil }); err != nil {
		t.Fatal(err)
	}
	if got := n.previews - before; got != 2 {
		t.Fatal("rock cell must not be previewed; open and unlisted cells are", got)
	}
}
