package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestCropSeasonRunwaySoilAndProtectedPatches(t *testing.T) {
	crop := func(name string, days, yield float64) CropChoice {
		return CropChoice{Name: name, Available: domain.Known(true), Edible: domain.Known(true), GrowDays: domain.Known(days), HarvestNutrition: domain.Known(yield), FertilityMin: domain.Known(0.7), FertilitySensitivity: domain.Known(1.0), Demand: domain.Known(2.0)}
	}
	rice, corn := crop("Plant_Rice", 3, 1), crop("Plant_Corn", 10, 5)
	var cells []SiteCell
	for x := int32(0); x < 8; x++ {
		for z := int32(0); z < 8; z++ {
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), Roofed: domain.Known(false), Fertility: domain.Known(1.0)})
		}
	}
	climate := CropClimate{domain.Known(true), domain.Known(60.0)}
	for _, tc := range []struct {
		runway float64
		name   string
	}{{2, "Plant_Rice"}, {30, "Plant_Corn"}} {
		selected, ok := ChooseCrop([]CropChoice{corn, rice}, climate, domain.Known(tc.runway), cells, nil)
		if !ok || selected.Name != tc.name {
			t.Fatal(selected, ok)
		}
	}
	climate.DaysRemaining = domain.Known(6.0)
	if _, ok := ChooseCrop([]CropChoice{rice, corn}, climate, domain.Known(1.0), cells, nil); ok {
		t.Fatal("winter crop")
	}
	patches := GrowthFields(Bounds{8, 8}, domain.Cell{X: 1, Z: 1}, domain.Unknown[domain.Cell](), cells, []domain.Cell{{X: 0, Z: 0}}, nil, rice, domain.Known(30), domain.Known(0.0)).Patches
	seen := map[domain.Cell]bool{}
	for _, p := range patches {
		for _, c := range rectCells(p) {
			if seen[c] || c == (domain.Cell{}) {
				t.Fatal("overlap", c)
			}
			seen[c] = true
		}
	}
	if len(seen) < 30 || len(patches) > 32 {
		t.Fatal(patches)
	}
	if p := GrowthFields(Bounds{8, 8}, domain.Cell{}, domain.Unknown[domain.Cell](), append(cells, cells[0]), nil, nil, rice, domain.Known(30), domain.Known(0.0)); len(p.Patches) != 0 {
		t.Fatal("duplicate census")
	}
	if p := GrowthFields(Bounds{8, 8}, domain.Cell{}, domain.Unknown[domain.Cell](), cells, nil, nil, rice, domain.Known(30), domain.Unknown[float64]()); len(p.Patches) != 0 {
		t.Fatal("unknown coverage")
	}
}
