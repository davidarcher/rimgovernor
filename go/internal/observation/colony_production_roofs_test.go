package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func zoneCell(zone string, roofed domain.Fact[bool]) policy.SiteCell {
	return policy.SiteCell{Zone: domain.Known(true), ZoneID: domain.Known(zone), Roofed: roofed}
}

func TestZoneRoofsCountsOpenSkyPerZone(t *testing.T) {
	cells := []policy.SiteCell{
		zoneCell("a", domain.Known(false)), zoneCell("a", domain.Known(false)), zoneCell("a", domain.Known(true)),
		zoneCell("b", domain.Known(true)),
		zoneCell("c", domain.Known(false)), zoneCell("c", domain.Unknown[bool]()),
		{Zone: domain.Known(false), ZoneID: domain.Known("a"), Roofed: domain.Known(false)},
		{Roofed: domain.Known(false)},
	}
	zones := zoneRoofs(cells)
	if n, _ := zones["a"].cells.Value(); n != 3 {
		t.Fatalf("a cells %d", n)
	}
	if n, _ := zones["a"].unroofed.Value(); n != 2 {
		t.Fatalf("a unroofed %d", n)
	}
	if n, _ := zones["b"].unroofed.Value(); n != 0 {
		t.Fatalf("a fully roofed zone is known with no open cells: %d", n)
	}
	if _, known := zones["b"].cells.Value(); !known {
		t.Fatal("b unknown")
	}
	if _, known := zones["c"].cells.Value(); known {
		t.Fatal("a zone with an unread roof must be unknown")
	}
	if _, known := zones["missing"].cells.Value(); known {
		t.Fatal("a zone outside the window must be unknown")
	}
	if len(zoneRoofs(nil)) != 0 {
		t.Fatal("no window, no zones")
	}
}
