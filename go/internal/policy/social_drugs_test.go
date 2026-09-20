package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
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
	site := farmSiteFixture()
	crop := site.Crop
	crop.Name, crop.Available, crop.Edible = "Plant_Hops", domain.Known(true), domain.Known(false)
	crop.HarvestNutrition = domain.Known(0.0)
	climate := CropClimate{Sowing: domain.Known(true), DaysRemaining: domain.Known(30.0)}
	got := PlanSocialCrop(crop, climate, 4, site)
	if got.Cells == 0 || got.Cells > 5 {
		t.Fatal(got)
	}
	for _, existing := range []int{-1, 9, 20} {
		if PlanSocialCrop(crop, climate, existing, site).Cells != 0 {
			t.Fatal(existing)
		}
	}
	climate.DaysRemaining = domain.Known(1.0)
	if PlanSocialCrop(crop, climate, 0, site).Cells != 0 {
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
