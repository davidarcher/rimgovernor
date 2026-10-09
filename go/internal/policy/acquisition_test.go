package policy

import (
	"fmt"
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestAcquisitionSubtractsPendingWithoutClaimingStock(t *testing.T) {
	rows := []AcquisitionSource{}
	for i := 0; i < 12; i++ {
		rows = append(rows, AcquisitionSource{ID: fmt.Sprint("plant", i), Resource: "WoodLog", Token: "cas", Tree: true, Yield: 10})
	}
	selected, err := SelectAcquisition(domain.Known(rows), domain.Known(50.0), domain.Known(30.0), false, map[string]bool{"plant0": true})
	if err != nil || len(selected) != 2 || selected[0].ID != "plant1" {
		t.Fatal(selected, err)
	}
	rows[1].Designated = true
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(500.0), domain.Known(0.0), false, nil)
	if err != nil || len(selected) != 8 {
		t.Fatal(selected, err)
	}
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(20.0), domain.Known(30.0), false, nil)
	if err != nil || len(selected) != 0 {
		t.Fatal(selected, err)
	}
	if _, err = SelectAcquisition(domain.Known(rows), domain.Known(20.0), domain.Unknown[float64](), false, nil); err == nil {
		t.Fatal("unknown pending treated as zero")
	}
	rows[0].Yield = math.NaN()
	if _, err = SelectAcquisition(domain.Known(rows), domain.Known(20.0), domain.Known(30.0), false, nil); err == nil {
		t.Fatal("invalid row hidden by satisfied deficit")
	}
}

func TestAcquisitionFoodUsesNutritionAndPreservesOrdering(t *testing.T) {
	rows := []AcquisitionSource{{ID: "tree", Resource: "WoodLog", Token: "t", Tree: true, Yield: 100}, {ID: "berry", Resource: "RawBerries", Token: "b", Food: true, Yield: 10, NutritionYield: 0.5}, {ID: "later", Resource: "RawBerries", Token: "c", Food: true, Yield: 20, NutritionYield: 1}}
	selected, err := SelectAcquisition(domain.Known(rows), domain.Known(0.6), domain.Known(0.0), true, nil)
	if err != nil || len(selected) != 2 || selected[0].ID != "berry" {
		t.Fatal(selected, err)
	}
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(0.5), domain.Known(0.0), true, nil)
	if err != nil || len(selected) != 1 {
		t.Fatal(selected, err)
	}
}

func TestAcquisitionHarvestPrecedesBoundedHunting(t *testing.T) {
	rows := []AcquisitionSource{{ID: "deer", Resource: "Corpse_Deer", Token: "d", Food: true, Hunt: true, Yield: 1, NutritionYield: 10}, {ID: "berry", Resource: "RawBerries", Token: "b", Food: true, Yield: 10, NutritionYield: 0.5}, {ID: "elk", Resource: "Corpse_Elk", Token: "e", Food: true, Hunt: true, Yield: 1, NutritionYield: 20}}
	selected, err := SelectAcquisition(domain.Known(rows), domain.Known(100.0), domain.Known(0.0), true, nil, domain.Known(1))
	if err != nil || len(selected) != 2 || selected[0].ID != "berry" || selected[1].ID != "elk" || rows[0].ID != "deer" {
		t.Fatal(selected, rows, err)
	}
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(0.5), domain.Known(0.0), true, nil, domain.Known(2))
	if err != nil || len(selected) != 1 || selected[0].Hunt {
		t.Fatal(selected, err)
	}
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(100.0), domain.Known(0.0), true, nil, domain.Unknown[int]())
	if err != nil || len(selected) != 1 || selected[0].Hunt {
		t.Fatal("unknown pending hunt count widened hunting", selected, err)
	}
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(100.0), domain.Known(100.0), true, nil, domain.Known(2))
	if err != nil || len(selected) != 0 {
		t.Fatal("pending material caused another hunt", selected, err)
	}
}

func TestHuntCountFollowsGapAndHunterBudget(t *testing.T) {
	var rows []AcquisitionSource
	for i := 0; i < 20; i++ {
		rows = append(rows, AcquisitionSource{ID: fmt.Sprint("deer", i), Resource: "Corpse_Deer", Token: "d", Food: true, Hunt: true, Yield: 1, NutritionYield: 10})
	}
	hunters := []PawnProfile{{ID: "a", Ranged: true}, {ID: "b", Ranged: true}, {ID: "melee"}}
	budget := HuntBudget(hunters, 0)
	if budget != 2*HuntsPerHunter || HuntBudget(hunters, 4) != 2 || HuntBudget(hunters, 9) != 0 || HuntBudget(nil, 0) != 0 {
		t.Fatalf("budget = %d", budget)
	}
	// ceil((deficit - pending) / yield): (45 - 5) / 10 = 4 prey.
	selected, err := SelectAcquisition(domain.Known(rows), domain.Known(45.0), domain.Known(5.0), true, nil, domain.Known(budget))
	if err != nil || len(selected) != 4 {
		t.Fatal(len(selected), err)
	}
	// Hunting capacity bounds a large food gap.
	four := append(hunters, PawnProfile{ID: "c", Ranged: true}, PawnProfile{ID: "d", Ranged: true})
	selected, err = SelectAcquisition(domain.Known(rows), domain.Known(1000.0), domain.Known(0.0), true, nil, domain.Known(HuntBudget(four, 0)))
	if err != nil || len(selected) != 12 {
		t.Fatal(len(selected), err)
	}
}

