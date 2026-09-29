package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestAssessArmoryTiers(t *testing.T) {
	all := domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Smithing", "Machining", "Fabrication"}})
	for _, tc := range []struct {
		name     string
		points   domain.Fact[float64]
		research domain.Fact[ResearchFacts]
		want     ArmoryAssessment
	}{
		{"unknown points", domain.Unknown[float64](), all, ArmoryAssessment{ArmoryTierUnknown, ArmoryTierFabrication, ArmoryTierUnknown}},
		{"NaN points", domain.Known(math.NaN()), all, ArmoryAssessment{ArmoryTierUnknown, ArmoryTierFabrication, ArmoryTierUnknown}},
		{"low", domain.Known(35.0), all, ArmoryAssessment{ArmoryTierNeolithic, ArmoryTierFabrication, ArmoryTierNeolithic}},
		{"smithing threshold", domain.Known(300.0), all, ArmoryAssessment{ArmoryTierSmithing, ArmoryTierFabrication, ArmoryTierSmithing}},
		{"machining threshold", domain.Known(800.0), all, ArmoryAssessment{ArmoryTierMachining, ArmoryTierFabrication, ArmoryTierMachining}},
		{"just below fabrication", domain.Known(2499.0), all, ArmoryAssessment{ArmoryTierMachining, ArmoryTierFabrication, ArmoryTierMachining}},
		{"fabrication", domain.Known(2500.0), all, ArmoryAssessment{ArmoryTierFabrication, ArmoryTierFabrication, ArmoryTierFabrication}},
		{"research caps", domain.Known(5000.0), domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Smithing"}}), ArmoryAssessment{ArmoryTierFabrication, ArmoryTierSmithing, ArmoryTierSmithing}},
		{"gap stops ladder", domain.Known(5000.0), domain.Known(ResearchFacts{Finished: []ResearchProjectID{"Machining", "Fabrication"}}), ArmoryAssessment{ArmoryTierFabrication, ArmoryTierNeolithic, ArmoryTierNeolithic}},
		{"unknown research", domain.Known(5000.0), domain.Unknown[ResearchFacts](), ArmoryAssessment{ArmoryTierFabrication, ArmoryTierNeolithic, ArmoryTierNeolithic}},
	} {
		if got := AssessArmory(tc.points, tc.research); got != tc.want {
			t.Errorf("%s: got %+v want %+v", tc.name, got, tc.want)
		}
	}
}
