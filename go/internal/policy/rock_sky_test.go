package policy

import (
	"reflect"
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
	c.Roof = domain.Known("RoofRockThin")
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
	got := RockStep(sky(cell(0, 0), cell(1, 0), cell(2, 0)), sites)
	if !reflect.DeepEqual(got.Dig, []domain.Cell{cell(1, 0)}) || !reflect.DeepEqual(got.Unroof, []domain.Cell{cell(1, 0), cell(2, 0)}) || len(got.Unfit) != 0 {
		t.Fatalf("got %+v", got)
	}
	got = RockStep(sky(cell(3, 0), cell(9, 9)), sites)
	if len(got.Dig) != 0 || len(got.Unroof) != 0 || !reflect.DeepEqual(got.Unfit, []domain.Cell{cell(3, 0), cell(9, 9)}) {
		t.Fatalf("thick and unseen cells: %+v", got)
	}
	// Sky wins over floor and blocks for the same cell.
	got = RockStep([]RoleCell{{cell(1, 0), RockBlocks}, {cell(1, 0), RockNeedsSky}, {cell(1, 0), RockNeedsFloor}}, sites)
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

func TestTurbineCatchZoneIsThePairsLanes(t *testing.T) {
	p := PlanUtilities(PlanCore(utilityTestZones(), 3, BuildTierCamp), UtilityWants{TurbinePairs: 1})
	sites := PlannedPowerSites(p, WindTurbineDefinition)
	if len(sites) != 2 {
		t.Fatal(sites)
	}
	if lanes := TurbineCatchZone(p, sites[0].Area); len(lanes) != 7*22 {
		t.Fatal(len(lanes))
	}
	if TurbineCatchZone(p, Rectangle{}) != nil {
		t.Fatal("lanes for a non-turbine area")
	}
}
