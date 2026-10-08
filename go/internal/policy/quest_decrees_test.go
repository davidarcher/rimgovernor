package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func TestDecreeProductionUsesCraftedProgressAndExactStuff(t *testing.T) {
	objective := QuestObjective{Def: "Armor", Stuff: "Steel"}
	recipe := DecreeRecipe{Bench: "bench", Recipe: "MakeArmor", Product: "Armor", Units: 2, Available: domain.Known(true), Stuff: map[Resource]bool{"Steel": true, "WoodLog": true}, Ingredients: domain.Known([][]Amount{{{Resource: "Steel", Count: 5}, {Resource: "WoodLog", Count: 5}}, {{Resource: "Component", Count: 1}}})}
	stock := domain.Known([]Amount{{Resource: "Armor", Count: 100}, {Resource: "Steel", Count: 20}, {Resource: "Component", Count: 4}})
	bill, reason := PlanDecreeProduction(objective, 7, []DecreeRecipe{recipe}, stock)
	if reason != "" || bill.Bench() != "bench" || bill.Recipe() != "MakeArmor" || bill.Mode() != domain.GearBatch || bill.Target() != 4 || !reflect.DeepEqual(bill.Ingredients(), []string{"Component", "Steel"}) {
		t.Fatalf("%+v %s", bill, reason)
	}
	for name, edit := range map[string]func(*DecreeRecipe){"unavailable": func(r *DecreeRecipe) { r.Available = domain.Known(false) }, "unknown ingredients": func(r *DecreeRecipe) { r.Ingredients = domain.Unknown[[][]Amount]() }, "wrong output": func(r *DecreeRecipe) { r.Product = "Other" }} {
		t.Run(name, func(t *testing.T) {
			r := recipe
			edit(&r)
			if _, reason := PlanDecreeProduction(objective, 7, []DecreeRecipe{r}, stock); reason != "production_infeasible" {
				t.Fatal(reason)
			}
		})
	}
	if _, reason := PlanDecreeProduction(objective, 9, []DecreeRecipe{recipe}, stock); reason != "production_infeasible" {
		t.Fatal("unfunded extra iteration", reason)
	}
	recipe.Existing = true
	if _, reason := PlanDecreeProduction(objective, 7, []DecreeRecipe{recipe}, stock); reason != "existing_work" {
		t.Fatal(reason)
	}
}

func TestDecreeHarvestSizesExactPlantCount(t *testing.T) {
	f := fieldRequest(1)
	crop := resourceCrop("Plant_Cotton", "Cloth", 5.8, 8)
	plan, reason := PlanDecreeHarvest(QuestObjective{Def: crop.Name}, 7, 0, []CropChoice{crop}, f.Climate, f.Site, domain.Known(int32(10)))
	if reason != "" || plan.Crop.Name != crop.Name || plan.Needed != 7 || plan.Sites.Cells != 7 {
		t.Fatalf("%+v %s", plan, reason)
	}
	if _, reason := PlanDecreeHarvest(QuestObjective{Def: crop.Name}, 7, 7, []CropChoice{crop}, f.Climate, f.Site, domain.Known(int32(10))); reason != "existing_work" {
		t.Fatal(reason)
	}
	f.Climate.Sowing = domain.Known(false)
	if _, reason := PlanDecreeHarvest(QuestObjective{Def: crop.Name}, 7, 0, []CropChoice{crop}, f.Climate, f.Site, domain.Known(int32(10))); reason != "plant_infeasible" {
		t.Fatal(reason)
	}
}

func TestDecreeHuntNeedsViolenceAndExactWildSpecies(t *testing.T) {
	objective := QuestObjective{Def: "Deer"}
	prey := domain.Known([]AcquisitionSource{{ID: "b", Definition: "Deer", Resource: "Corpse_Deer", Hunt: true}, {ID: "a", Definition: "Deer", Resource: "Corpse_Deer", Hunt: true}, {ID: "tame", Definition: "Deer", Hunt: false}, {ID: "elk", Definition: "Elk", Hunt: true}})
	selected, reason := PlanDecreeHunt(objective, 2, domain.Known(true), prey)
	if reason != "" || len(selected) != 2 || selected[0].ID != "a" || selected[1].ID != "b" {
		t.Fatalf("%+v %s", selected, reason)
	}
	if _, reason := PlanDecreeHunt(objective, 2, domain.Known(false), prey); reason != "violent_quests_off" {
		t.Fatal(reason)
	}
	if _, reason := PlanDecreeHunt(objective, 3, domain.Known(true), prey); reason != "species_absent" {
		t.Fatal(reason)
	}
}

func TestDecreeObjectiveUsesNativeActiveProgress(t *testing.T) {
	profile := QuestFamilyForRoot("Decree_ProduceItem")
	offer := JoinerOffer{State: "Ongoing", Profile: domain.Known(profile), Objectives: []QuestObjective{{Kind: o.QuestObjectiveKind_QUEST_OBJECTIVE_KIND_PRODUCE_ITEM, Def: "Armor", Count: domain.Known(int64(10)), Produced: domain.Known(int64(3)), Active: domain.Known(true)}}}
	if _, remaining, owed := DecreeObjective(offer); !owed || remaining != 7 {
		t.Fatal(remaining, owed)
	}
	offer.Objectives[0].Active = domain.Known(false)
	if _, _, owed := DecreeObjective(offer); owed {
		t.Fatal("disabled objective drove work")
	}
	offer.Objectives[0].Active = domain.Known(true)
	offer.Objectives[0].Produced = domain.Known(int64(10))
	if _, _, owed := DecreeObjective(offer); owed {
		t.Fatal("complete objective drove work")
	}
}
