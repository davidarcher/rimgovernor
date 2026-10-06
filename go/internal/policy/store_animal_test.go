package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestAnimalFeedStoreDeclaredWhenNothingDeliversFeed(t *testing.T) {
	cells := []domain.Cell{{X: 6, Z: 5}, {X: 7, Z: 5}, {X: 8, Z: 5}, {X: 6, Z: 6}, {X: 7, Z: 6}, {X: 8, Z: 6}}
	group := feedGroup("Husky", 2, "husky1", "husky2")
	group.ReachableBenches, group.StorageCandidates = nil, cells
	got := AnimalFeedStores([]AnimalFeedGroup{group}, testRaces)
	if len(got) != 1 || got[0].Race != "Husky" || !reflect.DeepEqual(got[0].Cells, cells) || len(got[0].Feed) == 0 {
		t.Fatalf("stores %+v", got)
	}
	stores := animalOwner{}.Stores(StoreView{AnimalFeed: got})
	if len(stores) != 1 || stores[0].Role != FeedRolePrefix+"Husky" || stores[0].Priority != domain.ImportantPriority || stores[0].Width != FeedStoreWidth || stores[0].Height != FeedStoreHeight {
		t.Fatalf("declared %+v", stores)
	}
	if stores[0].Role == "" || stores[0].Filter.Base() != domain.BaseNothing {
		t.Fatalf("a feed store has a role and an allow-only filter: %+v", stores[0])
	}
}

func TestAnimalFeedStoreNotDeclaredWhenBenchOrZoneDelivers(t *testing.T) {
	cells := []domain.Cell{{X: 6, Z: 5}, {X: 6, Z: 6}}
	group := feedGroup("Husky", 2, "husky1", "husky2")
	group.StorageCandidates = cells
	group.ReachableBenches = []string{"bench1"}
	if got := AnimalFeedStores([]AnimalFeedGroup{group}, testRaces); len(got) != 0 {
		t.Fatalf("a reachable bench needs no store: %+v", got)
	}
	group.ReachableBenches = nil
	kibble := []AnimalFeedStorage{{Zone: "Zone_3", Accepts: []string{"Hay", "Kibble"}}}
	group.ReachableStorage = [][]AnimalFeedStorage{kibble, kibble}
	if got := AnimalFeedStores([]AnimalFeedGroup{group}, testRaces); len(got) != 0 {
		t.Fatalf("a standing zone needs no store: %+v", got)
	}
	group.ReachableStorage, group.StorageCandidates = nil, nil
	if got := AnimalFeedStores([]AnimalFeedGroup{group}, testRaces); len(got) != 0 {
		t.Fatalf("no ground, no store: %+v", got)
	}
}
