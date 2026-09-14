package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func supplyRoomSiteFixture() SupplyRoomEnclosureRequest {
	r := SupplyRoomEnclosureRequest{Bounds: Bounds{20, 20}, Anchor: domain.Cell{X: 10, Z: 10}}
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			r.Cells = append(r.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true), StorageEmpty: domain.Known(true)})
		}
	}
	return r
}

func TestSupplyRoomEnclosureSitesDeterministicNearestFirst(t *testing.T) {
	sites, err := SupplyRoomEnclosureSites(supplyRoomSiteFixture())
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) == 0 || len(sites) > 24 {
		t.Fatalf("unexpected candidate count: %d", len(sites))
	}
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

func TestSupplyRoomEnclosureSitesExcludesProtectedAndOutOfBoundsCandidates(t *testing.T) {
	r := supplyRoomSiteFixture()
	for z := int32(0); z < 20; z++ {
		r.Protected = append(r.Protected, domain.Cell{X: 10, Z: z})
	}
	sites, err := SupplyRoomEnclosureSites(r)
	if err != nil {
		t.Fatal(err)
	}
	for _, s := range sites {
		if s.X <= 10 && 10 < s.X+6 {
			t.Fatalf("site overlaps protected column: %+v", s)
		}
	}
}

func TestSupplyRoomEnclosureSitesRequiresEmptyInterior(t *testing.T) {
	r := SupplyRoomEnclosureRequest{Bounds: Bounds{8, 8}, Anchor: domain.Cell{X: 4, Z: 4}}
	for x := int32(0); x < 8; x++ {
		for z := int32(0); z < 8; z++ {
			cell := SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true), StorageEmpty: domain.Known(true)}
			if x == 3 && z == 3 {
				// A non-empty interior cell rules out the only 6x6 site this
				// bound can fit, matching empty_interior=True.
				cell.StorageEmpty = domain.Known(false)
			}
			r.Cells = append(r.Cells, cell)
		}
	}
	sites, err := SupplyRoomEnclosureSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) != 0 {
		t.Fatalf("expected no legal site with an occupied interior cell, got %+v", sites)
	}
}

func TestSupplyRoomEnclosureSitesUnknownInteriorStorageIsNotEmpty(t *testing.T) {
	r := SupplyRoomEnclosureRequest{Bounds: Bounds{8, 8}, Anchor: domain.Cell{X: 4, Z: 4}}
	for x := int32(0); x < 8; x++ {
		for z := int32(0); z < 8; z++ {
			cell := SiteCell{Cell: domain.Cell{X: x, Z: z}, Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), SupportsLight: domain.Known(true), StorageEmpty: domain.Known(true)}
			if x == 3 && z == 3 {
				cell.StorageEmpty = domain.Unknown[bool]()
			}
			r.Cells = append(r.Cells, cell)
		}
	}
	sites, err := SupplyRoomEnclosureSites(r)
	if err != nil {
		t.Fatal(err)
	}
	if len(sites) != 0 {
		t.Fatalf("expected unknown interior storage to be treated as not free, got %+v", sites)
	}
}

func TestSupplyRoomEnclosureSitesInvalidInput(t *testing.T) {
	if _, err := SupplyRoomEnclosureSites(SupplyRoomEnclosureRequest{Bounds: Bounds{0, 10}}); err == nil {
		t.Fatal("expected invalid bounds error")
	}
	if _, err := SupplyRoomEnclosureSites(SupplyRoomEnclosureRequest{Bounds: Bounds{10, 10}, Anchor: domain.Cell{X: 20, Z: 20}}); err == nil {
		t.Fatal("expected out-of-bounds anchor error")
	}
	dup := SupplyRoomEnclosureRequest{Bounds: Bounds{10, 10}, Anchor: domain.Cell{X: 1, Z: 1}}
	dup.Cells = []SiteCell{{Cell: domain.Cell{X: 1, Z: 1}}, {Cell: domain.Cell{X: 1, Z: 1}}}
	if _, err := SupplyRoomEnclosureSites(dup); err == nil {
		t.Fatal("expected duplicate cell error")
	}
}
