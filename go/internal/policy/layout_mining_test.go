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
