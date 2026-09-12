package policy

import (
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
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
