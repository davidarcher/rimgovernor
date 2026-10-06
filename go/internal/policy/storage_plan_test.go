package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The Food department declares nothing without a meal store or a layout, and
// the whole-room meal store otherwise.
func TestFoodStoresMealStoreAndUnknownLayout(t *testing.T) {
	t.Parallel()
	if got := DeclareStores(StorageRequest{}).Stores; len(got) != 0 {
		t.Fatalf("empty view declared %+v", got)
	}
	room := Room{ID: "Room_1", Cells: []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}}}
	stores := DeclareStores(StorageRequest{Meals: &MealStore{Room: room, Filter: domain.PerishablesFilter(), Whole: true}}).Stores
	if len(stores) != 1 || stores[0].Role != domain.MealsRolePrefix+"Room_1" || stores[0].Priority != domain.CriticalPriority || stores[0].Width != 0 {
		t.Fatalf("%+v", stores)
	}
}
