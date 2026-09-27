package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStockOverlayTintsEveryStockpileByItsTarget(t *testing.T) {
	block := func(x, z int32) []domain.Cell {
		var out []domain.Cell
		for dz := int32(0); dz < 2; dz++ {
			for dx := int32(0); dx < 3; dx++ {
				out = append(out, domain.Cell{X: x + dx, Z: z + dz})
			}
		}
		return out
	}
	steel, _ := domain.AllowOnlyFilter([]string{"Steel"})
	zones := []StockZone{
		// The general store holds wood and steel: wood is the worse.
		{ID: "a", Role: domain.GeneralRole, Filter: domain.Known(domain.GeneralFilter()), Cells: block(0, 0)},
		// A covered steel zone: steel 90/100 is amber.
		{ID: "b", Role: "covered:Steel", Filter: domain.Known(steel), Cells: block(10, 0)},
		// A player's food stockpile (no claim) by the census food flag.
		{ID: "c", Label: "Stockpile zone 1", FoodStorage: true, Cells: block(20, 0)},
		// A hospital's medicine stockpile: medicine met.
		{ID: "d", Role: "medicine:room1", Cells: block(30, 0)},
		// Apparel has no target: a label, no tint.
		{ID: "e", Role: domain.ApparelRole, Cells: block(40, 0)},
		// A player's stockpile with an unread filter and a non-ASCII label.
		{ID: "f", Label: "Lager ä", Cells: block(50, 0)},
	}
	levels := StockLevels{
		Targets:        map[Resource]int64{"WoodLog": 400, "Steel": 100, "MedicineHerbal": 10},
		Stock:          map[Resource]int64{"WoodLog": 120, "Steel": 90, "MedicineHerbal": 12},
		FoodDays:       domain.Known(5.5),
		FoodTargetDays: 7,
	}
	o := StockOverlay(zones, levels, Bounds{Width: 100, Height: 100})
	band := map[string]int{}
	for _, l := range o.Layers {
		if l.Style != OverlayFill {
			t.Fatalf("layer %+v", l)
		}
		for _, r := range l.Runs {
			band[l.Label] += int(r.Length)
		}
	}
	// short: a; low: b and c; met: d. Six cells each.
	if band["short"] != 6 || band["low"] != 12 || band["met"] != 6 || len(o.Layers) != 3 {
		t.Fatalf("bands %v", band)
	}
	want := []string{"wood 120/400", "Steel 90/100", "food 5.5/7 days", "MedicineHerbal 12/10", "apparel", "Lager ?"}
	if len(o.Labels) != len(want) {
		t.Fatalf("labels %+v", o.Labels)
	}
	for i, l := range o.Labels {
		if l.Text != want[i] {
			t.Fatalf("label %d %q, want %q", i, l.Text, want[i])
		}
		if l.Cell != (domain.Cell{X: int32(i)*10 + 1, Z: 0}) {
			t.Fatalf("label %q at %+v", l.Text, l.Cell)
		}
	}
}
