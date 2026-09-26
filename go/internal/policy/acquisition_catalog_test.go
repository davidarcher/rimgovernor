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
	got, err := SelectCatalogAcquisition(rows, "WoodLog", 70, domain.Cell{}, map[string]bool{"held": true}, 0)
	if err != nil || len(got) != 2 || got[0].ID != "near" || got[1].ID != "mid" {
		t.Fatalf("%+v %v", got, err)
	}
	if got, _ := SelectCatalogAcquisition(rows, "WoodLog", 20, domain.Cell{}, nil, 0); len(got) != 0 {
		t.Fatal("designated yield covers the need", got)
	}
	hunts := []AcquisitionSource{
		{ID: "a", Resource: "Meat_Hare", Hunt: true, Food: true, Yield: 1, NutritionYield: 1},
		{ID: "b", Resource: "Meat_Hare", Hunt: true, Food: true, Yield: 1, NutritionYield: 1},
	}
	if got, _ := SelectCatalogAcquisition(hunts, "Meat_Hare", 5, domain.Cell{}, nil, 1); len(got) != 1 {
		t.Fatal("hunt slots exceeded", got)
	}
	if got, _ := SelectCatalogAcquisition(hunts, "Meat_Hare", 5, domain.Cell{}, nil, 0); len(got) != 0 {
		t.Fatal("hunted without a slot", got)
	}
}
