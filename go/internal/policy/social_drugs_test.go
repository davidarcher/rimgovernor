package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestSocialDrugsRequireResearchAndWritablePawn(t *testing.T) {
	for _, research := range []domain.Fact[ResearchFacts]{domain.Unknown[ResearchFacts](), domain.Known(ResearchFacts{}), domain.Known(ResearchFacts{Current: "Brewing"})} {
		if len(SocialDrugTargets(research)) != 0 {
			t.Fatal("reserve before completed research")
		}
	}
	targets := SocialDrugTargets(domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Brewing"}}))
	if len(targets) != 2 || targets["Beer"] != 12 || targets["SmokeleafJoint"] != 12 {
		t.Fatal(targets)
	}
	pawn := WorkPawn{Available: domain.Known(true), DrugPolicyWritable: domain.Known(true)}
	if !DrugPolicyChange(pawn) {
		t.Fatal("default not assigned")
	}
	pawn.DrugPolicyName = SocialDrugPolicyName
	if DrugPolicyChange(pawn) {
		t.Fatal("repeated matching assignment")
	}
	pawn.DrugPolicyName, pawn.DrugPolicyWritable = "other", domain.Known(false)
	if DrugPolicyChange(pawn) {
		t.Fatal("unavailable drug tracker accepted")
	}
	pawn.DrugPolicyWritable = domain.Unknown[bool]()
	if DrugPolicyChange(pawn) {
		t.Fatal("unknown policy overwritten")
	}
}

func TestSocialCropBoundedAndNotFood(t *testing.T) {
	crop := CropChoice{Name: "Plant_Hops", Available: domain.Known(true), Edible: domain.Known(false), GrowDays: domain.Known(3.0), HarvestNutrition: domain.Known(0.0), FertilityMin: domain.Known(0.7), FertilitySensitivity: domain.Known(1.0)}
	climate := CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0)}
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
