package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestMiningFollowsTheLayoutPlanTiers(t *testing.T) {
	plan := LayoutPlan{
		Rooms: []LayoutRoom{{Role: ModuleReserve, Interior: Rectangle{X: 10, Z: 10, Width: 5, Height: 5}, Dug: true}},
		Zones: []LayoutZone{
			{Kind: ZoneMining, Runs: []RowRun{{Z: 1, X: 0, Length: 2}}, Ore: true},
			{Kind: ZoneMining, Runs: []RowRun{{Z: 9, X: 9, Length: 7}, {Z: 2, X: 2, Length: 3}}},
		},
	}
	for cell, want := range map[domain.Cell]int{
		{X: 1, Z: 1}: MineTierOre, {X: 9, Z: 9}: MineTierCore, {X: 3, Z: 2}: MineTierStone,
	} {
		if got := plan.MineTier(cell); got != want {
			t.Errorf("%v: tier %d, want %d", cell, got, want)
		}
	}
	lone := LayoutPlan{Zones: plan.Zones[:1]}
	if got := lone.MineTier(domain.Cell{X: 1, Z: 1}); got != MineTierOre {
		t.Errorf("lone ore zone: tier %d, want ore", got)
	}
	src := func(id string, d float64, cell domain.Cell) ResourceSource {
		return ResourceSource{ThingID: id, Yield: 10, Distance: d, Method: ResourceSourceMine, Safety: "open_surface", Cell: cell, Reachable: domain.Known(true)}
	}
	sources := []ResourceSource{src("stone", 1, domain.Cell{X: 3, Z: 2}), src("core", 5, domain.Cell{X: 12, Z: 12}), src("ore", 50, domain.Cell{X: 0, Z: 1})}
	for i := range sources {
		sources[i].Tier = plan.MineTier(sources[i].Cell)
	}
	if got := SelectResourceSources(sources, 100, 0, 0); len(got) != 1 || got[0].ThingID != "ore" {
		t.Fatalf("with ore: %+v", got)
	}
	if got := SelectResourceSources(sources[:2], 100, 0, 0); len(got) != 1 || got[0].ThingID != "core" {
		t.Fatalf("without ore: %+v", got)
	}
}

// TestPlannedDig (#836): a dug cooled room mines its interior, door, cooler
// cell and shaft ahead of the ring; a standing room digs only its shaft,
// and nothing while its back wall is still rock.
func TestPlannedDig(t *testing.T) {
	p := PlanUtilities(PlanCore(coreTestZones(), 3, BuildTierCamp), UtilityWants{})
	var freezer LayoutRoom
	for _, r := range p.Rooms {
		if r.Role == ModuleFreezer {
			freezer = r
		}
	}
	site, shaft, ok := p.CoolerExhaust(freezer)
	if !ok {
		t.Fatal("no exhaust")
	}
	if got := p.MineTier(domain.Cell{X: shaft.X, Z: shaft.Z}); got != MineTierCore {
		t.Fatalf("shaft tier %d", got)
	}
	in := freezer.Interior
	var cells []SiteCell
	for x := in.X - 2; x <= in.X+in.Width+1; x++ {
		for z := in.Z - 2 - shaft.Height; z <= in.Z+in.Height+1+shaft.Height; z++ {
			cells = append(cells, rockSite(x, z))
		}
	}
	dig := map[domain.Cell]bool{}
	for _, c := range p.RoomRock(freezer, cells).Dig {
		dig[c] = true
	}
	want := RectangleCells(in)
	threshold := domain.Cell{X: freezer.Door.X, Z: freezer.Door.Z}
	if shell, err := freezer.Footprint(); err == nil {
		threshold = shell.Threshold()
	}
	want = append(append(want, freezer.Door, threshold, site.Cell), RectangleCells(shaft)...)
	for _, c := range want {
		if !dig[c] {
			t.Fatalf("%v not dug", c)
		}
	}
	// A rock back wall puts the cooler cell first in the exhaust dig, for
	// the plan that places the cooler (#874).
	exhaust, _ := p.ExhaustRock(freezer, cells)
	if len(exhaust.Dig) != len(RectangleCells(shaft))+1 || exhaust.Dig[0] != site.Cell {
		t.Fatalf("rock back wall exhaust dig %v, want cooler cell %v then %v", exhaust.Dig, site.Cell, shaft)
	}
	for i := range cells {
		if cells[i].Cell == site.Cell {
			cells[i] = openSite(site.Cell.X, site.Cell.Z)
		}
	}
	if exhaust, _ := p.ExhaustRock(freezer, cells); len(exhaust.Dig) != len(RectangleCells(shaft)) {
		t.Fatalf("shaft dig %v, want %v", exhaust.Dig, shaft)
	}
}

// TestRoomDigIncludesFoggedCells: cells the census does not list are fogged
// mountain and are dug like any rock, while listed non-rock cells are not.
func TestRoomDigIncludesFoggedCells(t *testing.T) {
	p := PlanCore(utilityTestZones(), 3, BuildTierCamp)
	var room LayoutRoom
	for _, r := range p.AllRooms() {
		room = r
		break
	}
	open := room.Interior
	cells := []SiteCell{openSite(open.X, open.Z)}
	dig := map[domain.Cell]bool{}
	for _, c := range p.RoomRock(room, cells).Dig {
		dig[c] = true
	}
	if dig[domain.Cell{X: open.X, Z: open.Z}] {
		t.Fatal("a listed open cell is dug")
	}
	if open.Width*open.Height > 1 && !dig[domain.Cell{X: open.X + open.Width - 1, Z: open.Z + open.Height - 1}] {
		t.Fatal("a fogged interior cell is not dug")
	}
	if !dig[room.Door] {
		t.Fatal("the fogged door is not dug")
	}
}
