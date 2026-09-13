package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func penSiteFixture() PenEnclosureRequest {
	r := PenEnclosureRequest{Bounds: Bounds{20, 20}, Anchor: domain.Cell{X: 10, Z: 10}}
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true)})
		}
	}
	return r
}

func TestPenEnclosureSitesDeterministicNearestFirst(t *testing.T) {
	sites, err := PenEnclosureSites(penSiteFixture())
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) == 0 || len(sites) > 24 {
		t.Fatalf("unexpected candidate count: %d", len(sites))
	}
	// The anchor sits at the exact center; the nearest legal 6x6 site keeps the
	// anchor within its footprint.
	first := sites[0]
	if first.Width != 6 || first.Height != 6 {
		t.Fatalf("unexpected size: %+v", first)
	}
	if first.X > 10 || first.X+6 <= 10 || first.Z > 10 || first.Z+6 <= 10 {
		t.Fatalf("nearest site does not straddle anchor: %+v", first)
	}
	for i := 1; i < len(sites); i++ {
		a, b := sites[i-1], sites[i]
		da := (a.X+3-10)*(a.X+3-10) + (a.Z+3-10)*(a.Z+3-10)
		db := (b.X+3-10)*(b.X+3-10) + (b.Z+3-10)*(b.Z+3-10)
		if da > db {
			t.Fatalf("sites not ordered by distance: %+v then %+v", a, b)
		}
	}
}

func TestPenEnclosureSitesExcludesProtectedAndOutOfBoundsCandidates(t *testing.T) {
	r := penSiteFixture()
	// Protect a strip through the center so the nearest unobstructed anchor
	// straddling site must shift away from the exact center.
	for z := int32(0); z < 20; z++ {
		r.Protected = append(r.Protected, domain.Cell{X: 10, Z: z})
	}
	sites, err := PenEnclosureSites(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, s := range sites {
		if s.X <= 10 && 10 < s.X+6 {
			t.Fatalf("site overlaps protected column: %+v", s)
		}
	}
}

func TestPenEnclosureSitesUnknownAndOccupiedCellsAreNotFree(t *testing.T) {
	r := PenEnclosureRequest{Bounds: Bounds{8, 8}, Anchor: domain.Cell{X: 4, Z: 4}}
	for x := int32(0); x < 8; x++ {
		for z := int32(0); z < 8; z++ {
			cell := SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true)}
			if x == 2 && z == 2 {
				cell.Occupied = domain.Known(true)
			}
			if x == 5 && z == 5 {
				cell.Walkable = domain.Unknown[bool]()
			}
			r.Cells = append(r.Cells, cell)
		}
	}
	sites, err := PenEnclosureSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) != 0 {
		t.Fatalf("expected no legal 6x6 site in an 8x8 bound with blocked cells, got %+v", sites)
	}
}

func TestPenEnclosureSitesInvalidInput(t *testing.T) {
	if _, err := PenEnclosureSites(PenEnclosureRequest{Bounds: Bounds{0, 10}}); err == nil {
		t.Fatal("expected invalid bounds error")
	}
	if _, err := PenEnclosureSites(PenEnclosureRequest{Bounds: Bounds{10, 10}, Anchor: domain.Cell{X: 20, Z: 20}}); err == nil {
		t.Fatal("expected out-of-bounds anchor error")
	}
	dup := PenEnclosureRequest{Bounds: Bounds{10, 10}, Anchor: domain.Cell{X: 1, Z: 1}}
	dup.Cells = []SiteCell{{Cell: domain.Cell{X: 1, Z: 1}}, {Cell: domain.Cell{X: 1, Z: 1}}}
	if _, err := PenEnclosureSites(dup); err == nil {
		t.Fatal("expected duplicate cell error")
	}
}
