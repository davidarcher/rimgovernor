package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// shrinkFacts is a 10x4 rice zone "z1" beside a 2x2 corn zone "z2".
func shrinkFacts() observation.ColonyProjection {
	var facts observation.ColonyProjection
	facts.Farms = []observation.FarmZoneFact{{ID: "z1", Crop: "Plant_Rice"}, {ID: "z2", Crop: "Plant_Corn"}}
	for z := int32(0); z < 4; z++ {
		for x := int32(0); x < 12; x++ {
			id := "z1"
			if x >= 10 {
				id = "z2"
				if z >= 2 {
					continue
				}
			}
			facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, ZoneID: domain.Known(id)})
		}
	}
	return facts
}

func TestFieldShrinkHysteresis(t *testing.T) {
	facts := shrinkFacts()
	// 40 rice cells against a target of 32 sit inside the 25% band.
	if _, ok := planFieldShrink(facts, map[string]int{"Plant_Rice": 32}); ok {
		t.Fatal("shrank inside the hysteresis band")
	}
	// Against 20 the zone shrinks to 22 (110%), never touching corn.
	s, ok := planFieldShrink(facts, map[string]int{"Plant_Rice": 20, "Plant_Corn": 4})
	if !ok || s.ID != "z1" || s.Remove != 18 {
		t.Fatalf("shrink = %+v %v", s, ok)
	}
	// Unknown or zero targets never shrink.
	if _, ok := planFieldShrink(facts, map[string]int{"Plant_Rice": 0}); ok {
		t.Fatal("shrank on a zero target")
	}
}

func TestFieldShrinkOnlyBareAndConnected(t *testing.T) {
	s, _ := planFieldShrink(shrinkFacts(), map[string]int{"Plant_Rice": 20})
	bare := map[domain.Cell]bool{}
	for _, c := range s.Order {
		bare[c] = true
	}
	// The sown column x=0 stays.
	for z := int32(0); z < 4; z++ {
		delete(bare, domain.Cell{X: 0, Z: z})
	}
	cells := shrinkCells(s, bare)
	if len(cells) != 18 {
		t.Fatalf("removed %d cells, want 18", len(cells))
	}
	left := map[domain.Cell]bool{}
	for c := range s.Zone {
		left[c] = true
	}
	for _, c := range cells {
		if c.X == 0 {
			t.Fatalf("removed sown cell %v", c)
		}
		delete(left, c)
	}
	var start domain.Cell
	for c := range left {
		start = c
	}
	if len(cellComponent(left, start)) != len(left) {
		t.Fatal("shrunk zone is disconnected")
	}
}
