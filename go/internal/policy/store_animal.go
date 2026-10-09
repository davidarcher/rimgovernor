package policy

import (
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// FeedRolePrefix keys a barn's feed stockpile: "feed:<barn interior corner>".
const FeedRolePrefix = "feed:"

// FeedStoreWidth and FeedStoreHeight size the barn feed store once: a 1x2 strip,
// the largest that fits the aisle between the template's rows of spots, in the
// barn's free floor, never resized.
const FeedStoreWidth, FeedStoreHeight = 1, 2

// AnimalFeedStore is the herds' feed store: the feed their races eat
// and the animal sleeping spot the barn's template is planned with, which the
// store is sited beside.
type AnimalFeedStore struct {
	Feed []Resource
	Spot InteriorPieceDef
}

// AnimalFeedFilterOf is the feed the animals' races eat that a bench makes or
// a field grows (hay), sorted: what the barn feed store accepts. Empty while
// the races are not in the catalog.
func AnimalFeedFilterOf(animals []UpkeepAnimal, races AnimalRaceCatalog) []Resource {
	seen := map[Resource]bool{}
	for _, a := range animals {
		race, known := races.Race(a.Definition)
		if !known {
			continue
		}
		for _, item := range race.FeedItems {
			seen[item.Def] = true
		}
		if slices.Contains(race.Edible, string(HayResource)) {
			seen[HayResource] = true
		}
	}
	out := make([]Resource, 0, len(seen))
	for def := range seen {
		out = append(out, def)
	}
	sort.Slice(out, func(i, j int) bool { return out[i] < out[j] })
	return out
}

// animalOwner is the People department's animal store: the barn's feed
// stockpile, a small feed-filtered zone at Important priority in each planned
// barn's free floor beside the sleeping spots. A herd with no barn
// has none: its feed is held by the warehouse or the freezer.
type animalOwner struct{}

func (animalOwner) Department() Department { return DepartmentPeople }

func (animalOwner) Stores(v StoreView) []Store {
	if v.Layout == nil || v.AnimalFeed == nil || len(v.AnimalFeed.Feed) == 0 {
		return nil
	}
	defs := make([]string, len(v.AnimalFeed.Feed))
	for i, def := range v.AnimalFeed.Feed {
		defs[i] = string(def)
	}
	filter, err := domain.AllowOnlyFilter(defs)
	if err != nil {
		return nil
	}
	var out []Store
	for _, barn := range v.Layout.HerdRooms(PlannedBarn) {
		var avoid []domain.Cell
		if in, ok := InteriorRoomFromLayout(barn, v.Shapes); ok {
			if plan, ok := PlanInterior(in, v.AnimalFeed.Spot); ok {
				for _, p := range plan.Pieces {
					avoid = append(avoid, rectCells(p.Rect)...)
				}
			}
		}
		out = append(out, Store{StoreSite: StoreSite{Role: plannedKey(FeedRolePrefix, barn.Interior), Interior: barn.Interior, Width: FeedStoreWidth, Height: FeedStoreHeight,
			Anchor: barn.Door, Avoid: avoid, Filter: filter, Priority: domain.ImportantPriority}})
	}
	return out
}

func (animalOwner) RoomDemand(StoreView) RoomDemand { return RoomDemand{} }
