package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// perimeterClaims builds the 32 Wall/Door claims RoutineBuildingPlanner's
// starter shell always produces: a 9x9 room's outer ring, anchored at
// (originX, originZ).
func perimeterClaims(t *testing.T, plan domain.PlanID, originX, originZ int32) []policy.ConstructionClaim {
	t.Helper()
	var cells []domain.Cell
	for x := int32(0); x < 9; x++ {
		for z := int32(0); z < 9; z++ {
			if x == 0 || x == 8 || z == 0 || z == 8 {
				cells = append(cells, domain.Cell{X: originX + x, Z: originZ + z})
			}
		}
	}
	if len(cells) != 32 {
		t.Fatalf("perimeter helper produced %d cells, want 32", len(cells))
	}
	var claims []policy.ConstructionClaim
	for i, cell := range cells {
		def := "Wall"
		if i == 4 {
			def = "Door"
		}
		b, err := domain.NewBuilding(def, cell, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		claims = append(claims, policy.ConstructionClaim{Plan: plan, Building: b})
	}
	return claims
}

func TestStarterRoomRecoversNineByNineFromThirtyTwoCellPerimeter(t *testing.T) {
	claims := perimeterClaims(t, "starter-shell", 10, 20)
	room, known := starterRoom(domain.Known(claims))
	if !known || room != (policy.Rectangle{X: 10, Z: 20, Width: 9, Height: 9}) {
		t.Fatal(room, known)
	}
}

func TestStarterRoomRejectsWrongCellCount(t *testing.T) {
	claims := perimeterClaims(t, "starter-shell", 0, 0)
	claims = claims[:31]
	if _, known := starterRoom(domain.Known(claims)); known {
		t.Fatal("31-cell shell recognized as a complete room")
	}
}

func TestStarterRoomRejectsNonSquareBoundingBox(t *testing.T) {
	claims := perimeterClaims(t, "starter-shell", 0, 0)
	b, err := domain.NewBuilding("Wall", domain.Cell{X: 20, Z: 20}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	claims[0] = policy.ConstructionClaim{Plan: "starter-shell", Building: b}
	if _, known := starterRoom(domain.Known(claims)); known {
		t.Fatal("scattered cells recognized as a room")
	}
}

func TestStarterRoomGroupsClaimsByPlanAndPicksMatchingOne(t *testing.T) {
	var claims []policy.ConstructionClaim
	claims = append(claims, perimeterClaims(t, "other-plan", 0, 0)[:20]...)
	claims = append(claims, perimeterClaims(t, "starter-shell", 30, 40)...)
	room, known := starterRoom(domain.Known(claims))
	if !known || room != (policy.Rectangle{X: 30, Z: 40, Width: 9, Height: 9}) {
		t.Fatal(room, known)
	}
}

func TestStarterRoomIgnoresNonWallDoorClaims(t *testing.T) {
	claims := perimeterClaims(t, "starter-shell", 0, 0)
	b, err := domain.NewBuilding("SleepingSpot", domain.Cell{X: 50, Z: 50}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	claims = append(claims, policy.ConstructionClaim{Plan: "starter-shell", Building: b})
	room, known := starterRoom(domain.Known(claims))
	if !known || room != (policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}) {
		t.Fatal(room, known)
	}
}

func TestStarterRoomReturnsFalseOnUnknownClaims(t *testing.T) {
	if _, known := starterRoom(domain.Unknown[[]policy.ConstructionClaim]()); known {
		t.Fatal("unknown claims reported a room")
	}
}

// freeInteriorCells fills every interior cell of a 9x9 room (anchored at
// origin) as legal storage ground, matching an indoor, roofed, walkable,
// unoccupied, unzoned starter room.
func freeInteriorCells(room policy.Rectangle) map[domain.Cell]policy.SiteCell {
	cells := map[domain.Cell]policy.SiteCell{}
	for x := room.X + 1; x < room.X+room.Width-1; x++ {
		for z := room.Z + 1; z < room.Z+room.Height-1; z++ {
			cell := domain.Cell{X: x, Z: z}
			cells[cell] = policy.SiteCell{
				Cell: cell, Indoors: domain.Known(true), Roofed: domain.Known(true),
				Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false),
			}
		}
	}
	return cells
}

func TestFoodStorageCellsPrefersCanonicalSpotWhenFree(t *testing.T) {
	room := policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}
	cells, known := foodStorageCells(room, freeInteriorCells(room), map[domain.Cell]bool{})
	if !known {
		t.Fatal("expected a block")
	}
	want := map[domain.Cell]bool{}
	for x := int32(3); x <= 5; x++ {
		for z := int32(5); z <= 7; z++ {
			want[domain.Cell{X: x, Z: z}] = true
		}
	}
	if len(cells) != 9 {
		t.Fatal(cells)
	}
	for _, cell := range cells {
		if !want[cell] {
			t.Fatal("canonical spot not chosen", cells)
		}
	}
}

