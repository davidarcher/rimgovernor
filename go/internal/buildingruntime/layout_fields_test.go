package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// blockFacts is a Camp-tier colony with two 4x2 plan field blocks at
// x 0-3 and x 6-9 on rows 0-1, fertile open ground everywhere else.
func blockFacts() observation.ColonyProjection {
	plan := policy.LayoutPlan{Zones: []policy.LayoutZone{
		{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 0, X: 0, Length: 4}, {Z: 1, X: 0, Length: 4}}},
		{Kind: policy.ZoneField, Runs: []policy.RowRun{{Z: 0, X: 6, Length: 4}, {Z: 1, X: 6, Length: 4}}},
	}}
	facts := observation.ColonyProjection{BuildTier: domain.Known(policy.BuildTierCamp), LayoutPlan: domain.Known(plan)}
	for z := int32(0); z < 2; z++ {
		for x := int32(0); x < 10; x++ {
			facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), ZoneID: domain.Known(""), Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	return facts
}

func zoneBlockCells(facts *observation.ColonyProjection, zone string, cells ...domain.Cell) {
	for i := range facts.Cells {
		for _, c := range cells {
			if facts.Cells[i].Cell == c {
				facts.Cells[i].Zone, facts.Cells[i].ZoneID = domain.Known(true), domain.Known(zone)
			}
		}
	}
}

var riceChoice = policy.CropChoice{Name: "Plant_Rice", FertilityMin: domain.Known(0.5)}

func rice(n int) []policy.FieldBlockOption {
	return []policy.FieldBlockOption{{Crop: riceChoice, Needed: n}}
}

// A new block next to a rice zone opens with the next viable crop; with
// rice the sole viable crop it still opens rice (#1225).
func TestFieldBlockAvoidsNeighbourCrop(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	var first []domain.Cell
	for z := int32(0); z < 2; z++ {
		for x := int32(0); x < 4; x++ {
			first = append(first, domain.Cell{X: x, Z: z})
		}
	}
	zoneBlockCells(&facts, "Zone_1", first...)
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Rice"}}
	corn := policy.FieldBlockOption{Crop: policy.CropChoice{Name: "Plant_Corn", FertilityMin: domain.Known(0.5)}, Needed: 8}
	edit, reason, ok := planFieldBlock(facts, anchor, append(rice(8), corn), nil)
	if !ok || edit.Zone != "" || edit.Crop != "Plant_Corn" {
		t.Fatalf("neighbour crop not avoided: %+v %q %v", edit, reason, ok)
	}
	edit, reason, ok = planFieldBlock(facts, anchor, rice(8), nil)
	if !ok || edit.Zone != "" || edit.Crop != "Plant_Rice" {
		t.Fatalf("sole viable crop refused: %+v %q %v", edit, reason, ok)
	}
}

func TestFieldBlockCreatesThenGrows(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	edit, reason, ok := planFieldBlock(facts, anchor, rice(4), nil)
	if !ok || edit.Zone != "" || edit.Crop != "Plant_Rice" || len(edit.Cells) != 4 {
		t.Fatalf("create %+v %q %v", edit, reason, ok)
	}
	for _, c := range edit.Cells {
		if c.X > 3 {
			t.Fatalf("created outside the first block: %v", edit.Cells)
		}
	}
	zoneBlockCells(&facts, "Zone_1", edit.Cells...)
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Potato"}}
	grow, reason, ok := planFieldBlock(facts, anchor, rice(4), nil)
	if !ok || grow.Zone != "Zone_1" || grow.Crop != "Plant_Potato" || len(grow.Cells) != 4 {
		t.Fatalf("grow %+v %q %v", grow, reason, ok)
	}
	for _, c := range grow.Cells {
		if c.X > 3 {
			t.Fatalf("grew outside the block: %v", grow.Cells)
		}
	}
}

func TestFieldBlockNextOnlyWhenFull(t *testing.T) {
	facts := blockFacts()
	anchor := domain.Cell{X: 0, Z: 0}
	facts.Farms = []observation.FarmZoneFact{{ID: "Zone_1", Crop: "Plant_Rice"}}
	var first []domain.Cell
	for z := int32(0); z < 2; z++ {
		for x := int32(0); x < 3; x++ {
			first = append(first, domain.Cell{X: x, Z: z})
		}
	}
	zoneBlockCells(&facts, "Zone_1", first...)
	edit, _, ok := planFieldBlock(facts, anchor, rice(8), nil)
	if !ok || edit.Zone != "Zone_1" || len(edit.Cells) != 2 {
		t.Fatalf("partial block should grow first: %+v %v", edit, ok)
	}
	zoneBlockCells(&facts, "Zone_1", edit.Cells...)
	next, _, ok := planFieldBlock(facts, anchor, rice(8), nil)
	if !ok || next.Zone != "" || len(next.Cells) != 8 || next.Cells[0].X != 6 {
		t.Fatalf("full block should open the next: %+v %v", next, ok)
	}
	zoneBlockCells(&facts, "Zone_2", next.Cells...)
	if _, reason, ok := planFieldBlock(facts, anchor, rice(8), nil); ok || reason != "every field block is full" {
		t.Fatalf("all full: %q %v", reason, ok)
	}
}

func TestFieldBlockRefusesWithoutBlocks(t *testing.T) {
	facts := blockFacts()
	facts.LayoutPlan = domain.Known(policy.LayoutPlan{})
	if _, reason, ok := planFieldBlock(facts, domain.Cell{}, rice(4), nil); ok || reason != "layout plan has no field blocks" {
		t.Fatalf("%q %v", reason, ok)
	}
	facts.LayoutPlan = domain.Unknown[policy.LayoutPlan]()
	if _, reason, ok := planFieldBlock(facts, domain.Cell{}, rice(4), nil); ok || reason != "no layout plan" {
		t.Fatalf("%q %v", reason, ok)
	}
}

// Camp tier protects everything outside the plan's field cells too.
func TestLayoutFieldProtectedAtCamp(t *testing.T) {
	facts := blockFacts()
	if got := layoutFieldProtected(facts, nil); len(got) != 4 {
		t.Fatalf("protected %v", got)
	}
}
