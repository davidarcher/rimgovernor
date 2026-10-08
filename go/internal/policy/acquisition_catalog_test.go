package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A near deposit covering the deficit outranks smelting it; a distant
// sliver of one does not.
func TestCatalogRanksMineAgainstBill(t *testing.T) {
	bill := ResourceMethod{Kind: ResourceMethodProduce, Bench: "smelter", Recipe: "SmeltSlag", Resource: "Steel", Target: 100}
	produce, ok := ProduceCandidate(bill, 60, nil)
	if !ok {
		t.Fatal("no produce candidate")
	}
	headroom := domain.Known(int64(500))
	demand := ResourceDeficitDemand("Steel", 60)
	near := MineCandidates("Steel", []ResourceSource{{ThingID: "ore-near", Yield: 60, Distance: 10, Method: ResourceSourceMine, Safety: "open_surface"}}, headroom)
	ranked, err := rankBySupply(demand, append(near, produce), AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].Kind != CandidateMining {
		t.Fatalf("near deposit %+v %v", ranked, err)
	}
	far := MineCandidates("Steel", []ResourceSource{{ThingID: "ore-far", Yield: 5, Distance: 120, Method: ResourceSourceMine, Safety: "open_surface"}}, headroom)
	ranked, err = rankBySupply(demand, append(far, produce), AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].Kind != CandidateProduce {
		t.Fatalf("far sliver %+v %v", ranked, err)
	}
}

// The colony-facts census maps to chop, harvest and hunt; a hunt's revenge
// chance prices it behind an equal safe source.
func TestCatalogSourceKindsAndHuntRisk(t *testing.T) {
	sources := []AcquisitionSource{
		{ID: "tree", Resource: "WoodLog", Tree: true, Yield: 25, Cell: domain.Cell{X: 5, Z: 0}},
		{ID: "other", Resource: "Steel", Yield: 10},
	}
	c := AcquisitionSourceCandidates("WoodLog", sources, domain.Cell{}, domain.Known(int64(100)))
	if len(c) != 1 || c[0].Kind != CandidateChop {
		t.Fatalf("%+v", c)
	}
	meat := []AcquisitionSource{
		{ID: "boar", Resource: "Meat_Boar", Hunt: true, Food: true, Yield: 1, NutritionYield: 1, RevengeChance: 0.5, Cell: domain.Cell{X: 10}},
		{ID: "hare", Resource: "Meat_Boar", Hunt: true, Food: true, Yield: 1, NutritionYield: 1, Cell: domain.Cell{X: 10}},
	}
	c = AcquisitionSourceCandidates("Meat_Boar", meat, domain.Cell{}, domain.Known(int64(100)))
	ranked, err := rankBySupply(ResourceDeficitDemand("Meat_Boar", 1), c, AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].ID != "hare" || ranked[0].Kind != CandidateHunt {
		t.Fatalf("%+v %v", ranked, err)
	}
}

// A deep drill's labor prices it behind a near surface deposit; a cheap
// caravan beats both, a dear one loses to the deposit.
func TestCatalogDeepDrillAndTrade(t *testing.T) {
	demand := ResourceDeficitDemand("Steel", 100)
	mine := MineCandidates("Steel", []ResourceSource{{ThingID: "ore", Yield: 100, Distance: 20, Method: ResourceSourceMine, Safety: "open_surface"}}, domain.Known(int64(500)))
	drill, ok := DeepDrillCandidate("Steel", "lump", 100, 20, domain.Known(int64(500)))
	if !ok {
		t.Fatal("no drill candidate")
	}
	cheap, _ := TradeCandidate("Steel", "caravan", 100, 1.9)
	ranked, err := rankBySupply(demand, append(mine, drill, cheap), AcquisitionCompetition{})
	if err != nil || len(ranked) != 3 || ranked[0].Kind != CandidateTrade || ranked[1].Kind != CandidateMining || ranked[2].Kind != CandidateDeepDrill {
		t.Fatalf("%+v %v", ranked, err)
	}
	dear, _ := TradeCandidate("Steel", "caravan", 100, 50)
	ranked, err = rankBySupply(demand, append(mine, dear), AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].Kind != CandidateMining {
		t.Fatalf("%+v %v", ranked, err)
	}
	if _, ok := DeepDrillCandidate("Steel", "lump", 0, 0, domain.Known(int64(1))); ok {
		t.Fatal("empty lump")
	}
}
