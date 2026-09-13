package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestResourceRecipeDeficitsRejectsUnknownIngredients(t *testing.T) {
	if _, err := ResourceRecipeDeficits(domain.Unknown[[][]Amount](), nil); err == nil {
		t.Fatalf("expected an error for unknown ingredients")
	}
}

func TestResourceRecipeDeficitsComputesPerAlternativeDeficit(t *testing.T) {
	ingredients := domain.Known([][]Amount{
		{{Resource: "Steel", Count: 50}, {Resource: "Plasteel", Count: 20}},
		{{Resource: "WoodLog", Count: 10}},
	})
	stock := map[Resource]int64{"Steel": 30, "Plasteel": 25, "WoodLog": 4}
	got, err := ResourceRecipeDeficits(ingredients, stock)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := [][]ResourceRequirement{
		{{Resource: "Steel", Required: 50, Deficit: 20}, {Resource: "Plasteel", Required: 20, Deficit: 0}},
		{{Resource: "WoodLog", Required: 10, Deficit: 6}},
	}
	if len(got) != len(want) {
		t.Fatalf("got %v want %v", got, want)
	}
	for i := range want {
		if len(got[i]) != len(want[i]) {
			t.Fatalf("slot %d: got %v want %v", i, got[i], want[i])
		}
		for j := range want[i] {
			if got[i][j] != want[i][j] {
				t.Fatalf("slot %d choice %d: got %v want %v", i, j, got[i][j], want[i][j])
			}
		}
	}
}

func TestResourceRecipeDeficitsMissingStockTreatedAsZero(t *testing.T) {
	ingredients := domain.Known([][]Amount{{{Resource: "Uranium", Count: 5}}})
	got, err := ResourceRecipeDeficits(ingredients, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got[0][0].Deficit != 5 {
		t.Fatalf("got %v", got)
	}
}

func TestResourceExtractionAdvancedDetectsMiningProgress(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 80}}
	if !ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected mining progress to be detected")
	}
}

func TestResourceExtractionAdvancedDetectsDrillingProgress(t *testing.T) {
	prior := []DrillingProgress{{ThingID: "drill1", Progress: 0.2}}
	current := []DrillingProgress{{ThingID: "drill1", Progress: 0.5}}
	if !ResourceExtractionAdvanced(10, 5, nil, nil, current, prior) {
		t.Fatalf("expected drilling progress to be detected")
	}
}

func TestResourceExtractionAdvancedNoChange(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	if ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected no progress")
	}
}

func TestResourceExtractionAdvancedIncreaseInHitPointsIsNotProgress(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 50}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	if ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected an increase in hit points to not count as progress")
	}
}

func TestResourceExtractionAdvancedRejectsStaleTick(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock1", HitPoints: 10}}
	if ResourceExtractionAdvanced(4, 5, current, prior, nil, nil) {
		t.Fatalf("expected a tick before the last observed progress to never establish an advance")
	}
}

func TestResourceExtractionAdvancedUnseenIdentityIsNotProgress(t *testing.T) {
	prior := []MiningProgress{{ThingID: "rock1", HitPoints: 100}}
	current := []MiningProgress{{ThingID: "rock2", HitPoints: 10}}
	if ResourceExtractionAdvanced(10, 5, current, prior, nil, nil) {
		t.Fatalf("expected a newly observed identity with no prior baseline to not count as progress")
	}
}

func TestSelectResourceSourcesNoDeficitSelectsNothing(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Yield: 50, Distance: 1}}
	if got := SelectResourceSources(sources, 100, 100, 0); got != nil {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesAccountsForStockAndPending(t *testing.T) {
	sources := []ResourceSource{{ThingID: "a", Yield: 10, Distance: 1}}
	// target 100, stock 95, pending 5 -> needed 0
	if got := SelectResourceSources(sources, 100, 95, 5); got != nil {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesOrdersByDistanceThenThingID(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "far", Yield: 100, Distance: 10},
		{ThingID: "near", Yield: 100, Distance: 1},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "near" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesSkipsDesignatedAndZeroYield(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "designated", Yield: 100, Distance: 1, Designated: true},
		{ThingID: "empty", Yield: 0, Distance: 2},
		{ThingID: "usable", Yield: 100, Distance: 3},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "usable" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesCombinesMultipleUntilCovered(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "a", Yield: 30, Distance: 1},
		{ThingID: "b", Yield: 30, Distance: 2},
		{ThingID: "c", Yield: 30, Distance: 3},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 2 || got[0].ThingID != "a" || got[1].ThingID != "b" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesMineRequiresOpenSurfaceSafety(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "unsafe", Yield: 100, Distance: 1, Method: ResourceSourceMine, Safety: "unknown"},
		{ThingID: "safe", Yield: 100, Distance: 2, Method: ResourceSourceMine, Safety: "open_surface"},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "safe" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesAtMostOneMinePerCall(t *testing.T) {
	sources := []ResourceSource{
		{ThingID: "mine1", Yield: 10, Distance: 1, Method: ResourceSourceMine, Safety: "open_surface"},
		{ThingID: "mine2", Yield: 10, Distance: 2, Method: ResourceSourceMine, Safety: "open_surface"},
		{ThingID: "surface", Yield: 100, Distance: 3},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "mine1" {
		t.Fatalf("expected only the nearest mine source and nothing after it, got %v", got)
	}
}

func TestSelectResourceSourcesMineNeverFollowsAnyPriorSelection(t *testing.T) {
	// A mine source is only ever added when nothing has been selected yet --
	// ordinary sources selected first stop the loop before a later mine
	// source is reached, matching production_policy.py's
	// "if method == 'mine' and selected: break".
	sources := []ResourceSource{
		{ThingID: "surface", Yield: 10, Distance: 1},
		{ThingID: "mine1", Yield: 100, Distance: 2, Method: ResourceSourceMine, Safety: "open_surface"},
	}
	got := SelectResourceSources(sources, 50, 0, 0)
	if len(got) != 1 || got[0].ThingID != "surface" {
		t.Fatalf("got %v", got)
	}
}

func TestSelectResourceSourcesCapAtEight(t *testing.T) {
	var sources []ResourceSource
	for i := 0; i < 12; i++ {
		sources = append(sources, ResourceSource{ThingID: string(rune('a' + i)), Yield: 1, Distance: float64(i)})
	}
	got := SelectResourceSources(sources, 1000, 0, 0)
	if len(got) != 8 {
		t.Fatalf("got %d sources", len(got))
	}
}
