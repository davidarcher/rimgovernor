package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Art benches are the gear benches offering the small sculpture; their
// bills carry the pinned worker into the art selection.
func TestArtBenchesFromGearBenches(t *testing.T) {
	sculpt := policy.GearRecipe{Definition: "Make_SculptureSmall", Role: domain.RoleSculpture, Available: domain.Known(true), AvailableOn: domain.Known(true)}
	club := policy.GearRecipe{Definition: "Make_MeleeWeapon_Club", Available: domain.Known(true), AvailableOn: domain.Known(true)}
	reads := []bridge.GearBenchRead{
		{Token: "t1", Bench: policy.GearBench{ID: "TableSculpting_1", Recipes: domain.Known([]policy.GearRecipe{sculpt}),
			Bills: domain.Known([]policy.GearBill{{ID: "Bill_1", Role: domain.RoleSculpture, Recipe: "Make_SculptureSmall", Active: domain.Known(true), Worker: domain.Known("a")}})}},
		{Token: "t2", Bench: policy.GearBench{ID: "CraftingSpot_1", Recipes: domain.Known([]policy.GearRecipe{club}), Bills: domain.Known([]policy.GearBill{})}},
		{Token: "t3", Bench: policy.GearBench{ID: "TableSculpting_2", Recipes: domain.Known([]policy.GearRecipe{sculpt})}},
	}
	benches := artBenches(reads)
	if len(benches) != 1 || benches[0].ID != "TableSculpting_1" || len(benches[0].Bills) != 1 {
		t.Fatalf("benches = %+v", benches)
	}
	got := policy.SelectArtBills(domain.Known(benches), domain.Known[int64](2), []policy.PawnID{"a", "b"}, policy.ArtDemand{Items: policy.CoreItemFacts()})
	if len(got) != 1 || got[0].Worker != "b" || got[0].Token != "t1" {
		t.Fatalf("bills = %+v", got)
	}

	if art := artBills(benches); !art.sculpting || art.finished["a"] != 0 {
		t.Fatalf("an active sculpture bill is sculpting (#1195): %+v", art)
	}
	benches[0].Bills[0].Active = domain.Known(false)
	if art := artBills(benches); art.sculpting || art.finished["a"] != 1 {
		t.Fatalf("a finished sculpture bill is a finished batch: %+v", art)
	}
}
