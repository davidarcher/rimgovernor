package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func workshopHosts() []RecipeHost {
	return []RecipeHost{
		{Definition: "Make_MeleeWeapon_Club", Products: []Resource{"MeleeWeapon_Club"}, Available: true, Benches: []string{"CraftingSpot"}},
		{Definition: "Make_StoneBlocksGranite", Products: []Resource{"BlocksGranite"}, Available: false, Benches: []string{"TableStonecutter"}},
		{Definition: "Make_MeleeWeapon_Club_Electric", Products: []Resource{"MeleeWeapon_Club"}, Available: true, Benches: []string{"ElectricSmithy"}},
	}
}

func workshopDefinitions() []BenchDefinition {
	return []BenchDefinition{
		{Name: "CraftingSpot", Available: domain.Known(true), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known[int32](0)},
		{Name: "ElectricSmithy", Available: domain.Known(true), NeedsPower: domain.Known(true), ConstructionSkill: domain.Known[int32](0)},
		{Name: "TableStonecutter", Available: domain.Known(false), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known[int32](0)},
	}
}

func TestWorkshopBenchCandidatesFollowAvailableRecipesForTheResource(t *testing.T) {
	got := WorkshopBenchCandidates("MeleeWeapon_Club", workshopHosts())
	if len(got) != 2 || got[0] != "CraftingSpot" || got[1] != "ElectricSmithy" {
		t.Fatal(got)
	}
	if got := WorkshopBenchCandidates("BlocksGranite", workshopHosts()); len(got) != 0 {
		t.Fatal("research-gated recipe offered a bench", got)
	}
}

func TestSelectWorkshopBenchReusesAnExistingBenchFirst(t *testing.T) {
	benches := []GearBench{{ID: "spot1", Recipes: domain.Known([]GearRecipe{{Definition: "Make_MeleeWeapon_Club", Products: []Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Known(true)}})}}
	choice, err := SelectWorkshopBench(WorkshopRequest{Resource: "MeleeWeapon_Club", Benches: domain.Known(benches), Hosts: workshopHosts(), Definitions: workshopDefinitions()})
	if err != nil || choice.Method != WorkshopExisting || choice.Recipe != "Make_MeleeWeapon_Club" {
		t.Fatal(choice, err)
	}
	benches[0].Recipes = domain.Known([]GearRecipe{{Definition: "Make_MeleeWeapon_Club", Products: []Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Unknown[bool]()}})
	if choice, _ = SelectWorkshopBench(WorkshopRequest{Resource: "MeleeWeapon_Club", Benches: domain.Known(benches), Hosts: workshopHosts(), Definitions: workshopDefinitions()}); choice.Method != WorkshopUnknown {
		t.Fatal("unobserved bench availability decided", choice)
	}
}

func TestSelectWorkshopBenchStagesTheFirstUnpoweredBuildableBench(t *testing.T) {
	request := WorkshopRequest{Resource: "MeleeWeapon_Club", Benches: domain.Known([]GearBench{}), Hosts: workshopHosts(), Definitions: workshopDefinitions()}
	choice, err := SelectWorkshopBench(request)
	if err != nil || choice.Method != WorkshopBuild || choice.Definition != "CraftingSpot" || choice.Recipe != "Make_MeleeWeapon_Club" {
		t.Fatal(choice, err)
	}
	request.Definitions = request.Definitions[1:]
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopUnknown {
		t.Fatal("missing census definition should be unknown, not skipped", choice)
	}
	request.Resource = "BlocksGranite"
	request.Definitions = workshopDefinitions()
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopUnavailable {
		t.Fatal("research-gated bench should be unavailable", choice)
	}
	request.Resource = "MeleeWeapon_Club"
	request.Hosts = workshopHosts()[2:]
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopUnavailable {
		t.Fatal("powered bench should not be staged", choice)
	}
}
