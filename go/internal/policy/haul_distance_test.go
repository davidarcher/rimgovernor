package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// open grid 10x10 with a wall column at x=5 except a gap at z=9.
func haulGrid() []SiteCell {
	var cells []SiteCell
	for x := int32(0); x < 10; x++ {
		for z := int32(0); z < 10; z++ {
			walk := !(x == 5 && z != 9)
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(walk)})
		}
	}
	return cells
}

func TestHaulCostsFollowPathsNotStraightLines(t *testing.T) {
	bench := HaulConsumer{Cells: []domain.Cell{{X: 4, Z: 0}}, Weight: 1}
	costs, err := HaulCosts(haulGrid(), []HaulConsumer{bench})
	if err != nil {
		t.Fatal(err)
	}
	// (6,0) is two cells away in a straight line but behind the wall.
	if got := costs[domain.Cell{X: 6, Z: 0}]; got <= 9 {
		t.Fatalf("cost behind wall = %d, want a detour through the gap", got)
	}
	if got := costs[domain.Cell{X: 2, Z: 0}]; got != 2 {
		t.Fatalf("cost same side = %d, want 2", got)
	}
	if _, ok := costs[domain.Cell{X: 5, Z: 0}]; ok {
		t.Fatal("wall cell costed")
	}
	sites := []Rectangle{{X: 6, Z: 0, Width: 2, Height: 2}, {X: 1, Z: 0, Width: 2, Height: 2}}
	ranked := RankSitesByHaul(sites, costs)
	if ranked[0] != sites[1] {
		t.Fatalf("ranked %v, want the same-side site first", ranked)
	}
}

func TestHaulCostsWeightTraffic(t *testing.T) {
	busy := HaulConsumer{Cells: []domain.Cell{{X: 0, Z: 0}}, Weight: 5}
	quiet := HaulConsumer{Cells: []domain.Cell{{X: 4, Z: 0}}, Weight: 1}
	costs, err := HaulCosts(haulGrid(), []HaulConsumer{busy, quiet})
	if err != nil {
		t.Fatal(err)
	}
	near, far := costs[domain.Cell{X: 1, Z: 0}], costs[domain.Cell{X: 3, Z: 0}]
	if near >= far {
		t.Fatalf("busy side %d, quiet side %d: traffic weight ignored", near, far)
	}
	unranked := []Rectangle{{X: 2, Z: 5, Width: 2, Height: 2}}
	if got := RankSitesByHaul(unranked, nil); got[0] != unranked[0] {
		t.Fatal("no costs must keep order")
	}
}
