package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A tree zone and a crop zone never touch: the native sower leaves a
// crop beside a sown tree and a tree beside a sown crop unsown, so each kind
// keeps a cell clear of the other, and a food field never adopts a tree zone.
func TestFieldBlocksKeepTreesApartFromCrops(t *testing.T) {
	plan := policy.LayoutPlan{Zones: []policy.LayoutZone{{Kind: policy.ZoneField}}}
	facts := observation.ColonyProjection{TechTier: domain.Known(policy.TechTierCamp)}
	for z := int32(0); z < 20; z++ {
		plan.Zones[0].Runs = append(plan.Zones[0].Runs, policy.RowRun{Z: z, X: 0, Length: 40})
		for x := int32(0); x < 40; x++ {
			facts.Cells = append(facts.Cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Things: policy.OccupantThings(false), Zone: domain.Known(false), ZoneID: domain.Known(""), Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	facts.LayoutPlan = domain.Known(plan)
	facts.Definitions = []observation.PlanningDefinition{
		{Name: "Plant_Rice", Edible: domain.Known(true)},
		{Name: "Plant_TreeOak", Edible: domain.Known(false), BlockAdjacentSow: domain.Known(true), HarvestDestroysPlant: domain.Known(true)},
	}
	oak := policy.CropChoice{Name: "Plant_TreeOak", Edible: domain.Known(false), FertilityMin: domain.Known(0.5), BlockAdjacentSow: domain.Known(true), HarvestDestroys: domain.Known(true)}
	place := func(zone string, options ...policy.FieldBlockOption) fieldBlockEdit {
		edit, reason, ok := planFieldBlock(facts, domain.Cell{X: 20, Z: 10}, options, nil)
		if !ok {
			t.Fatalf("%s: %q", zone, reason)
		}
		if edit.Zone != "" {
			// A grown zone is the food zone, never the tree zone.
			if edit.Zone != "Zone_rice" {
				t.Fatalf("%s grew zone %s", zone, edit.Zone)
			}
			zoneBlockCells(&facts, edit.Zone, edit.Cells...)
			return edit
		}
		zoneBlockCells(&facts, zone, edit.Cells...)
		facts.Farms = append(facts.Farms, observation.FarmZoneFact{ID: zone, Crop: edit.Crop})
		return edit
	}
	near := func(a, b []domain.Cell) bool {
		for _, c := range a {
			for _, d := range b {
				if dx, dz := c.X-d.X, c.Z-d.Z; dx >= -1 && dx <= 1 && dz >= -1 && dz <= 1 {
					return true
				}
			}
		}
		return false
	}
	first := place("Zone_rice", rice(60)...)
	trees := place("Zone_oak", policy.FieldBlockOption{Crop: oak, Needed: 20})
	if trees.Crop != "Plant_TreeOak" || len(trees.Cells) != 20 || near(first.Cells, trees.Cells) {
		t.Fatalf("tree block %v touches rice %v", trees.Cells, first.Cells)
	}
	more := place("Zone_rice2", rice(60)...)
	if more.Zone == "Zone_oak" || near(more.Cells, trees.Cells) {
		t.Fatalf("a food block %v touches the tree block %v", more.Cells, trees.Cells)
	}
}
