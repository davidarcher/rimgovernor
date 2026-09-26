package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A near deposit covering the deficit outranks smelting it; a distant
// sliver of one does not (#728).
func TestCatalogRanksMineAgainstBill(t *testing.T) {
	bill := ResourceMethod{Kind: ResourceMethodProduce, Bench: "smelter", Recipe: "SmeltSlag", Resource: "Steel", Target: 100}
	produce, ok := ProduceCandidate(bill, 60)
	if !ok {
		t.Fatal("no produce candidate")
	}
	headroom := domain.Known(int64(500))
	demand := ResourceDeficitDemand("Steel", 60)
	near := MineCandidates("Steel", []ResourceSource{{ThingID: "ore-near", Yield: 60, Distance: 10, Method: ResourceSourceMine, Safety: "open_surface"}}, headroom)
	ranked, err := RankResourceCandidates(demand, append(near, produce), AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].Kind != AcquisitionMining {
		t.Fatalf("near deposit %+v %v", ranked, err)
	}
	far := MineCandidates("Steel", []ResourceSource{{ThingID: "ore-far", Yield: 5, Distance: 120, Method: ResourceSourceMine, Safety: "open_surface"}}, headroom)
	ranked, err = RankResourceCandidates(demand, append(far, produce), AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].Kind != AcquisitionProduce {
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
	if len(c) != 1 || c[0].Kind != AcquisitionChop {
		t.Fatalf("%+v", c)
	}
	meat := []AcquisitionSource{
		{ID: "boar", Resource: "Meat_Boar", Hunt: true, Food: true, Yield: 1, NutritionYield: 1, RevengeChance: 0.5, Cell: domain.Cell{X: 10}},
		{ID: "hare", Resource: "Meat_Boar", Hunt: true, Food: true, Yield: 1, NutritionYield: 1, Cell: domain.Cell{X: 10}},
	}
	c = AcquisitionSourceCandidates("Meat_Boar", meat, domain.Cell{}, domain.Known(int64(100)))
	ranked, err := RankResourceCandidates(ResourceDeficitDemand("Meat_Boar", 1), c, AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].ID != "hare" || ranked[0].Kind != AcquisitionHunt {
		t.Fatalf("%+v %v", ranked, err)
	}
}
