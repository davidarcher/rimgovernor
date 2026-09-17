package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func growerFixture(id, crop string) PlantGrower {
	return PlantGrower{ID: id, Definition: "HydroponicsBasin", Fertility: domain.Known(2.8), SowTag: domain.Known("Hydroponic"), Crop: domain.Known(crop), CanSow: domain.Known(true)}
}

func TestPlanGrowerCropsReCropsToTheBestHydroponicCrop(t *testing.T) {
	f := siteFixture(1.0).Field
	// A basin sowing potato re-crops to rice, the best rate over basin
	// fertility; one already on rice is left alone.
	choices := PlanGrowerCrops(GrowerCropRequest{Choices: f.Choices, Growers: []PlantGrower{growerFixture("basin-2", "Plant_Potato"), growerFixture("basin-1", "Plant_Rice")}})
	if len(choices) != 1 || choices[0].Grower != "basin-2" || choices[0].Crop.Name != "Plant_Rice" || choices[0].Current != "Plant_Potato" {
		t.Fatalf("%+v", choices)
	}
	// Soil-only corn is excluded with its reason even though it scores best per cell.
	for _, c := range choices[0].Ranking {
		if c.Crop.Name == "Plant_Corn" && (c.Score != 0 || c.Reason != "crop cannot be sown on this grower") {
			t.Fatal(c.Reason)
		}
	}
	// Rice unavailable: potato is the only remaining basin crop, so a rice
	// basin re-crops to potato.
	f.Choices[0].Available = domain.Known(false)
	choices = PlanGrowerCrops(GrowerCropRequest{Choices: f.Choices, Growers: []PlantGrower{growerFixture("basin-1", "Plant_Rice")}})
	if len(choices) != 1 || choices[0].Crop.Name != "Plant_Potato" {
		t.Fatalf("%+v", choices)
	}
	// Nothing sowable: no change.
	f.Choices[2].Available = domain.Known(false)
	if choices = PlanGrowerCrops(GrowerCropRequest{Choices: f.Choices, Growers: []PlantGrower{growerFixture("basin-1", "Plant_Rice")}}); len(choices) != 0 {
		t.Fatalf("%+v", choices)
	}
}

func TestPlanGrowerCropsLeavesUnknownGrowersAlone(t *testing.T) {
	f := siteFixture(1.0).Field
	unknownCrop := growerFixture("basin-1", "Plant_Potato")
	unknownCrop.Crop = domain.Unknown[string]()
	cannotSow := growerFixture("basin-2", "Plant_Potato")
	cannotSow.CanSow = domain.Known(false)
	noTag := growerFixture("basin-3", "Plant_Potato")
	noTag.SowTag = domain.Unknown[string]()
	if choices := PlanGrowerCrops(GrowerCropRequest{Choices: f.Choices, Growers: []PlantGrower{unknownCrop, cannotSow, noTag}}); len(choices) != 0 {
		t.Fatalf("%+v", choices)
	}
	// Urgency prefers the fastest sowable crop; the fixture's rice is both
	// fastest and best, so make potato faster to see the order flip.
	f.Choices[2].GrowDays = domain.Known(2.0)
	choices := PlanGrowerCrops(GrowerCropRequest{Choices: f.Choices, Growers: []PlantGrower{growerFixture("basin-1", "Plant_Rice")}, Urgent: true})
	if len(choices) != 1 || choices[0].Crop.Name != "Plant_Potato" {
		t.Fatalf("%+v", choices)
	}
}
