package startuplabor

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBlockedTicksCreditsEachDiagnosisUntilTheNextReview(t *testing.T) {
	blocked := BlockedTicks([]Diagnosis{
		{Goal: "shelter", ReviewTick: 0, Class: ClassSlotRefusal},
		{Goal: "shelter", ReviewTick: 100, Class: ClassMaterialBlocker},
		{Goal: "shelter", ReviewTick: 300, Class: ClassProgressing},
		{Goal: "feed", ReviewTick: 0, Class: ClassSlotRefusal},
		{Goal: "feed", ReviewTick: 50, Class: ClassSlotRefusal},
	}, 0, 1000)
	if blocked[ClassSlotRefusal] != 150 || blocked[ClassMaterialBlocker] != 200 {
		t.Fatalf("blocked = %v", blocked)
	}
	// The last diagnosis of each goal observed no end and credits nothing.
	if blocked[ClassProgressing] != 0 {
		t.Fatalf("a trailing diagnosis was credited: %v", blocked)
	}
}

func TestBlockedTicksIgnoresRewindsAndBoundsGaps(t *testing.T) {
	blocked := BlockedTicks([]Diagnosis{
		{Goal: "shelter", ReviewTick: 500, Class: ClassSlotRefusal},
		{Goal: "shelter", ReviewTick: 100, Class: ClassSlotRefusal},
		{Goal: "shelter", ReviewTick: 100, Class: ClassSlotRefusal},
		{Goal: "shelter", ReviewTick: 9000, Class: ClassProgressing},
	}, 0, 1000)
	if blocked[ClassSlotRefusal] != 1000 {
		t.Fatalf("blocked = %v: a rewind, a repeat and an over-long gap must not inflate the total", blocked)
	}
}

func TestCompareLimitsLabelsObservations(t *testing.T) {
	out := CompareLimits([]LimitObservation{
		{Limit: 8, Variant: "wood_sufficient", Seed: "s", Revision: "abc", ShelterRecovery: domain.Known[domain.Tick](900)},
		{Limit: 2, Variant: "wood_sufficient", Seed: "s", Revision: "abc"},
	})
	runs, _ := out["runs"].([]map[string]any)
	if len(runs) != 2 || runs[0]["limit"] != 2 || runs[1]["limit"] != 8 {
		t.Fatalf("runs = %v", runs)
	}
	if runs[0]["shelter_recovery_tick"] != nil {
		t.Fatalf("an unobserved recovery must stay unknown: %v", runs[0])
	}
	if runs[1]["shelter_recovery_tick"] != domain.Tick(900) {
		t.Fatalf("runs = %v", runs)
	}
	if label, _ := out["label"].(string); label == "" {
		t.Fatal("comparison is unlabelled; it must not read as proof")
	}
}
