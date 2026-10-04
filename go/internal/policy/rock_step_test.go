package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rockSite(x, z int32) SiteCell {
	return SiteCell{Cell: domain.Cell{X: x, Z: z},
		Occupied: domain.Known(true), Walkable: domain.Known(false), Roof: domain.Known("RoofRockThick"), NaturalRock: domain.Known(true)}
}

func openSite(x, z int32) SiteCell {
	return SiteCell{Cell: domain.Cell{X: x, Z: z},
		Occupied: domain.Known(false), Walkable: domain.Known(true), Roofed: domain.Known(false)}
}

func TestRockStep(t *testing.T) {
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	sites := []SiteCell{openSite(0, 0), rockSite(1, 0), rockSite(2, 0)}
	t.Run("open ground yields no dig", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(0, 0), RockNeedsFloor}, {cell(0, 0), RockBlocks}}, sites)
		if len(got.Dig) != 0 || len(got.Left) != 0 {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("needs_floor on rock digs exactly that cell", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(0, 0), RockNeedsFloor}, {cell(1, 0), RockNeedsFloor}}, sites)
		if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("blocks on rock is left and built", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(2, 0), RockBlocks}}, sites)
		if len(got.Dig) != 0 || !reflect.DeepEqual(got.Left, []domain.Cell{cell(2, 0)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("fogged rock is dug", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(9, 9), RockNeedsFloor}}, sites)
		if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(9, 9)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("fogged wall-role cell is left as rock", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(9, 9), RockBlocks}}, sites)
		if len(got.Dig) != 0 || !reflect.DeepEqual(got.Left, []domain.Cell{cell(9, 9)}) {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("floor role wins over wall role for one cell", func(t *testing.T) {
		got := RockStep([]RoleCell{{cell(1, 0), RockBlocks}, {cell(1, 0), RockNeedsFloor}}, sites)
		if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) || len(got.Left) != 0 {
			t.Fatalf("got %+v", got)
		}
	})
	t.Run("overlapping plans dedupe to one excavation", func(t *testing.T) {
		a := RockStep([]RoleCell{{cell(1, 0), RockNeedsFloor}, {cell(2, 0), RockNeedsFloor}}, sites).Dig
		b := RockStep([]RoleCell{{cell(2, 0), RockNeedsFloor}, {cell(1, 0), RockNeedsFloor}}, sites).Dig
		want := []domain.Cell{cell(1, 0), cell(2, 0)}
		if got := MergeRockDigs(a, b); !reflect.DeepEqual(got, want) {
			t.Fatalf("got %v", got)
		}
	})
}

// The rim of a mountain is natural rock with no roof over it; a dig that
// read it as open ground would leave the rock behind it unreachable.
func TestRockStepDigsUnroofedRimRock(t *testing.T) {
	rim := rockSite(1, 0)
	rim.Roof, rim.Roofed = domain.Known(""), domain.Known(false)
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	got := RockStep([]RoleCell{{cell(1, 0), RockNeedsFloor}, {cell(2, 0), RockBlocks}}, []SiteCell{rim, rockSite(2, 0)})
	if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) || !reflect.DeepEqual(got.Left, []domain.Cell{cell(2, 0)}) {
		t.Fatalf("got %+v", got)
	}
}

func TestRockSiteView(t *testing.T) {
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	wet := openSite(0, 0)
	wet.Walkable = domain.Known(false)
	sites := []SiteCell{openSite(1, 0), rockSite(2, 0), rockSite(7, 0), wet}
	view := RockSiteView(sites, Bounds{Width: 8, Height: 4}, Rectangle{X: 0, Z: 0, Width: 4, Height: 2})
	by := map[domain.Cell]SiteCell{}
	for _, c := range view {
		if _, dup := by[c.Cell]; dup {
			t.Fatalf("duplicate %v", c.Cell)
		}
		by[c.Cell] = c
	}
	isOpen := func(c domain.Cell) bool {
		s := by[c]
		return positive(s.Walkable) && positive(measured(s.Occupied, func(v bool) bool { return !v }))
	}
	if !isOpen(cell(2, 0)) || rockCell(by[cell(2, 0)]) {
		t.Fatal("listed rock in the area must read open")
	}
	if !isOpen(cell(3, 1)) {
		t.Fatal("fogged cell in the area must read open")
	}
	if isOpen(cell(0, 0)) {
		t.Fatal("a listed non-rock cell must pass through")
	}
	if !rockCell(by[cell(7, 0)]) {
		t.Fatal("rock outside the area must stay rock")
	}
	if _, ok := by[cell(5, 3)]; ok {
		t.Fatal("a fogged cell outside the area must not be invented")
	}
	if len(view) != 9 {
		t.Fatal(len(view))
	}
	// The step then digs what the picker chose over rock and fog.
	got := RockStep([]RoleCell{{cell(1, 0), RockNeedsFloor}, {cell(2, 0), RockNeedsFloor}, {cell(3, 1), RockNeedsFloor}}, sites)
	if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(2, 0), cell(3, 1)}) {
		t.Fatal(got)
	}
}

func TestPenEnclosureEntranceMustOpenOntoGround(t *testing.T) {
	var cells, ground []SiteCell
	for x := int32(0); x < 8; x++ {
		for z := int32(0); z < 8; z++ {
			open := openSite(x, z)
			open.Zone, open.SupportsLight = domain.Known(false), domain.Known(true)
			cells = append(cells, open)
			if z == 0 { // only the south row is real ground
				ground = append(ground, open)
			} else {
				ground = append(ground, rockSite(x, z))
			}
		}
	}
	request := PenEnclosureRequest{Bounds: Bounds{Width: 8, Height: 8}, Anchor: domain.Cell{X: 3, Z: 3}, Cells: cells}
	free, err := PenEnclosureSites(request)
	if err != nil || len(free) == 0 {
		t.Fatal(free, err)
	}
	request.Entrance = ground
	sites, err := PenEnclosureSites(request)
	if err != nil {
		t.Fatal(err)
	}
	// Only a pen whose first row sits on z=1 has the ground row below its gate.
	if len(sites) != 3 {
		t.Fatal(sites)
	}
	for _, s := range sites {
		if s.Z != 1 {
			t.Fatal("entrance not on ground", s)
		}
	}
}

func TestRockAccessPicksFirstOpenNeighbour(t *testing.T) {
	foot := []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}
	cells := []SiteCell{openSite(5, 5), openSite(2, 2), openSite(0, 1), rockSite(1, 0)}
	got, ok := RockAccess(foot, cells)
	if !ok || got != (domain.Cell{X: 0, Z: 1}) {
		t.Fatal(got, ok)
	}
	if _, ok := RockAccess(foot, cells[:1]); ok {
		t.Fatal("walled-in footprint reported access")
	}
}