func TestFoodStorageCellsFallsBackWhenCanonicalBlockedByReservation(t *testing.T) {
	room := policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}
	occupied := map[domain.Cell]bool{{X: 4, Z: 6}: true}
	cells, known := foodStorageCells(room, freeInteriorCells(room), occupied)
	if !known || len(cells) != 9 {
		t.Fatal(cells, known)
	}
	for _, cell := range cells {
		if cell == (domain.Cell{X: 4, Z: 6}) {
			t.Fatal("reserved cell included in chosen block", cells)
		}
	}
}

func TestFoodStorageCellsShrinksToSingleFreeCell(t *testing.T) {
	room := policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}
	only := domain.Cell{X: 7, Z: 1}
	cells := map[domain.Cell]policy.SiteCell{
		only: {Cell: only, Indoors: domain.Known(true), Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)},
	}
	block, known := foodStorageCells(room, cells, map[domain.Cell]bool{})
	if !known || len(block) != 1 || block[0] != only {
		t.Fatal(block, known)
	}
}

func TestFoodStorageCellsReturnsFalseWhenNothingFits(t *testing.T) {
	room := policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}
	if _, known := foodStorageCells(room, map[domain.Cell]policy.SiteCell{}, map[domain.Cell]bool{}); known {
		t.Fatal("empty site produced a block")
	}
}

func TestFoodStorageCellsRequiresEachSitePredicate(t *testing.T) {
	room := policy.Rectangle{X: 0, Z: 0, Width: 9, Height: 9}
	only := domain.Cell{X: 7, Z: 1}
	base := policy.SiteCell{Cell: only, Indoors: domain.Known(true), Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)}
	for _, change := range []struct {
		name string
		make func(policy.SiteCell) policy.SiteCell
	}{
		{"not-indoors", func(c policy.SiteCell) policy.SiteCell { c.Indoors = domain.Known(false); return c }},
		{"unknown-indoors", func(c policy.SiteCell) policy.SiteCell { c.Indoors = domain.Unknown[bool](); return c }},
		{"not-roofed", func(c policy.SiteCell) policy.SiteCell { c.Roofed = domain.Known(false); return c }},
		{"not-walkable", func(c policy.SiteCell) policy.SiteCell { c.Walkable = domain.Known(false); return c }},
		{"occupied", func(c policy.SiteCell) policy.SiteCell { c.Occupied = domain.Known(true); return c }},
		{"unknown-occupied", func(c policy.SiteCell) policy.SiteCell { c.Occupied = domain.Unknown[bool](); return c }},
		{"zoned", func(c policy.SiteCell) policy.SiteCell { c.Zone = domain.Known(true); return c }},
	} {
		t.Run(change.name, func(t *testing.T) {
			cells := map[domain.Cell]policy.SiteCell{only: change.make(base)}
			if _, known := foodStorageCells(room, cells, map[domain.Cell]bool{}); known {
				t.Fatal("blocked cell accepted as storage ground")
			}
		})
	}
}
