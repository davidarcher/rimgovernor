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
	if err != nil || len(selected) != 2 || selected[0].ID != "berry" || selected[1].ID != "deer" || rows[0].ID != "deer" {
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

// Converted from the native case food/hunt-selection (removed for #749 at
// 04b0a98c): HuntSelectionFixture staged a wild deer and a three-muffalo
// herd with a positive revenge chance. With one hunting slot and a small
// food deficit the planner picks the deer first, whatever the native row
// order, and every hunt channel explains its revenge risk and work.
func TestHuntSelectionPrefersTheSafeDeerOverTheHerd(t *testing.T) {
	var rows []AcquisitionSource
	for i := 0; i < 3; i++ {
		rows = append(rows, AcquisitionSource{ID: fmt.Sprint("muffalo", i), Definition: "Muffalo", Resource: "Corpse_Muffalo", Token: "m", Food: true, Hunt: true, Yield: 1, NutritionYield: 9, RevengeChance: 0.1, HerdSize: 3, Cell: domain.Cell{X: 10 + int32(i), Z: 10}})
	}
	rows = append(rows, AcquisitionSource{ID: "deer", Definition: "Deer", Resource: "Corpse_Deer", Token: "d", Food: true, Hunt: true, Yield: 1, NutritionYield: 4, RevengeChance: 0.05, HerdSize: 1, Cell: domain.Cell{X: 9, Z: 10}})
	selected, err := SelectAcquisition(domain.Known(rows), domain.Known(0.1), domain.Known(0.0), true, nil, domain.Known(1))
	if err != nil || len(selected) != 1 || selected[0].ID != "deer" {
		t.Fatal("safe deer not selected first", selected, err)
	}
	channels := HuntChannels(rows)
	if len(channels) != 4 {
		t.Fatal(channels)
	}
	for _, c := range channels {
		if len(c.Risk) != 1 || c.Risk[0].Kind != FoodRevenge {
			t.Fatal("hunt channel lacks revenge risk", c)
		}
		if w, known := c.WorkPerDay.Value(); !known || w <= 0 {
			t.Fatal("hunt channel lacks work", c)
		}
	}
	if muffalo, deer := channels[0].Risk[0], channels[3].Risk[0]; muffalo.Weight <= deer.Weight {
		t.Fatal("herd revenge cost not above the deer's", muffalo, deer)
	}
}
