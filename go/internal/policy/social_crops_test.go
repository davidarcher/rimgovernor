package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSocialCropBoundedAndNotFood(t *testing.T) {
	crop := CropChoice{Name: "Plant_Hops", Harvests: domain.Known(Resource("Hops")), Available: domain.Known(true), Edible: domain.Known(false), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.0), FertilityMin: domain.Known(0.7), FertilitySensitivity: domain.Known(1.0)}
	climate := CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0), OutdoorsDark: domain.Known(false)}
	got := PlanSocialCrop(crop, climate, 4)
	if got != 5 {
		t.Fatal(got)
	}
	for _, existing := range []int{-1, 9, 20} {
		if PlanSocialCrop(crop, climate, existing) != 0 {
			t.Fatal(existing)
		}
	}
	climate.DaysRemaining = domain.Known(1.0)
	if PlanSocialCrop(crop, climate, 0) != 0 {
		t.Fatal("short season")
	}
	if _, _, ok := viableCrops(FieldRequest{Choices: []CropChoice{crop}, Climate: climate, Coverage: domain.Known(0.0)}, false); !ok {
		t.Fatal("invalid test request")
	}
	viable, _, _ := viableCrops(FieldRequest{Choices: []CropChoice{crop}, Climate: climate, Coverage: domain.Known(0.0)}, false)
	if len(viable) != 0 {
		t.Fatal("social crop counted as food")
	}
}
