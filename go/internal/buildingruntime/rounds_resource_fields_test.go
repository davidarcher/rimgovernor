package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A cotton field is priced from the crop's catalog facts, standing fields of
// the crop count against the deficit, and the plan it priced stays readable by
// candidate ID for the executor (#2284).
func TestResourceFieldPlannerPricesAndCountsStandingFields(t *testing.T) {
	cotton := policy.CropChoice{Name: "Plant_Cotton", Available: domain.Known(true), Edible: domain.Known(false),
		GrowDays: domain.Known(5.8), FertilityMin: domain.Known(0.5), FertilitySensitivity: domain.Known(1.0), HarvestWork: domain.Known(200.0),
		SowTags: domain.Known([]string{"Ground"}), Harvests: domain.Known(policy.Resource("Cloth")), UnitsPerCell: domain.Known(8.0)}
	var cells []policy.SiteCell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			cells = append(cells, policy.SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false),
				Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	sited := 0
	f := &resourceFieldPlanner{
		choices: []policy.CropChoice{cotton},
		climate: policy.CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(40.0), OutdoorsDark: domain.Known(false)},
		farms:   []observation.FarmZoneFact{{ID: "z1", Crop: "Plant_Cotton", UsableCells: domain.Known(uint32(3))}, {ID: "z2", Crop: "Plant_Rice", UsableCells: domain.Known(uint32(9))}},
		siteRead: func() (policy.FarmSiteRequest, bool, error) {
			sited++
			return policy.FarmSiteRequest{Bounds: policy.Bounds{Width: 20, Height: 20}, Anchor: domain.Cell{X: 10, Z: 10}, Cells: cells}, true, nil
		},
	}
	if !f.serves("Cloth") || f.serves("Steel") {
		t.Fatal("cotton serves Cloth only")
	}
	if got := f.standing("Cloth"); got != 24 {
		t.Fatalf("three standing cells yield 24, got %d", got)
	}
	if sited != 0 {
		t.Fatal("the site is read only when a field is priced")
	}
	candidate, plan, ok, err := f.candidate("Cloth", 50)
	if err != nil || !ok || sited != 1 {
		t.Fatalf("%v %v sited=%d", ok, err, sited)
	}
	if candidate.ID != policy.ResourceFieldPrefix+"Plant_Cotton" || plan.Crop.Name != "Plant_Cotton" || plan.Needed != 7 || plan.Sites.Cells == 0 {
		t.Fatalf("%+v %s", candidate, plan.Explain())
	}
	if _, _, ok, _ = f.candidate("Cloth", 10); !ok || sited != 1 {
		t.Fatalf("the site is read once: ok=%v sited=%d", ok, sited)
	}
	f.climate.Sowing = domain.Known(false)
	if _, _, ok, _ = f.candidate("Cloth", 50); ok {
		t.Fatal("no field out of the sowing season")
	}
}
