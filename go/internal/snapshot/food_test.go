package snapshot

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// openChannels are the plan's Open rows of one kind, each with finite
// positive nutrition, the planner's explain terms and a decision reason.
func openChannels(t *testing.T, plan policy.FoodPlan, kind policy.CandidateKind) []policy.FoodPlanEntry {
	t.Helper()
	var rows []policy.FoodPlanEntry
	for _, row := range plan.Portfolio {
		if row.Channel.Kind != kind || row.Decision != policy.FoodPlanOpen {
			continue
		}
		n, known := row.Channel.Nutrition().PerDay.Value()
		if !known || math.IsNaN(n) || math.IsInf(n, 0) || n <= 0 || row.DeliveredPerDay <= 0 || row.Reason == "" {
			t.Fatalf("%s/%s lacks explained positive nutrition: %+v", kind, row.Channel.ID, row)
		}
		terms := map[string]bool{}
		for _, term := range row.Terms {
			if math.IsNaN(term.Value) || math.IsInf(term.Value, 0) || term.Value < 0 {
				t.Fatalf("%s/%s term %s = %v", kind, row.Channel.ID, term.Name, term.Value)
			}
			terms[term.Name] = true
		}
		for _, key := range []string{"nutrition_per_day", "work_per_day", "lead_days", "risk_discount", "target_cover"} {
			if !terms[key] {
				t.Fatalf("%s/%s lacks explain term %s", kind, row.Channel.ID, key)
			}
		}
		rows = append(rows, row)
	}
	if len(rows) == 0 {
		t.Fatalf("no open %s channel; %s", kind, plan.Explain())
	}
	return rows
}

// Recorded from acceptance run food/ledger-baseline at 04b0a98c (tick
// 12538): the committed tribal8 baseline with a staged wild deer, bill,
// supply, work and equip families, before any rice is planted. The live
// portfolio opens forage and hunt with positive admitted nutrition.
func TestLedgerBaselineOpensForageAndHunt(t *testing.T) {
	r, err := Load("testdata/food-ledger-baseline-forage-hunt.json")
	if err != nil {
		t.Fatal(err)
	}
	plan, known := r.Facts.FoodPlan.Value()
	if !known {
		t.Fatal("food plan unknown")
	}
	openChannels(t, plan, policy.CandidateForage)
	openChannels(t, plan, policy.CandidateHunt)
	for _, row := range plan.Portfolio {
		if row.Channel.Kind == policy.CandidateCrop && row.DeliveredPerDay > 0 {
			t.Fatal("pre-harvest plan counts crop delivery", row)
		}
	}
	if _, err = r.Detect(); err != nil {
		t.Fatal(err)
	}
}

// Recorded from acceptance run food/corpse-larder at 04b0a98c (tick 26565):
// three forbidden muffalo corpses frozen beside a forever butcher bill,
// with raw meat drained to create cook demand. The food-storage-upkeep
// larder releases exactly one frozen corpse and keeps the rest forbidden.
func TestCorpseLarderReleasesOneFrozenCorpse(t *testing.T) {
	r, err := Load("testdata/food-corpse-larder-release.json")
	if err != nil {
		t.Fatal(err)
	}
	larder, known := r.Facts.FoodStorageUpkeep.Larder.Value()
	if !known || len(larder.Corpses) != 3 {
		t.Fatal("larder census", larder, known)
	}
	choice, err := policy.SelectCorpseLarder(r.Facts.FoodStorageUpkeep)
	if err != nil || choice.Kind != "allow" || !choice.Stock.Corpse || choice.Stock.DefName != "Corpse_Muffalo" {
		t.Fatal("no corpse release", choice, err)
	}
	if forbidden, _ := choice.Stock.Forbidden.Value(); !forbidden || !choice.Handling.FrozenDestination {
		t.Fatal("released corpse was not a frozen forbidden reserve", choice)
	}
	// With cook demand met the larder releases nothing more.
	larder.CookDemandNutrition = 0
	r.Facts.FoodStorageUpkeep.Larder = domain.Known(larder)
	if again, err := policy.SelectCorpseLarder(r.Facts.FoodStorageUpkeep); err != nil || again.Kind == "allow" {
		t.Fatal("released a corpse without cook demand", again, err)
	}
}
