package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func resourceCrop(name string, resource Resource, days, units float64) CropChoice {
	c := fieldCrop(name, days, 0, 0.5, 1.0)
	c.HarvestNutrition = domain.Unknown[float64]()
	c.Edible = domain.Known(false)
	c.Harvests = domain.Known(resource)
	c.UnitsPerCell = domain.Known(units)
	return c
}

func resourceRequest(deficit float64, crops ...CropChoice) ResourceFieldRequest {
	f := fieldRequest(1.0)
	return ResourceFieldRequest{Resource: "Cloth", Deficit: domain.Known(deficit), Choices: crops, Climate: f.Climate, Site: f.Site}
}

func TestPlanFieldByResource(t *testing.T) {
	cotton := resourceCrop("Plant_Cotton", "Cloth", 5.8, 8)
	t.Run("cotton on suitable soil", func(t *testing.T) {
		plan, ok := PlanFieldByResource(resourceRequest(50, cotton, resourceCrop("Plant_Healroot", "MedicineHerbal", 9, 1), fieldCrop("Plant_Rice", 3, 0.3, 0.7, 1)))
		if !ok || plan.Crop.Name != "Plant_Cotton" || plan.Needed != 7 || plan.Sites.Cells != 7 || plan.Urgent {
			t.Fatal(plan.Explain())
		}
	})
	t.Run("refusals", func(t *testing.T) {
		for name, mutate := range map[string]func(*ResourceFieldRequest){
			"out of season":    func(r *ResourceFieldRequest) { r.Climate.DaysRemaining = domain.Known(10.0) },
			"not sowing":       func(r *ResourceFieldRequest) { r.Climate.Sowing = domain.Known(false) },
			"dark biome":       func(r *ResourceFieldRequest) { r.Climate.OutdoorsDark = domain.Known(true) },
			"unknown darkness": func(r *ResourceFieldRequest) { r.Climate.OutdoorsDark = domain.Unknown[bool]() },
			"unknown season":   func(r *ResourceFieldRequest) { r.Climate.DaysRemaining = domain.Unknown[float64]() },
			"unknown deficit":  func(r *ResourceFieldRequest) { r.Deficit = domain.Unknown[float64]() },
			"no deficit":       func(r *ResourceFieldRequest) { r.Deficit = domain.Known(0.0) },
			"unknown units":    func(r *ResourceFieldRequest) { r.Choices[0].UnitsPerCell = domain.Unknown[float64]() },
			"unknown harvests": func(r *ResourceFieldRequest) { r.Choices[0].Harvests = domain.Unknown[Resource]() },
			"unknown grow":     func(r *ResourceFieldRequest) { r.Choices[0].GrowDays = domain.Unknown[float64]() },
			"unavailable":      func(r *ResourceFieldRequest) { r.Choices[0].Available = domain.Known(false) },
			"poor soil":        func(r *ResourceFieldRequest) { r.Choices[0].FertilityMin = domain.Known(1.5) },
		} {
			r := resourceRequest(50, cotton)
			r.Choices = []CropChoice{cotton}
			mutate(&r)
			if plan, ok := PlanFieldByResource(r); ok {
				t.Fatalf("%s: planned %s", name, plan.Explain())
			}
		}
	})
	t.Run("score picks between crops", func(t *testing.T) {
		// Devilstrand-like: slower and poorer per cell than cotton for the same resource.
		slow := resourceCrop("Plant_Devilstrand", "Cloth", 20, 8)
		plan, ok := PlanFieldByResource(resourceRequest(50, slow, cotton))
		if !ok || plan.Crop.Name != "Plant_Cotton" || len(plan.Candidates) != 2 {
			t.Fatal(plan.Explain())
		}
		richer := resourceCrop("Plant_Aaa", "Cloth", 5.8, 16)
		plan, ok = PlanFieldByResource(resourceRequest(50, cotton, richer))
		if !ok || plan.Crop.Name != "Plant_Aaa" || plan.Needed != 4 {
			t.Fatal(plan.Explain())
		}
	})
	t.Run("cell cap", func(t *testing.T) {
		plan, ok := PlanFieldByResource(resourceRequest(1e9, cotton))
		if !ok || plan.Needed != ResourceFieldCellCap {
			t.Fatal(plan.Explain())
		}
	})
}
