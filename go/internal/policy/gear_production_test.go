package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// parkaBatch is the batch the model fixture's pawn lacks: one cloth parka.
func parkaBatch(t *testing.T, r GearPlanningRequest) OrderSpec {
	t.Helper()
	got, err := DeclareGearOrders(r)
	if err != nil || got.Abstained() || len(got.Orders) != 1 {
		t.Fatal(got, err)
	}
	return got.Orders[0]
}

func TestGearProductionPreservesMaterialAndSharedBudget(t *testing.T) {
	r, before := gearModelFixture(), gearModelFixture()
	order := parkaBatch(t, r)
	if order.Recipe != "Make_Parka" || order.Mode != domain.GearBatch || order.Target != 1 || !reflect.DeepEqual(order.Ingredients, []string{"Cloth"}) {
		t.Fatal(order)
	}
	if !reflect.DeepEqual(r, before) {
		t.Fatal("declaration mutated caller inputs")
	}
}

func TestGearProductionRefusesUnknownOrInvalidRequiredWork(t *testing.T) {
	r := gearModelFixture()
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].RequiredWork = domain.Unknown[[]WorkRequirement]()
	if got, err := DeclareGearOrders(r); err != nil || !got.Abstained() || len(got.Orders) != 0 {
		t.Fatal(got, err)
	}
	recipes[0].RequiredWork = domain.Known([]WorkRequirement{{Work: "Tailoring", Minimum: -1}})
	if _, err := DeclareGearOrders(r); err == nil {
		t.Fatal("invalid native work requirement accepted")
	}
}

func TestGearProductionFindsExistingBillOnLaterBench(t *testing.T) {
	r := gearModelFixture()
	standing := OrderSpec{Recipe: "Make_Parka", Mode: domain.GearBatch, Target: 4, Ingredients: []string{"Synthread"}}
	benches, _ := r.Benches.Value()
	benches[0].ID = "a-new-bench"
	benches = append(benches, GearBench{ID: "z-player-bench", Bills: domain.Known([]GearBill{{Active: domain.Known(true), Products: []Resource{"Parka"}, Spec: domain.Known(standing)}})})
	r.Benches = domain.Known(benches)
	got, err := DeclareGearOrders(r)
	if err != nil || got.Abstained() || !reflect.DeepEqual(got.Orders, []OrderSpec{standing}) {
		t.Fatal("duplicated player production", got, err)
	}
	benches[1].Bills = domain.Unknown[[]GearBill]()
	r.Benches = domain.Known(benches)
	got, err = DeclareGearOrders(r)
	if err != nil || !got.Abstained() || len(got.Orders) != 0 {
		t.Fatal("unknown existing bills spent materials", got, err)
	}
}

func TestGearProductionAggregatesSlotsAndRejectsAmbiguousFlatFilter(t *testing.T) {
	r := gearModelFixture()
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}}, {{"Cloth", 50}}})
	if order := parkaBatch(t, r); !reflect.DeepEqual(order.Ingredients, []string{"Cloth"}) {
		t.Fatal(order)
	}
	// A wanted stuff the flat filter cannot isolate is refused.
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 60}, {"Synthread", 60}}, {{"Synthread", 10}}})
	if got, err := DeclareGearOrders(r); err != nil || got.Abstained() || len(got.Orders) != 0 {
		t.Fatal(got, err)
	}
}

func TestGearProductionSpendsOnlyTheWantedStuff(t *testing.T) {
	r := gearModelFixture()
	if order := parkaBatch(t, r); !reflect.DeepEqual(order.Ingredients, []string{"Cloth"}) {
		t.Fatal("wanted stuff not preferred", order)
	}
	// Cloth is wanted and the recipe also accepts leather and silver: with no
	// shared category the model's stuff alone is admitted.
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", 80}, {"Leather_Plain", 80}, {"Silver", 10}}})
	if order := parkaBatch(t, r); !reflect.DeepEqual(order.Ingredients, []string{"Cloth"}) {
		t.Fatal("unwanted material substituted", order)
	}
	// Stuffs of the wanted stuff's catalog category stand in, a valuable
	// of the same category never does.
	r.StuffCategories = map[Resource][]string{"Cloth": {"Fabric"}, "Leather_Plain": {"Fabric"}, "Silver": {"Fabric"}}
	if order := parkaBatch(t, r); !reflect.DeepEqual(order.Ingredients, []string{"Cloth", "Leather_Plain"}) {
		t.Fatal("category equivalents not admitted", order)
	}
}

func TestGearRejectsMalformedProduction(t *testing.T) {
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) {
			v, _ := r.Observation.Value()
			v.Pawns = append(v.Pawns, v.Pawns[0])
			r.Observation = domain.Known(v)
		},
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Ingredients = domain.Known([][]Amount{{{"Cloth", -1}}})
		},
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Products = []Resource{"Parka", "Parka"}
		},
	} {
		r := gearModelFixture()
		change(&r)
		if _, err := DeclareGearOrders(r); err == nil {
			t.Fatal("malformed evidence accepted")
		}
	}
}

func TestGearProductionKeepsUnknownEvidenceUnknown(t *testing.T) {
	for _, change := range []func(*GearPlanningRequest){
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Available = domain.Unknown[bool]()
		},
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].AvailableOn = domain.Unknown[bool]()
		},
		func(r *GearPlanningRequest) {
			b, _ := r.Benches.Value()
			recipes, _ := b[0].Recipes.Value()
			recipes[0].Ingredients = domain.Unknown[[][]Amount]()
		},
	} {
		r := gearModelFixture()
		change(&r)
		if got, err := DeclareGearOrders(r); err != nil || !got.Abstained() || len(got.Orders) != 0 {
			t.Fatal(got, err)
		}
	}
}
