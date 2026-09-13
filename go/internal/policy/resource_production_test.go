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
