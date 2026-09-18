package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestIngredientStorageAllowListUnionsTheFirstAvailableRecipe(t *testing.T) {
	t.Parallel()
	gladius := policy.GearRecipe{Definition: "Make_MeleeWeapon_Gladius", Products: []policy.Resource{"MeleeWeapon_Gladius"}, Available: domain.Known(true), AvailableOn: domain.Known(true),
		Ingredients: domain.Known([][]policy.Amount{{{Resource: "Steel", Count: 50}, {Resource: "Plasteel", Count: 50}}, {{Resource: "Steel", Count: 10}}})}
	gated := gladius
	gated.Available = domain.Known(false)
	benches := []policy.GearBench{
		{ID: "spot", Recipes: domain.Known([]policy.GearRecipe{gated})},
		{ID: "smithy", Recipes: domain.Known([]policy.GearRecipe{gladius})},
	}
	bench, recipe, allow, known := ingredientStorageAllowList("MeleeWeapon_Gladius", benches)
	if !known || bench != "smithy" || recipe != "Make_MeleeWeapon_Gladius" || len(allow) != 2 || allow[0] != "Plasteel" || allow[1] != "Steel" {
		t.Fatal(bench, recipe, allow, known)
	}
	// No standing bench hosts the recipe: nothing to store for yet.
	if bench, recipe, _, known = ingredientStorageAllowList("MeleeWeapon_Gladius", benches[:1]); !known || bench != "" || recipe != "" {
		t.Fatal(bench, recipe, known)
	}
	// An unobserved ingredient list is unknown, never an empty allow list.
	unknown := gladius
	unknown.Ingredients = domain.Unknown[[][]policy.Amount]()
	if _, _, _, known = ingredientStorageAllowList("MeleeWeapon_Gladius", []policy.GearBench{{ID: "smithy", Recipes: domain.Known([]policy.GearRecipe{unknown})}}); known {
		t.Fatal("unknown ingredients must not be known")
	}
}

func TestIngredientStorageCellsStaysInsideTheWorkshopRoom(t *testing.T) {
	t.Parallel()
	room := policy.Rectangle{X: 10, Z: 10, Width: 9, Height: 9}
	var cells []policy.SiteCell
	var roomCells []domain.Cell
	for x := int32(0); x < 40; x++ {
		for z := int32(0); z < 40; z++ {
			cell := domain.Cell{X: x, Z: z}
			cells = append(cells, policy.SiteCell{Cell: cell, Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	for x := room.X + 1; x < room.X+room.Width-1; x++ {
		for z := room.Z + 1; z < room.Z+room.Height-1; z++ {
			roomCells = append(roomCells, domain.Cell{X: x, Z: z})
		}
	}
	rooms := []policy.Room{
		{ID: "kitchen", Role: domain.Known(policy.RoomRoleKitchen), Cells: []domain.Cell{{X: 1, Z: 1}, {X: 1, Z: 2}, {X: 2, Z: 1}, {X: 2, Z: 2}}},
		{ID: "workshop", Role: domain.Known(policy.RoomRoleWorkshop), Cells: roomCells},
	}
	bounds := policy.Bounds{Width: 40, Height: 40}
	sites, err := ingredientStorageSites(rooms, bounds, cells, nil)
	if err != nil || len(sites) == 0 || len(sites[0]) != 4 {
		t.Fatal(sites, err)
	}
	got := sites[0]
	inside := map[domain.Cell]bool{}
	for _, c := range roomCells {
		inside[c] = true
	}
	for _, c := range got {
		if !inside[c] || c.X < 13 || c.X > 15 || c.Z < 13 || c.Z > 15 {
			t.Fatal("site must sit inside the workshop room near its centre", got)
		}
	}
	// Every later candidate stays inside the room and none repeats the
	// first, so a refused preview has somewhere else to go.
	for _, site := range sites[1:] {
		if len(site) != 4 || site[0] == got[0] {
			t.Fatal("later candidates must be distinct 2x2 blocks", site)
		}
		for _, c := range site {
			if !inside[c] {
				t.Fatal("candidate must sit inside the workshop room", site)
			}
		}
	}
	// A reservation on the centre pushes the site aside; no workshop room means
	// no site.
	shifted, err := ingredientStorageSites(rooms, bounds, cells, got)
	if err != nil || len(shifted) == 0 || len(shifted[0]) != 4 || shifted[0][0] == got[0] {
		t.Fatal(shifted, err)
	}
	if none, err := ingredientStorageSites(rooms[:1], bounds, cells, nil); err != nil || none != nil {
		t.Fatal(none, err)
	}
}

func TestRoutineResearchWorkNeedsAResearcherUntilTheTargetFinishes(t *testing.T) {
	t.Parallel()
	p := policy.RoutinePolicy{ResourceTargets: map[policy.Resource]int64{"MeleeWeapon_Gladius": 1}}
	unknown := domain.Unknown[policy.ResearchFacts]()
	if rows := routineResearchWork(p, nil, unknown); len(rows) != 0 {
		t.Fatal("no target owes no researcher", rows)
	}
	if rows := routineResearchWork(p, []string{"Smithing"}, unknown); len(rows) != 1 || rows[0].Work != policy.WorkResearch {
		t.Fatal(rows)
	}
	current := domain.Known(policy.ResearchFacts{Current: "Smithing", Projects: []policy.ResearchProjectID{"Smithing"}})
	if rows := routineResearchWork(p, []string{"Smithing"}, current); len(rows) != 1 {
		t.Fatal("a selected but unfinished target still owes a researcher", rows)
	}
	finished := domain.Known(policy.ResearchFacts{Finished: []policy.ResearchProjectID{"Smithing"}, Projects: []policy.ResearchProjectID{"Smithing"}})
	if rows := routineResearchWork(p, []string{"Smithing"}, finished); len(rows) != 0 {
		t.Fatal("a finished target owes no researcher", rows)
	}
}

func TestResearchSelectIsARoutineExecutableKind(t *testing.T) {
	t.Parallel()
	// The research rung dispatches its selection under the routine worker
	// like every other ladder method; a kind missing here is never
	// authorized and the goal stalls on existing_work forever.
	if !routineExecutableKind(domain.ResearchSelectAction) {
		t.Fatal("research_select must be routine executable")
	}
}
