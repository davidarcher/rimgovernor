package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
)

// fakeResourceSourceNative implements only the ReadResourceSources half of
// RoutineResourceSource that sourcesForDeficit actually calls; the other
// methods are unused by this narrow test and simply panic if ever reached.
type fakeResourceSourceNative struct {
	rows    []bridge.ResourceSourceRow
	storage policy.ResourceStorage
	err     error
}

func (f *fakeResourceSourceNative) Identity(context.Context) (*l.IdentityReply, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) PreviewBill(context.Context, *c.Identity, domain.ProductionBill) (*op.PreviewReply, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error) {
	panic("unused")
}
func (f *fakeResourceSourceNative) ReadResourceSources(context.Context, *c.Identity, string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error) {
	return f.rows, f.storage, bridge.Result{}, f.err
}
func (f *fakeResourceSourceNative) PreviewAcquisition(context.Context, *c.Identity, bridge.AcquisitionTarget) (*op.PreviewReply, bridge.Result, error) {
	panic("unused")
}

func TestSourcesForDeficitSelectsAgainstOutstandingNeed(t *testing.T) {
	native := &fakeResourceSourceNative{rows: []bridge.ResourceSourceRow{
		{ThingID: "rock1", Yield: 40, Distance: 3, Method: policy.ResourceSourceMine, Safety: "open_surface"},
		{ThingID: "rock2", Yield: 40, Distance: 30, Method: policy.ResourceSourceMine, Safety: "open_surface"},
	}}
	planner := &RoutineResourcePlanner{native: native}
	stock := domain.Known([]policy.Amount{{Resource: "Steel", Count: 10}})
	selected, _, ok := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, stock)
	if !ok || len(selected) != 1 || selected[0].ThingID != "rock1" {
		t.Fatal(selected, ok)
	}
}

func TestSourcesForDeficitReturnsNilWhenStockUnknown(t *testing.T) {
	native := &fakeResourceSourceNative{}
	planner := &RoutineResourcePlanner{native: native}
	selected, _, ok := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, domain.Unknown[[]policy.Amount]())
	if ok || selected != nil {
		t.Fatal(selected, ok)
	}
}

func TestSourcesForDeficitSwallowsNativeReadFailure(t *testing.T) {
	native := &fakeResourceSourceNative{err: errors.New("native unavailable")}
	planner := &RoutineResourcePlanner{native: native}
	stock := domain.Known([]policy.Amount{{Resource: "Steel", Count: 0}})
	selected, _, ok := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, stock)
	if ok || selected != nil {
		t.Fatal(selected, ok)
	}
}

func TestMaterialStorageZoneFallbackNotHandledWithoutMineSelection(t *testing.T) {
	planner := &RoutineResourcePlanner{}
	selected := []policy.ResourceSource{{ThingID: "wood1", Yield: 40, Method: "cut"}}
	storage := policy.ResourceStorage{Capacity: 0, StackLimit: 75, Haulers: 0}
	result, handled, err := planner.materialStorageZoneFallback(context.Background(), context.Background(), ControlState{}, store.GoalState{}, 0, "WoodLog", selected, storage, time.Time{})
	if err != nil || handled || result.Reason != "" {
		t.Fatalf("got result=%v handled=%v err=%v", result, handled, err)
	}
}

func TestMaterialStorageZoneFallbackBlockedWithoutHaulers(t *testing.T) {
	planner := &RoutineResourcePlanner{}
	selected := []policy.ResourceSource{{ThingID: "rock1", Yield: 40, Method: policy.ResourceSourceMine}}
	storage := policy.ResourceStorage{Capacity: 1000, StackLimit: 75, Haulers: 0}
	result, handled, err := planner.materialStorageZoneFallback(context.Background(), context.Background(), ControlState{}, store.GoalState{}, 0, "Steel", selected, storage, time.Time{})
	if err != nil || !handled || result.Reason != BuildingMethodNoSpace {
		t.Fatalf("got result=%v handled=%v err=%v", result, handled, err)
	}
}

func TestSourcesForDeficitReturnsStorageOnSuccess(t *testing.T) {
	storage := policy.ResourceStorage{Resource: "Steel", Capacity: 5, StackLimit: 75, Haulers: 1, Candidates: []domain.Cell{{X: 1, Z: 2}}}
	native := &fakeResourceSourceNative{rows: []bridge.ResourceSourceRow{
		{ThingID: "rock1", Yield: 40, Distance: 3, Method: policy.ResourceSourceMine, Safety: "open_surface"},
	}, storage: storage}
	planner := &RoutineResourcePlanner{native: native}
	stock := domain.Known([]policy.Amount{{Resource: "Steel", Count: 0}})
	_, gotStorage, ok := planner.sourcesForDeficit(context.Background(), &c.Identity{}, "Steel", 50, stock)
	if !ok || !reflect.DeepEqual(gotStorage, storage) {
		t.Fatal(gotStorage, ok)
	}
}

func TestResourceIngredientNamesFundOnlyTheProducingRecipes(t *testing.T) {
	census := []bridge.GearBenchRead{{Bench: policy.GearBench{ID: "spot", Recipes: domain.Known([]policy.GearRecipe{
		{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Ingredients: domain.Known([][]policy.Amount{{{Resource: "WoodLog", Count: 40}, {Resource: "Steel", Count: 40}}})},
		{Definition: "Make_Apparel_TribalA", Products: []policy.Resource{"Apparel_TribalA"}, Ingredients: domain.Known([][]policy.Amount{{{Resource: "Cloth", Count: 60}, {Resource: "Leather_Plain", Count: 60}}})},
	})}}}
	if got := recipeIngredientNames(census, "MeleeWeapon_Club"); !reflect.DeepEqual(got, []string{"Steel", "WoodLog"}) {
		t.Fatal(got)
	}
	if got := recipeIngredientNames(census, "Pemmican"); len(got) != 0 {
		t.Fatal(got)
	}
}
