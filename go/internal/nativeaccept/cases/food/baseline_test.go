package food

import (
	"encoding/json"
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases/sustained"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestBaselineKeepsNativeHuntPrerequisitesAndUnplantedSave(t *testing.T) {
	c, ok := cases.Lookup("food/ledger-baseline")
	if !ok || c.Start != (cases.Save{Name: sustained.BaselineSave}) || c.Serve == nil {
		t.Fatalf("invalid baseline registration: %+v", c)
	}
	if !slices.Contains(c.RequiredOps, "test/food_baseline_prey") {
		t.Fatal("baseline lacks deterministic native prey setup")
	}
	for _, family := range []string{"bill", "supply", "work", "equip"} {
		if !slices.Contains(c.Serve.Families, family) {
			t.Fatalf("missing native hunt prerequisite family %s", family)
		}
	}
	if slices.Contains(c.Serve.Families, "field") {
		t.Fatal("field creation invalidates the unsown-census proof")
	}
	if slices.Contains(c.Serve.Families, "acquisition") {
		t.Fatal("acquisition can exhaust forage before the ledger read")
	}
}

func TestBaselineStatusDecodesLiveNullableRatesAndTerms(t *testing.T) {
	row := map[string]any{"kind": "Forage", "id": "berries", "decision": "Open", "reason": "gap", "nutritionPerDay": 2.0, "deliveredPerDay": 1.5, "terms": ledgerRow(policy.FoodForage, policy.FoodPlanOpen).Terms}
	hunt := map[string]any{"kind": "Hunt", "id": "deer", "decision": "Open", "reason": "gap", "nutritionPerDay": 3.0, "deliveredPerDay": 2.0, "terms": ledgerRow(policy.FoodHunt, policy.FoodPlanOpen).Terms}
	for _, unknown := range []bool{false, true} {
		if unknown {
			hunt["nutritionPerDay"] = nil
		}
		body, err := json.Marshal(map[string]any{"tick": 110, "foodPlanTick": 100, "foodPlan": map[string]any{"portfolio": []any{row, hunt}, "explain": "controller terms"}})
		if err != nil {
			t.Fatal(err)
		}
		var status baselineStatus
		if err = json.Unmarshal(body, &status); err != nil {
			t.Fatal(err)
		}
		if status.FoodPlanTick == nil || *status.FoodPlanTick != 100 || status.FoodPlan.Explain != "controller terms" {
			t.Fatalf("lost tick or explain: %+v", status)
		}
		if err = CheckBaselinePlan(status.plan()); (err != nil) != unknown {
			t.Fatalf("unknown=%v: %v", unknown, err)
		}
	}
}
