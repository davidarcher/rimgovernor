package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestArtSaleWanted(t *testing.T) {
	need := domain.Known(TradeNeed{MedicineReplenish: 10}) // 180 silver
	colonists := domain.Known[int64](3)                    // reserve 300
	for _, tc := range []struct {
		name     string
		headroom domain.Fact[float64]
		need     domain.Fact[TradeNeed]
		silver   int64
		want     bool
	}{
		{"headroom and a silver need", domain.Known(500.0), need, 100, true},
		{"negative headroom", domain.Known(-1.0), need, 100, false},
		{"zero headroom", domain.Known(0.0), need, 100, false},
		{"unknown headroom", domain.Unknown[float64](), need, 100, false},
		{"silver covers price plus reserve", domain.Known(500.0), need, 480, false},
		{"surplus only is no purchase", domain.Known(500.0), domain.Known(TradeNeed{Surplus: []Amount{{Resource: "Steel", Count: 9}}}), 0, false},
		{"shortfall counts", domain.Known(500.0), domain.Known(TradeNeed{Shortfall: []Amount{{Resource: "Steel", Count: 100}}}), 400, true},
	} {
		if got := ArtSaleWanted(CoreItemFacts(), tc.headroom, tc.need, domain.Known(tc.silver), colonists); got != tc.want {
			t.Errorf("%s: got %v", tc.name, got)
		}
	}
}

func TestSaleArtBills(t *testing.T) {
	bench := artBench()
	bench.Recipes = append(bench.Recipes, ProductionRecipe{Name: "Make_SculptureLarge", Role: domain.RoleSculpture, Available: domain.Known(true)}, ProductionRecipe{Name: "Make_SculptureGrand", Role: domain.RoleSculpture, Available: domain.Known(true)})
	benches := domain.Known([]ProductionBench{bench})
	artists := Artists([]PawnProfile{artProfile("a", 8, ""), artProfile("b", 2, "Minor")})
	demand := ArtDemand{Sale: true, Items: CoreItemFacts(), Stock: map[Resource]int64{"Gold": 200, "Steel": 1000}, Skill: map[PawnID]int{"a": 8, "b": 2}}
	got := SelectArtBills(benches, domain.Known[int64](2), artists, demand)
	if len(got) != 2 || got[0].Recipe != "Make_SculptureLarge" || got[0].Ingredients[0] != "Gold" || got[1].Recipe != SculptureRecipe || got[1].Ingredients[0] != "Gold" {
		t.Fatalf("sale bills = %+v", got)
	}
	// Negative headroom: the review asks for no sale.
	needFacts := domain.Known(TradeNeed{ComponentShortfall: 5})
	demand.Sale = ArtSaleWanted(CoreItemFacts(), domain.Known(-10.0), needFacts, domain.Known[int64](0), domain.Known[int64](2))
	if demand.Sale {
		t.Fatal("negative headroom wants a sale")
	}
}
