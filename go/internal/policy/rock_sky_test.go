package policy

import (
	"errors"
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func thinRock(x, z int32) SiteCell {
	c := rockSite(x, z)
	c.Roof = domain.Known("RoofRockThin")
	return c
}

func thinOpen(x, z int32) SiteCell {
	c := openSite(x, z)
	c.Roofed, c.Roof = domain.Known(true), domain.Known("RoofRockThin")
	return c
}

// A needs-sky cell is dug when rock, unroofed when its roof is removable,
// and unfit when unseen or under thick roof (#1758).
func TestRockStepNeedsSky(t *testing.T) {
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	sites := []SiteCell{openSite(0, 0), thinRock(1, 0), thinOpen(2, 0), rockSite(3, 0)}
	sky := func(cells ...domain.Cell) []RoleCell {
		var out []RoleCell
		for _, c := range cells {
			out = append(out, RoleCell{Cell: c, Role: RockNeedsSky})
		}
		return out
	}
	got, _ := RockStepRoofs(sky(cell(0, 0), cell(1, 0), cell(2, 0)), sites, testRoofs)
	if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) || !reflect.DeepEqual(got.Unroof, []domain.Cell{cell(1, 0), cell(2, 0)}) || len(got.Unfit) != 0 {
		t.Fatalf("got %+v", got)
	}
	got, _ = RockStepRoofs(sky(cell(3, 0), cell(9, 9)), sites, testRoofs)
	if len(got.Dig) != 0 || len(got.Unroof) != 0 || !reflect.DeepEqual(got.Unfit, []domain.Cell{cell(3, 0), cell(9, 9)}) {
		t.Fatalf("thick and unseen cells: %+v", got)
	}
	// Sky wins over floor and blocks for the same cell.
	got, _ = RockStepRoofs([]RoleCell{{cell(1, 0), RockBlocks}, {cell(1, 0), RockNeedsSky}, {cell(1, 0), RockNeedsFloor}}, sites, testRoofs)
	if !reflect.DeepEqual(got.Unroof, []domain.Cell{cell(1, 0)}) || len(got.Left) != 0 {
		t.Fatalf("got %+v", got)
	}
}

// A sky site crosses rock but never a thick-roof cell, and rock costs.
func TestUtilityGridSkyRock(t *testing.T) {
	u := &utilityGrid{w: 4, h: 4, rock: make([]bool, 16), thick: map[domain.Cell]bool{{X: 3, Z: 3}: true}}
	u.rock[5], u.rock[6] = true, true
	if n := u.skyRock(Rectangle{X: 0, Z: 0, Width: 3, Height: 3}); n != 2 {
		t.Fatal("rock cells", n)
	}
	if n := u.skyRock(Rectangle{X: 2, Z: 2, Width: 2, Height: 2}); n != -1 {
		t.Fatal("thick roof site accepted", n)
	}
}

// A turbine's catch zone is its own 7x16 wind path, inside the pair's lanes;
// the other turbine's back zone is not in it (#1871).
func TestTurbineCatchZoneIsTheTurbinesOwnWindPath(t *testing.T) {
	p := PlanUtilities(corePlan(utilityTestZones(), 3, BuildTierCamp), UtilityWants{TurbinePairs: 1})
	sites := PlannedPowerSites(p, WindTurbineDefinition)
	if len(sites) != 2 {
		t.Fatal(sites)
	}
	var lanes []domain.Cell
	for _, r := range p.Reservations {
		if r.Kind == ReserveTurbineLane {
			lanes = append(lanes, RectangleCells(r.Area)...)
		}
	}
	zones := make([][]domain.Cell, len(sites))
	for i, site := range sites {
		zones[i] = TurbineCatchZone(p, site.Area)
		if len(zones[i]) != 7*16 {
			t.Fatal(i, len(zones[i]))
		}
		for _, c := range zones[i] {
			if !slices.Contains(lanes, c) {
				t.Fatal("wind cell outside the lanes", i, c)
			}
		}
	}
	shared := 0
	for _, c := range zones[0] {
		if slices.Contains(zones[1], c) {
			shared++
		}
	}
	if shared != 7*10 || len(lanes) != 7*22 {
		t.Fatal("zones share", shared, "lanes", len(lanes))
	}
	if TurbineCatchZone(p, Rectangle{}) != nil {
		t.Fatal("lanes for a non-turbine area")
	}
}

// testRoofs are the rows of the game's roofs by their RoofDef flags: a thick
// natural roof, a thin natural one and a constructed one.
var testRoofs = RoofRules{"RoofRockThick": {Thick: true}, "RoofRockThin": {}, "RoofConstructed": {}}

// The removable-roof rule reads the roof rows (#1870): thick is unfit, thin
// natural and constructed roofs come off, no roof needs nothing, and a def
// the rules lack fails loudly while the other cells are still classified.
func TestRockStepRoofRules(t *testing.T) {
	cell := func(x, z int32) domain.Cell { return domain.Cell{X: x, Z: z} }
	roofed := func(x, z int32, roof string) SiteCell {
		c := openSite(x, z)
		c.Roofed, c.Roof = domain.Known(roof != ""), domain.Known(roof)
		return c
	}
	sites := []SiteCell{roofed(0, 0, "RoofRockThick"), roofed(1, 0, "RoofRockThin"), roofed(2, 0, "RoofConstructed"), roofed(3, 0, ""), roofed(4, 0, "RoofMystery")}
	sky := func(x int32) []RoleCell { return []RoleCell{{Cell: cell(x, 0), Role: RockNeedsSky}} }
	for _, tc := range []struct {
		name           string
		x              int32
		unroof, unfit  []domain.Cell
		unknownInError bool
	}{
		{"thick", 0, nil, []domain.Cell{cell(0, 0)}, false},
		{"thin natural", 1, []domain.Cell{cell(1, 0)}, nil, false},
		{"constructed", 2, []domain.Cell{cell(2, 0)}, nil, false},
		{"no roof", 3, nil, nil, false},
		{"unknown", 4, nil, []domain.Cell{cell(4, 0)}, true},
	} {
		got, err := RockStepRoofs(sky(tc.x), sites, testRoofs)
		if !reflect.DeepEqual(got.Unroof, tc.unroof) || !reflect.DeepEqual(got.Unfit, tc.unfit) {
			t.Errorf("%s: %+v", tc.name, got)
		}
		if tc.unknownInError != errors.Is(err, ErrUnknownRoof) || tc.unknownInError && !strings.Contains(err.Error(), "RoofMystery") {
			t.Errorf("%s: error %v", tc.name, err)
		}
	}
	// No rules at all: every def is unknown.
	if _, err := RockStepRoofs(sky(1), sites, nil); !errors.Is(err, ErrUnknownRoof) {
		t.Fatal(err)
	}
}
