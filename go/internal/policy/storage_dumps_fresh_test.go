package policy

import (
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A fresh animal corpse waits for the freezer's corpse shelf; with no shelf
// it gets the fresh dump, whose filter takes fresh animal and insect corpses
// only, so a rotting one still goes to the rotten dump.
func TestFreshAnimalCorpseHasAHomeWithoutAFreezerShelf(t *testing.T) {
	t.Parallel()
	facts := RoutineFacts{Waste: domain.Known([]WasteItem{
		{Kind: "corpse", CorpseOf: domain.CorpseAnimal, RotStage: domain.RotFresh, State: WasteExposed},
		{Kind: "corpse", CorpseOf: domain.CorpseAnimal, RotStage: domain.RotRotting, State: WasteExposed},
	})}
	needs := DumpNeeds(facts)
	if needs[domain.FreshDumpRole] != 1 || needs[domain.RottenDumpRole] != 1 {
		t.Fatalf("needs %v", needs)
	}
	roles := func(r StorageRequest) (fresh StockpileSite, rotten, shelf bool) {
		for _, s := range PlanStorage(r).Sites {
			switch {
			case s.Role == domain.FreshDumpRole:
				fresh = s
			case s.Role == domain.RottenDumpRole:
				rotten = true
			case strings.HasPrefix(s.Role, domain.CorpsesRolePrefix):
				shelf = true
			}
		}
		return
	}
	req := storeRequest(1, 1)
	req.Dumps = &DumpStore{Needs: needs, Rooms: []Room{}, Anchor: domain.Cell{X: 30, Z: 30}}
	fresh, rotten, shelf := roles(req)
	if fresh.Role == "" || fresh.Filter != domain.CorpseLarderFilter() || fresh.Priority != domain.LowPriority || !rotten || shelf {
		t.Fatalf("no freezer: fresh %+v rotten %v shelf %v", fresh, rotten, shelf)
	}
	req.Layout.Rooms[0].Role = ModuleFreezer
	if fresh, rotten, shelf = roles(req); fresh.Role != "" || !rotten || !shelf {
		t.Fatalf("freezer shelf standing: fresh %+v rotten %v shelf %v", fresh, rotten, shelf)
	}
}
