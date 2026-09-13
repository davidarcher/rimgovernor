package policy

import "testing"

func TestResearchNeedsDedupesAndOrdersByPriorityThenIdentity(t *testing.T) {
	sources := []ResearchNeedSource{
		{Goal: "MaintainResource-Steel", PriorityClass: 4, UnavailableThings: []string{"DeepDrill"}},
		{Goal: "intent-outpost", PriorityClass: 2, BlockedRecipes: []string{"Smelting"}},
		{Goal: "MaintainResource-Steel", PriorityClass: 4, UnavailableThings: []string{"DeepDrill"}}, // duplicate source
	}
	got := ResearchNeeds(sources)
	want := []ResearchNeed{
		{Goal: "intent-outpost", Requirement: ResearchRequirementRecipe, Name: "Smelting"},
		{Goal: "MaintainResource-Steel", Requirement: ResearchRequirementThing, Name: "DeepDrill"},
	}
	if len(got) != len(want) {
		t.Fatalf("got %v want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("index %d: got %v want %v", i, got[i], want[i])
		}
	}
}

func TestResearchNeedsOrdersRecipeBeforeThingWithinSameGoal(t *testing.T) {
	sources := []ResearchNeedSource{
		{Goal: "MaintainEquipment", PriorityClass: 3, UnavailableThings: []string{"Smithy"}, BlockedRecipes: []string{"MakeSteelKnife"}},
	}
	got := ResearchNeeds(sources)
	if len(got) != 2 || got[0].Requirement != ResearchRequirementRecipe || got[1].Requirement != ResearchRequirementThing {
		t.Fatalf("got %v", got)
	}
}

func TestResearchNeedsEmpty(t *testing.T) {
	if got := ResearchNeeds(nil); len(got) != 0 {
		t.Fatalf("got %v", got)
	}
}

func TestResearchNeedsMultipleGoalsSamePriority(t *testing.T) {
	sources := []ResearchNeedSource{
		{Goal: "MaintainResource-Wood", PriorityClass: 4, UnavailableThings: []string{"WoodenWall"}},
		{Goal: "MaintainResource-Steel", PriorityClass: 4, UnavailableThings: []string{"SteelWall"}},
	}
	got := ResearchNeeds(sources)
	if len(got) != 2 || got[0].Goal != "MaintainResource-Steel" || got[1].Goal != "MaintainResource-Wood" {
		t.Fatalf("expected alphabetical goal tie-break, got %v", got)
	}
}
