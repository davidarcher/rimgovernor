package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Vanilla-shaped crops: rice fast and fertility-hungry, corn slow and rich,
// potato tolerant of poor soil.
func fieldCrop(name string, days, yield, minimum, sensitivity float64) CropChoice {
	return CropChoice{Name: name, Available: domain.Known(true), Edible: domain.Known(true), GrowDays: domain.Known(days), HarvestNutrition: domain.Known(yield), FertilityMin: domain.Known(minimum), FertilitySensitivity: domain.Known(sensitivity), Demand: domain.Known(1.6)}
}
func fieldRequest(fertility float64) FieldRequest {
	r := FieldRequest{Climate: CropClimate{domain.Known(true), domain.Known(60.0)}, Runway: domain.Known(30.0), Colonists: domain.Known(int64(3)), ReserveDays: 10, Coverage: domain.Known(0.0)}
	r.Choices = []CropChoice{fieldCrop("Plant_Rice", 3, 0.3, 0.7, 1.0), fieldCrop("Plant_Corn", 11, 1.1, 0.7, 1.0), fieldCrop("Plant_Potato", 6, 0.55, 0.5, 0.4)}
	r.Site = FarmSiteRequest{Bounds: Bounds{40, 40}, Anchor: domain.Cell{X: 20, Z: 20}}
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			r.Site.Cells = append(r.Site.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(false), Fertility: domain.Known(fertility)})
		}
	}
	return r
}

func TestPlanFieldPoorSoilPrefersPotato(t *testing.T) {
	plan, ok := PlanField(fieldRequest(1.0))
	if !ok || plan.Crop.Name == "Plant_Potato" || plan.Sites.Cells < plan.Needed || plan.Urgent {
		t.Fatal(plan.Explain())
	}
	// Gravel-grade soil is below rice and corn's floor; only potato can plant.
	plan, ok = PlanField(fieldRequest(0.6))
	if !ok || plan.Crop.Name != "Plant_Potato" || plan.Sites.Cells < plan.Needed {
		t.Fatal(plan.Explain())
	}
	for _, c := range plan.Candidates[1:] {
		if c.Sites.Cells != 0 || c.Reason != "no plantable soil" {
			t.Fatal(c.Crop.Name, c.Reason)
		}
	}
	// Barren ground plants nothing and says so.
	if plan, ok = PlanField(fieldRequest(0.1)); ok || len(plan.Candidates) != 3 {
		t.Fatal(plan.Explain())
	}
}

func TestPlanFieldUrgentRunwayPrefersFastestCrop(t *testing.T) {
	r := fieldRequest(1.0)
	buffered, ok := PlanField(r)
	if !ok || buffered.Urgent || buffered.Crop.Name == "Plant_Rice" {
		t.Fatal(buffered.Explain())
	}
	r.Runway = domain.Known(4.0)
	urgent, ok := PlanField(r)
	if !ok || !urgent.Urgent || urgent.Crop.Name != "Plant_Rice" {
		t.Fatal(urgent.Explain())
	}
	// Unknown runway is not urgency.
	r.Runway = domain.Unknown[float64]()
	if plan, ok := PlanField(r); !ok || plan.Urgent {
		t.Fatal(plan.Explain())
	}
}

func TestPlanFieldShortAndUnknownSeason(t *testing.T) {
	r := fieldRequest(1.0)
	// Ten days left: corn (27.5) is out, potato (15) is out, rice (7.5) stays.
	r.Climate.DaysRemaining = domain.Known(10.0)
	plan, ok := PlanField(r)
	if !ok || plan.Crop.Name != "Plant_Rice" || len(plan.Candidates) != 3 {
		t.Fatal(plan.Explain())
	}
	for _, c := range plan.Candidates[1:] {
		if c.Reason != "season too short" {
			t.Fatal(c.Crop.Name, c.Reason)
		}
	}
	r.Climate.DaysRemaining = domain.Known(6.0)
	if plan, ok = PlanField(r); ok {
		t.Fatal("winter crop", plan.Explain())
	}
	// Unknown remaining season while sowing is possible plants the fastest crop.
	r.Climate.DaysRemaining = domain.Unknown[float64]()
	plan, ok = PlanField(r)
	if !ok || !plan.Urgent || plan.Crop.Name != "Plant_Rice" {
		t.Fatal(plan.Explain())
	}
	r.Climate.Sowing = domain.Unknown[bool]()
	if plan, ok = PlanField(r); ok {
		t.Fatal("unknown sowing planted", plan.Explain())
	}
	r.Climate.Sowing = domain.Known(false)
	if plan, ok = PlanField(r); ok {
		t.Fatal("sowing off planted", plan.Explain())
	}
}

func TestPlanFieldCoverageProtectionAndBounds(t *testing.T) {
	r := fieldRequest(1.0)
	r.Coverage = domain.Known(1.0)
	if plan, ok := PlanField(r); ok {
		t.Fatal("full coverage planted", plan.Explain())
	}
	r = fieldRequest(1.0)
	r.Coverage = domain.Known(0.5)
	half, ok := PlanField(r)
	full, _ := PlanField(fieldRequest(1.0))
	if !ok || half.Needed >= full.Needed {
		t.Fatal(half.Needed, full.Needed)
	}
	for _, c := range full.Candidates {
		for _, h := range half.Candidates {
			if c.Crop.Name == h.Crop.Name && h.Needed*2 != c.Needed && h.Needed*2 != c.Needed+1 {
				t.Fatal(c.Crop.Name, h.Needed, c.Needed)
			}
		}
	}
	r = fieldRequest(1.0)
	r.Site.Protected = []domain.Cell{{X: 20, Z: 20}}
	plan, ok := PlanField(r)
	if !ok {
		t.Fatal(plan.Explain())
	}
	for _, patch := range plan.Sites.Patches {
		for _, c := range rectCells(patch) {
			if c == (domain.Cell{X: 20, Z: 20}) {
				t.Fatal("protected cell planted")
			}
		}
	}
	r = fieldRequest(1.0)
	r.Choices = append(r.Choices, r.Choices[0])
	if plan, ok := PlanField(r); ok {
		t.Fatal("duplicate crop", plan.Explain())
	}
	r = fieldRequest(1.0)
	r.Coverage = domain.Unknown[float64]()
	if plan, ok := PlanField(r); ok {
		t.Fatal("unknown coverage", plan.Explain())
	}
	r = fieldRequest(1.0)
	r.Choices[0].Available = domain.Known(false)
	r.Choices[1].Edible = domain.Unknown[bool]()
	if plan, ok := PlanField(r); !ok || plan.Crop.Name != "Plant_Potato" {
		t.Fatal(plan.Explain())
	}
}
