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

func TestSelectWorkshopBenchReportsTheResearchGatingTheFirstCandidate(t *testing.T) {
	hosts := []RecipeHost{
		{Definition: "Make_MeleeWeapon_Gladius", Products: []Resource{"MeleeWeapon_Gladius"}, Available: true, Benches: []string{"ElectricSmithy", "FueledSmithy"}},
		{Definition: "Make_ComponentIndustrial", Products: []Resource{"ComponentIndustrial"}, Available: false, Benches: []string{"FabricationBench"}, Research: []string{"Fabrication"}},
		{Definition: "Make_Mystery", Products: []Resource{"Mystery"}, Available: false, Benches: []string{"CraftingSpot"}},
	}
	definitions := []BenchDefinition{
		{Name: "CraftingSpot", Available: domain.Known(true), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known[int32](0)},
		{Name: "FueledSmithy", Available: domain.Known(false), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known[int32](0), Research: []string{"Smithing"}},
		{Name: "ElectricSmithy", Available: domain.Known(false), NeedsPower: domain.Known(true), ConstructionSkill: domain.Known[int32](0), Research: []string{"Smithing", "Electricity"}},
		{Name: "FabricationBench", Available: domain.Known(false), NeedsPower: domain.Known(true), ConstructionSkill: domain.Known[int32](0), Research: []string{"Fabrication"}},
	}
	request := WorkshopRequest{Resource: "MeleeWeapon_Gladius", Benches: domain.Known([]GearBench{}), Hosts: hosts, Definitions: definitions}
	// Without power only the fueled smithy's research is the next rung.
	choice, err := SelectWorkshopBench(request)
	if err != nil || choice.Method != WorkshopResearch || choice.Definition != "FueledSmithy" || choice.Recipe != "Make_MeleeWeapon_Gladius" || len(choice.Research) != 1 || choice.Research[0] != "Smithing" || choice.NeedsPower {
		t.Fatal(choice, err)
	}
	// With a generator available the powered smithy is considered too, but
	// the bench order still names the unpowered one first.
	request.Power = domain.Known(true)
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopResearch || choice.Definition != "ElectricSmithy" || len(choice.Research) != 2 || choice.Research[0] != "Electricity" || choice.Research[1] != "Smithing" || !choice.NeedsPower {
		t.Fatal(choice)
	}
	// Recipe-level research joins the bench's.
	request = WorkshopRequest{Resource: "ComponentIndustrial", Benches: domain.Known([]GearBench{}), Hosts: hosts, Definitions: definitions, Power: domain.Known(true)}
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopResearch || choice.Definition != "FabricationBench" || len(choice.Research) != 1 || choice.Research[0] != "Fabrication" {
		t.Fatal(choice)
	}
	// A gate the census cannot name is not research.
	request.Resource = "Mystery"
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopUnavailable {
		t.Fatal(choice)
	}
	// Once research finishes the bench is staged; a powered bench is staged
	// only with a generator available.
	definitions[2].Available = domain.Known(true)
	request = WorkshopRequest{Resource: "MeleeWeapon_Gladius", Benches: domain.Known([]GearBench{}), Hosts: hosts, Definitions: definitions}
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopResearch || choice.Definition != "FueledSmithy" {
		t.Fatal("powered bench staged without a generator", choice)
	}
	request.Power = domain.Known(true)
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopBuild || choice.Definition != "ElectricSmithy" || !choice.NeedsPower {
		t.Fatal(choice)
	}
	definitions[1].Available = domain.Known(true)
	if choice, _ = SelectWorkshopBench(request); choice.Method != WorkshopBuild || choice.Definition != "FueledSmithy" || choice.NeedsPower {
		t.Fatal("unpowered bench should be preferred", choice)
	}
	if got := WorkshopResearchCandidates("ComponentIndustrial", hosts); len(got) != 1 || got[0] != "FabricationBench" {
		t.Fatal(got)
	}
}

func TestSelectWorkshopBenchNeedsABuilderForASkilledBench(t *testing.T) {
	hosts := []RecipeHost{{Definition: "Make_MeleeWeapon_Gladius", Products: []Resource{"MeleeWeapon_Gladius"}, Available: true, Benches: []string{"FueledSmithy"}}}
	definitions := []BenchDefinition{{Name: "FueledSmithy", Available: domain.Known(true), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known[int32](4)}}
	request := WorkshopRequest{Resource: "MeleeWeapon_Gladius", Benches: domain.Known([]GearBench{}), Hosts: hosts, Definitions: definitions}
	if choice, err := SelectWorkshopBench(request); err != nil || choice.Method != WorkshopUnavailable {
		t.Fatal("unknown builders must not stage a skilled bench", choice, err)
	}
	request.BuilderSkill = domain.Known[int32](3)
	if choice, _ := SelectWorkshopBench(request); choice.Method != WorkshopUnavailable {
		t.Fatal(choice)
	}
	request.BuilderSkill = domain.Known[int32](4)
	if choice, _ := SelectWorkshopBench(request); choice.Method != WorkshopBuild || choice.Definition != "FueledSmithy" {
		t.Fatal(choice)
	}
	// The research rung applies the same gate.
	definitions[0].Available = domain.Known(false)
	definitions[0].Research = []string{"Smithing"}
	if choice, _ := SelectWorkshopBench(request); choice.Method != WorkshopResearch {
		t.Fatal(choice)
	}
	request.BuilderSkill = domain.Known[int32](1)
	if choice, _ := SelectWorkshopBench(request); choice.Method != WorkshopUnavailable {
		t.Fatal(choice)
	}
}

func TestBuilderSkillIsTheBestEnabledConstructionLevel(t *testing.T) {
	pawns := []WorkPawn{
		{ID: "a", Available: domain.Known(true), Applies: domain.Known(true), Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 9, Disabled: true}})},
		{ID: "b", Available: domain.Known(true), Applies: domain.Known(true), Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 4}})},
		{ID: "c", Available: domain.Known(false), Applies: domain.Known(true), Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 12}})},
	}
	if v, known := BuilderSkill(domain.Known(pawns)).Value(); !known || v != 4 {
		t.Fatal(v, known)
	}
	pawns[1].Skills = domain.Unknown[[]WorkSkill]()
	if _, known := BuilderSkill(domain.Known(pawns)).Value(); known {
		t.Fatal("unread skills must be unknown")
	}
	if _, known := BuilderSkill(domain.Unknown[[]WorkPawn]()).Value(); known {
		t.Fatal("unread roster must be unknown")
	}
}
