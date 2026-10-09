package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func clubBench(id string) GearBench {
	return GearBench{ID: id, Def: "CraftingSpot", Recipes: domain.Known([]GearRecipe{{Definition: "Make_MeleeWeapon_Club", Products: []Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Known(true)}})}
}

func TestBenchCapGrowsWithColonistsAndIsBounded(t *testing.T) {
	for colonists, want := range map[int]int{-1: 1, 0: 1, 3: 1, 4: 2, 8: 3, 12: 4, 100: MaxBenchesPerKind} {
		if got := BenchCap(colonists); got != want {
			t.Fatalf("BenchCap(%d) = %d, want %d", colonists, got, want)
		}
	}
}

func TestFurtherBenchKindsTriggerOnlyOnBenchBoundReasonsBelowTheCap(t *testing.T) {
	benches := []GearBench{clubBench("a")}
	unmet := func(reason UnmetReason, short float64) []UnmetThroughput {
		return []UnmetThroughput{{BenchKind: "CraftingSpot", ShortPerDay: short, Reason: reason}}
	}
	for _, reason := range []UnmetReason{UnmetNoBench, UnmetBenchesExhausted} {
		if got := FurtherBenchKinds(unmet(reason, 2), benches, 8); len(got) != 1 || got[0] != "CraftingSpot" {
			t.Fatalf("%s: got %v", reason, got)
		}
	}
	for _, reason := range []UnmetReason{UnmetSlotsFull, UnmetIngredients, UnmetHaul} {
		if got := FurtherBenchKinds(unmet(reason, 2), benches, 8); len(got) != 0 {
			t.Fatalf("%s must not trigger: %v", reason, got)
		}
	}
	if got := FurtherBenchKinds(unmet(UnmetBenchesExhausted, 0), benches, 8); len(got) != 0 {
		t.Fatalf("no shortfall triggered: %v", got)
	}
	// Four colonists cap a kind at two: one standing bench may grow, two may not.
	if got := FurtherBenchKinds(unmet(UnmetBenchesExhausted, 2), benches, 4); len(got) != 1 {
		t.Fatalf("below cap: %v", got)
	}
	if got := FurtherBenchKinds(unmet(UnmetBenchesExhausted, 2), append(benches, clubBench("b")), 4); len(got) != 0 {
		t.Fatalf("at cap: %v", got)
	}
}

func TestSelectWorkshopBenchStagesAFurtherBenchOnlyWhenCapacityFalls(t *testing.T) {
	benches := []GearBench{clubBench("a")}
	request := WorkshopRequest{Resource: "MeleeWeapon_Club", Benches: domain.Known(benches), Hosts: workshopHosts(), Definitions: workshopDefinitions()}
	// Capacity suffices: reuse still wins.
	if choice, err := SelectWorkshopBench(request); err != nil || choice.Method != WorkshopExisting {
		t.Fatal(choice, err)
	}
	// Capacity short: another bench of the same kind through the build rung.
	request.Further = []string{"CraftingSpot"}
	if choice, err := SelectWorkshopBench(request); err != nil || choice.Method != WorkshopBuild || choice.Definition != "CraftingSpot" {
		t.Fatal(choice, err)
	}
	// A further bench that cannot be staged falls back to reuse.
	request.Definitions = []BenchDefinition{{Name: "CraftingSpot", Available: domain.Known(false), NeedsPower: domain.Known(false), ConstructionSkill: domain.Known[int32](0)}}
	if choice, err := SelectWorkshopBench(request); err != nil || choice.Method != WorkshopExisting {
		t.Fatal(choice, err)
	}
}
