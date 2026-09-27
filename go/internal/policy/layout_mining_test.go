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
	p := PlanUtilities(PlanCore(coreTestZones(), 3), UtilityWants{})
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
			cells = append(cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, NaturalRock: domain.Known(true)})
		}
	}
	dig := map[domain.Cell]bool{}
	for _, c := range p.RoomDig(freezer, cells) {
		dig[c] = true
	}
	want := RectangleCells(in)
	want = append(append(want, freezer.Door, site.Cell), RectangleCells(shaft)...)
	if len(dig) != len(want) {
		t.Fatalf("dig %d cells, want %d", len(dig), len(want))
	}
	for _, c := range want {
		if !dig[c] {
			t.Fatalf("%v not dug", c)
		}
	}
	if got := p.ExhaustDig(freezer, cells); got != nil {
		t.Fatalf("rock back wall dug %v", got)
	}
	for i := range cells {
		if cells[i].Cell == site.Cell {
			cells[i].NaturalRock = domain.Known(false)
		}
	}
	if got := p.ExhaustDig(freezer, cells); len(got) != len(RectangleCells(shaft)) {
		t.Fatalf("shaft dig %v, want %v", got, shaft)
	}
}
