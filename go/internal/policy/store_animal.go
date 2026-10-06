package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FeedRolePrefix keys a People animal-feed store: "feed:<race>".
const FeedRolePrefix = "feed:"

// FeedStoreWidth and FeedStoreHeight size the animal feed store once: a fixed
// rectangle on the animals' shared reachable ground, never resized.
const FeedStoreWidth, FeedStoreHeight = 3, 2

// AnimalFeedStore is one race group's feed store the animals lack: the
// connected ground every animal of the group reaches (an observed fact, so the
// store is sited from it rather than from the plan) and the feed the race eats.
type AnimalFeedStore struct {
	Race  Resource
	Feed  []Resource
	Cells []domain.Cell
}

// AnimalFeedStores are the groups short of feed with no reachable bench and no
// reachable zone accepting their feed, and ground to put one on. A group whose
// feed a bench or a standing zone already delivers declares nothing.
func AnimalFeedStores(groups []AnimalFeedGroup, races AnimalRaceCatalog) []AnimalFeedStore {
	var out []AnimalFeedStore
	for _, g := range groups {
		race, known := races.Race(g.Definition)
		if !known || len(g.ReachableBenches) > 0 || len(g.StorageCandidates) == 0 {
			continue
		}
		var feed []Resource
		delivered := false
		for _, item := range race.FeedItems {
			feed = append(feed, item.Def)
			delivered = delivered || storageDelivers(g.ReachableStorage, item.Def)
		}
		if delivered || len(feed) == 0 {
			continue
		}
		out = append(out, AnimalFeedStore{Race: g.Definition, Feed: feed, Cells: g.StorageCandidates})
	}
	return out
}

// animalOwner is the People department's animal stores: the feed store each
// short herd lacks, declared through the department entity at Important
// priority with an allow-only filter of the race's feed.
type animalOwner struct{}

func (animalOwner) Department() Department { return DepartmentPeople }

func (animalOwner) Stores(v StoreView) []Store {
	var out []Store
	for _, f := range v.AnimalFeed {
		defs := make([]string, len(f.Feed))
		for i, def := range f.Feed {
			defs[i] = string(def)
		}
		filter, err := domain.AllowOnlyFilter(defs)
		if err != nil {
			continue
		}
		out = append(out, Store{StoreSite: StoreSite{Role: FeedRolePrefix + string(f.Race), Width: FeedStoreWidth, Height: FeedStoreHeight, Anchor: f.Cells[0], Filter: filter, Priority: domain.ImportantPriority, room: f.Cells}})
	}
	return out
}

func (animalOwner) RoomDemand(StoreView) RoomDemand { return RoomDemand{} }
