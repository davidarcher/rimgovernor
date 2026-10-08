package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func barnFeedView(t *testing.T) (StoreView, PlannedRoom) {
	t.Helper()
	plan := herdTestPlan(t, 6)
	barn := plan.HerdRooms(PlannedBarn)[0]
	return StoreView{Layout: &plan, Shapes: testShapes, AnimalFeed: &AnimalFeedStore{Feed: []Resource{"Hay", "Kibble"}, Spot: testHerdFurniture.Spot}}, barn
}

// Each planned barn declares one small Important feed stockpile inside it, off
// the planned sleeping spots, filtered to the herd's feed.
func TestAnimalFeedStoreInThePlannedBarn(t *testing.T) {
	view, barn := barnFeedView(t)
	edit, ok := barnStoreCreates(view)[plannedKey(FeedRolePrefix, barn.Interior)]
	if !ok || len(edit.Cells()) != FeedStoreWidth*FeedStoreHeight || edit.Priority != domain.ImportantPriority || !withinRect(edit.Cells(), barn.Interior) {
		t.Fatalf("%+v", edit)
	}
	if f := edit.Filter; f.Base() != domain.BaseNothing || len(f.Allow()) != 2 {
		t.Fatal(f)
	}
	if in, ok := InteriorRoomFromLayout(barn, testShapes); ok {
		if plan, ok := PlanInterior(in, testHerdFurniture.Spot); ok {
			taken := map[domain.Cell]bool{}
			for _, p := range plan.Pieces {
				for _, c := range rectCells(p.Rect) {
					taken[c] = true
				}
			}
			for _, c := range edit.Cells() {
				if taken[c] {
					t.Fatal("feed on a planned sleeping spot", c)
				}
			}
		}
	}
}

// barnStoreCreates is storeCreates on open ground wide enough for the herd
// plan, which sits far from the origin.
func barnStoreCreates(view StoreView) map[string]StockpileEdit {
	view.Bounds = Bounds{Width: 200, Height: 200}
	for x := int32(80); x < 140; x++ {
		for z := int32(100); z < 150; z++ {
			view.Cells = append(view.Cells, SiteCell{Cell: domain.Cell{X: x, Z: z}, Roofed: domain.Known(false), Walkable: domain.Known(true), Things: OccupantThings(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
		}
	}
	review := PlanStockpileMaintenance(StockpileRequest{Tick: 1, Bounds: view.Bounds, Cells: view.Cells, Protected: view.Protected, Stores: DeclareStores(view).Stores})
	out := map[string]StockpileEdit{}
	for _, e := range review.Edits {
		if e.Kind == StockpileCreate {
			out[e.Role] = e
		}
	}
	return out
}

// No barn, unread feed or an unread layout declares nothing: the feed is
// held by the warehouse or the freezer.
func TestAnimalFeedStoreNeedsABarnAndFeed(t *testing.T) {
	view, _ := barnFeedView(t)
	none := view
	none.Layout = &LayoutPlan{}
	if got := (animalOwner{}).Stores(none); len(got) != 0 {
		t.Fatalf("no barn, no store: %+v", got)
	}
	none = view
	none.AnimalFeed = nil
	if got := (animalOwner{}).Stores(none); len(got) != 0 {
		t.Fatalf("unread feed: %+v", got)
	}
	none = view
	none.AnimalFeed = &AnimalFeedStore{}
	if got := (animalOwner{}).Stores(none); len(got) != 0 {
		t.Fatalf("no feed: %+v", got)
	}
	none = view
	none.Layout = nil
	if got := (animalOwner{}).Stores(none); len(got) != 0 {
		t.Fatalf("unread layout: %+v", got)
	}
}
