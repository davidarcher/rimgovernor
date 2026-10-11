package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestPlacementSearchStockpileZonesAreSoft(t *testing.T) {
	r := placementSearchFixture()
	for i := range r.Cells {
		c := &r.Cells[i]
		if c.Cell.X == 10 && c.Cell.Z == 10 {
			c.Zone, c.ZoneID = domain.Known(true), domain.Known("stock")
		}
		if c.Cell.X == 10 && c.Cell.Z == 11 {
			c.Zone, c.ZoneID = domain.Known(true), domain.Known("grow")
		}
	}
	hard, err := NewPlacementSearch(r)
	if err != nil || hard.free[domain.Cell{X: 10, Z: 10}] {
		t.Fatal("a stockpile cell is free without the zone kind", err)
	}
	r.StockpileZones = map[string]bool{"stock": true}
	soft, err := NewPlacementSearch(r)
	if err != nil || !soft.free[domain.Cell{X: 10, Z: 10}] || soft.free[domain.Cell{X: 10, Z: 11}] {
		t.Fatal("stockpile must be free and a growing zone must block", err)
	}
}