func TestResourceAcquisitionSelectsOnlyTheNamedHarvest(t *testing.T) {
	rows := []AcquisitionSource{
		{ID: "berry", Resource: "RawBerries", Token: "cas", Food: true, Yield: 8, NutritionYield: 0.4},
		{ID: "root1", Resource: "MedicineHerbal", Token: "cas", Yield: 2},
		{ID: "root2", Resource: "MedicineHerbal", Token: "cas", Yield: 2, Designated: true},
		{ID: "root3", Resource: "MedicineHerbal", Token: "cas", Yield: 2},
		{ID: "tree", Resource: "WoodLog", Token: "cas", Tree: true, Yield: 20},
	}
	selected, err := SelectResourceAcquisition(domain.Known(rows), domain.Known(5.0), domain.Known(2.0), "MedicineHerbal", nil)
	if err != nil || len(selected) != 2 || selected[0].ID != "root1" || selected[1].ID != "root3" {
		t.Fatal(selected, err)
	}
	if _, err = SelectResourceAcquisition(domain.Known(rows), domain.Known(5.0), domain.Known(0.0), "", nil); err == nil {
		t.Fatal("empty resource accepted")
	}
}

// A small food deficit and one hunting slot prefer the safer deer over a
// retaliating herd, independent of census order. Channels retain risk and work.
func TestHuntSelectionPrefersTheSafeDeerOverTheHerd(t *testing.T) {
	var rows []AcquisitionSource
	for i := 0; i < 3; i++ {
		rows = append(rows, AcquisitionSource{ID: fmt.Sprint("muffalo", i), Definition: "Muffalo", Resource: "Corpse_Muffalo", Token: "m", Food: true, Hunt: true, Yield: 1, NutritionYield: 9, RevengeChance: 0.1, HerdSize: 3, Cell: domain.Cell{X: 10 + 20*int32(i), Z: 10}})
	}
	rows = append(rows, AcquisitionSource{ID: "deer", Definition: "Deer", Resource: "Corpse_Deer", Token: "d", Food: true, Hunt: true, Yield: 1, NutritionYield: 4, RevengeChance: 0.05, HerdSize: 1, Cell: domain.Cell{X: 100, Z: 10}})
	selected, err := SelectAcquisition(domain.Known(rows), domain.Known(0.1), domain.Known(0.0), true, nil, domain.Known(1))
	if err != nil || len(selected) != 1 || selected[0].ID != "deer" {
		t.Fatal("safe deer not selected first", selected, err)
	}
	channels := HuntCandidates(rows, SquadHuntMinGunners, domain.Fact[float64]{})
	if len(channels) != 4 {
		t.Fatal(channels)
	}
	for _, c := range channels {
		if len(c.Risk) != 1 || c.Risk[0].Kind != CandidateRevenge {
			t.Fatal("hunt channel lacks revenge risk", c)
		}
		if w, known := c.LaborPerDay.Value(); !known || w <= 0 {
			t.Fatal("hunt channel lacks work", c)
		}
	}
	if muffalo, deer := channels[0].Risk[0], channels[3].Risk[0]; muffalo.Weight <= deer.Weight {
		t.Fatal("herd revenge cost not above the deer's", muffalo, deer)
	}
}

func TestChopGateOffersPlantationTreesOnlyAtMinimumGrowth(t *testing.T) {
	plantation := func(id string, growth float64) AcquisitionSource {
		return AcquisitionSource{ID: id, Resource: "WoodLog", Tree: true, Plantation: true, Growth: growth, Yield: 20}
	}
	wild := AcquisitionSource{ID: "wild", Resource: "WoodLog", Tree: true, Growth: 0.1, Yield: 20}
	crop := AcquisitionSource{ID: "rice", Resource: "RawRice", Growth: 0.2, Yield: 5, Food: true}
	const gate = 0.7
	for _, c := range []struct {
		row   AcquisitionSource
		below bool
	}{{plantation("young", 0.69), true}, {plantation("at", gate), false}, {plantation("old", 1), false}, {wild, false}, {crop, false}} {
		if got := c.row.BelowChopGate(gate); got != c.below {
			t.Fatalf("%s: BelowChopGate(%v) = %v, want %v", c.row.ID, gate, got, c.below)
		}
		if c.row.BelowChopGate(0) {
			t.Fatalf("%s: a zero gate held a row", c.row.ID)
		}
	}
}
