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

// MaintainResource's census selection takes the best-ranked rows until the
// need less designated yield is covered, and never more hunts than slots.
func TestSelectCatalogAcquisition(t *testing.T) {
	rows := []AcquisitionSource{
		{ID: "near", Resource: "WoodLog", Tree: true, Yield: 30, Cell: domain.Cell{X: 5}},
		{ID: "far", Resource: "WoodLog", Tree: true, Yield: 30, Cell: domain.Cell{X: 90}},
		{ID: "mid", Resource: "WoodLog", Tree: true, Yield: 30, Cell: domain.Cell{X: 20}},
		{ID: "done", Resource: "WoodLog", Tree: true, Designated: true, Yield: 20},
		{ID: "held", Resource: "WoodLog", Tree: true, Yield: 30, Cell: domain.Cell{X: 1}},
	}
	got, best, err := SelectCatalogAcquisition(rows, "WoodLog", 70, domain.Cell{}, map[string]bool{"held": true}, 0)
	if err != nil || len(got) != 2 || got[0].ID != "near" || got[1].ID != "mid" || best.ID != "near" || best.Score <= 0 {
		t.Fatalf("%+v %v", got, err)
	}
	if got, _, _ := SelectCatalogAcquisition(rows, "WoodLog", 20, domain.Cell{}, nil, 0); len(got) != 0 {
		t.Fatal("designated yield covers the need", got)
	}
	hunts := []AcquisitionSource{
		{ID: "a", Resource: "Meat_Hare", Hunt: true, Food: true, Yield: 1, NutritionYield: 1},
		{ID: "b", Resource: "Meat_Hare", Hunt: true, Food: true, Yield: 1, NutritionYield: 1},
	}
	if got, _, _ := SelectCatalogAcquisition(hunts, "Meat_Hare", 5, domain.Cell{}, nil, 1); len(got) != 1 {
		t.Fatal("hunt slots exceeded", got)
	}
	if got, _, _ := SelectCatalogAcquisition(hunts, "Meat_Hare", 5, domain.Cell{}, nil, 0); len(got) != 0 {
		t.Fatal("hunted without a slot", got)
	}
}

// A deep drill's labor prices it behind a near surface deposit; a cheap
// caravan beats both, a dear one loses to the deposit (#728).
func TestCatalogDeepDrillAndTrade(t *testing.T) {
	demand := ResourceDeficitDemand("Steel", 100)
	mine := MineCandidates("Steel", []ResourceSource{{ThingID: "ore", Yield: 100, Distance: 20, Method: ResourceSourceMine, Safety: "open_surface"}}, domain.Known(int64(500)))
	drill, ok := DeepDrillCandidate("Steel", "lump", 100, 20, domain.Known(int64(500)))
	if !ok {
		t.Fatal("no drill candidate")
	}
	cheap, _ := TradeCandidate("Steel", "caravan", 100, 1.9)
	ranked, err := RankResourceCandidates(demand, append(mine, drill, cheap), AcquisitionCompetition{})
	if err != nil || len(ranked) != 3 || ranked[0].Kind != AcquisitionTrade || ranked[1].Kind != AcquisitionMining || ranked[2].Kind != AcquisitionDeepDrill {
		t.Fatalf("%+v %v", ranked, err)
	}
	dear, _ := TradeCandidate("Steel", "caravan", 100, 50)
	ranked, err = RankResourceCandidates(demand, append(mine, dear), AcquisitionCompetition{})
	if err != nil || len(ranked) != 2 || ranked[0].Kind != AcquisitionMining {
		t.Fatalf("%+v %v", ranked, err)
	}
	if _, ok := DeepDrillCandidate("Steel", "lump", 0, 0, domain.Known(int64(1))); ok {
		t.Fatal("empty lump")
	}
}
