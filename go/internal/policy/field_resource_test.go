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

func TestResourceFieldCandidateServesAClothingFloor(t *testing.T) {
	cotton := resourceCrop("Plant_Cotton", "Cloth", 5.8, 8)
	cotton.HarvestWork = domain.Known(200.0)
	plan, ok := PlanFieldByResource(resourceRequest(50, cotton))
	if !ok {
		t.Fatal(plan.Explain())
	}
	field, ok := ResourceFieldCandidate("Cloth", plan)
	if !ok || field.ID != ResourceFieldPrefix+"Plant_Cotton" || field.Kind != CandidateHarvest {
		t.Fatalf("%+v %v", field, ok)
	}
	if lead, _ := field.LeadDays.Value(); lead != 5.8 {
		t.Fatalf("lead %v", lead)
	}
	if setup, _ := field.UpfrontCost.LaborTicks.Value(); setup != 7*FieldSowTicksPerCell {
		t.Fatalf("setup %v", setup)
	}
	if work, _ := field.LaborPerDay.Value(); work != 200*7/5.8 {
		t.Fatalf("work %v", work)
	}
	open := func(candidates ...SupplyCandidate) map[string]bool {
		p, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "Cloth", Deficit: 50, HorizonDays: 5.8, Candidates: candidates}}, domain.Known(100000.0))
		if err != nil {
			t.Fatal(err)
		}
		return p.OpenedIDs("Cloth", CandidateHarvest)
	}
	if got := open(field); !got[field.ID] {
		t.Fatalf("no wild cotton: the field opens, got %v", got)
	}
	wild := AcquisitionSourceCandidates("Cloth", []AcquisitionSource{{ID: "wild", Resource: "Cloth", Yield: 60}}, domain.Cell{}, domain.Known(int64(1000)))
	if got := open(append(wild, field)...); !got["wild"] || got[field.ID] {
		t.Fatalf("wild cotton is cheaper: got %v", got)
	}
	// An unknown harvest work prices nothing.
	plan.Crop.HarvestWork = domain.Unknown[float64]()
	if _, ok := ResourceFieldCandidate("Cloth", plan); ok {
		t.Fatal("priced a field on an unknown harvest work")
	}
}

func TestStandingFieldYield(t *testing.T) {
	cotton := resourceCrop("Plant_Cotton", "Cloth", 5.8, 8)
	if got := StandingFieldYield(cotton, "Cloth", domain.Known(uint32(5))); got != 40 {
		t.Fatalf("standing yield %v", got)
	}
	if StandingFieldYield(cotton, "Medicine", domain.Known(uint32(5))) != 0 || StandingFieldYield(cotton, "Cloth", domain.Unknown[uint32]()) != 0 {
		t.Fatal("another resource or unknown cells count nothing")
	}
}
