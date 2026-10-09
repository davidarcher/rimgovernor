package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func parkaOrder(count int32) OrderSpec {
	return OrderSpec{Recipe: "Make_Parka", Ingredients: []string{"Cloth"}, Mode: domain.GearBatch, Target: count, BenchKind: "TableTailor"}
}

func withBenchDef(r GearPlanningRequest, def string) GearPlanningRequest {
	benches, _ := r.Benches.Value()
	benches = append([]GearBench(nil), benches...)
	benches[0].Def = def
	r.Benches = domain.Known(benches)
	return r
}

// A pawn lacking a garment declares one demand-sized batch of the first funded
// recipe, netted against stored stock.
func TestDeclareGearOrdersBatchesTheGap(t *testing.T) {
	r := withBenchDef(gearModelFixture(), "TableTailor")
	got, err := DeclareGearOrders(r)
	if err != nil || got.Abstain || !reflect.DeepEqual(got.Orders, []OrderSpec{parkaOrder(1)}) {
		t.Fatal(got, err)
	}
	v, _ := r.Observation.Value()
	v.Stored = domain.Known([]GearStock{{"Parka", "Cloth", 2, 9, 1}})
	r.Observation = domain.Known(v)
	if got, err = DeclareGearOrders(r); err != nil || got.Abstain || len(got.Orders) != 0 {
		t.Fatal("stored stock covers the gap", got, err)
	}
}

// A bill that already makes the need is declared as it stands, which keeps it
// whatever the recipe's current funding says.
func TestDeclareGearOrdersKeepsTheStandingBill(t *testing.T) {
	r := withBenchDef(gearModelFixture(), "TableTailor")
	standing := OrderSpec{Recipe: "Make_Parka", Ingredients: []string{"Synthread"}, Mode: domain.GearBatch, Target: 4, BenchKind: "TableTailor"}
	benches, _ := r.Benches.Value()
	benches[0].Bills = domain.Known([]GearBill{{ID: "Bill_1", Products: []Resource{"Parka"}, Active: domain.Known(true), Spec: domain.Known(standing)}})
	got, err := DeclareGearOrders(r)
	if err != nil || got.Abstain || !reflect.DeepEqual(got.Orders, []OrderSpec{standing}) {
		t.Fatal(got, err)
	}
	// A finished bill keeps nothing.
	benches[0].Bills = domain.Known([]GearBill{{ID: "Bill_1", Products: []Resource{"Parka"}, Active: domain.Known(true), Spent: true, Spec: domain.Known(standing)}})
	if got, err = DeclareGearOrders(r); err != nil || !reflect.DeepEqual(got.Orders, []OrderSpec{parkaOrder(1)}) {
		t.Fatal(got, err)
	}
	// A standing bill whose spec was unread is unknown, so nothing is removed.
	benches[0].Bills = domain.Known([]GearBill{{ID: "Bill_1", Products: []Resource{"Parka"}, Active: domain.Known(true)}})
	if got, err = DeclareGearOrders(r); err != nil || !got.Abstain {
		t.Fatal("an unread bill spec must abstain", got, err)
	}
}

// A wearable item is worn before anything is crafted: a pending candidate
// declares no new order.
func TestDeclareGearOrdersWearsBeforeCrafting(t *testing.T) {
	r := withBenchDef(gearModelFixture(), "TableTailor")
	v, _ := r.Observation.Value()
	worn := loadoutOption("shelf-parka", GearSkinTorso)
	worn.Source = GearLoose
	worn.Definition = "Parka"
	worn.Stuff = "Cloth"
	v.Pawns[0].LoadoutModel = domain.Known(GearLoadoutInput{Female: true, Options: []GearOption{worn}})
	v.Pawns[0].Candidates = domain.Known([]GearCandidate{{Target: "shelf-parka", Definition: "Parka", Gain: 1}})
	r.Observation = domain.Known(v)
	got, err := DeclareGearOrders(r)
	if err != nil || got.Abstain || len(got.Orders) != 0 {
		t.Fatal(got, err)
	}
}

func TestDeclareGearOrdersAbstainsOnUnreadFacts(t *testing.T) {
	r := withBenchDef(gearModelFixture(), "TableTailor")
	r.Benches = domain.Unknown[[]GearBench]()
	if got, err := DeclareGearOrders(r); err != nil || !got.Abstain {
		t.Fatal("an unread bench census must abstain", got, err)
	}
	r = withBenchDef(gearModelFixture(), "TableTailor")
	r.Observation = domain.Unknown[GearObservation]()
	if got, err := DeclareGearOrders(r); err != nil || !got.Abstain {
		t.Fatal("an unread census must abstain", got, err)
	}
	r = withBenchDef(gearModelFixture(), "TableTailor")
	benches, _ := r.Benches.Value()
	recipes, _ := benches[0].Recipes.Value()
	recipes[0].Ingredients = domain.Unknown[[][]Amount]()
	if got, err := DeclareGearOrders(r); err != nil || !got.Abstain {
		t.Fatal("an unread recipe must abstain", got, err)
	}
}

// Armor is the armory's: gear declares none.
func TestDeclareGearLeavesArmorToTheArmory(t *testing.T) {
	r := armoryArmorFixture()
	if got, err := DeclareGearOrders(r); err != nil || got.Abstain || len(got.Orders) != 0 {
		t.Fatal(got, err)
	}
}

func TestDeclareArmoryOrdersWeaponsArmorAndShells(t *testing.T) {
	r := withBenchDef(gearModelFixture(), "TableTailor")
	v, _ := r.Observation.Value()
	v.Pawns[0] = gearDressedPawn("pawn", loadoutOption("Parka", GearSkinTorso))
	r.Observation = domain.Known(v)
	got, err := DeclareArmoryOrders(r, ArmoryTierSmithing, []Amount{{"Parka", 3}}, nil)
	if err != nil || got.Abstain || len(got.Orders) != 1 || got.Orders[0].Recipe != "Make_Parka" || got.Orders[0].Target != 3 {
		t.Fatal("weapon batch", got, err)
	}
	if got, err = DeclareArmoryOrders(r, ArmoryTierUnknown, []Amount{{"Parka", 3}}, nil); err != nil || !got.Abstain {
		t.Fatal("an unknown tier must abstain", got, err)
	}
}

func TestDeclareArmoryOrdersArmorLadderByTier(t *testing.T) {
	for tier, want := range map[ArmoryTier]string{ArmoryTierFabrication: "Make_Apparel_PowerArmor", ArmoryTierMachining: "Make_Apparel_FlakVest", ArmoryTierSmithing: "", ArmoryTierNeolithic: ""} {
		r := armoryArmorFixture()
		benches, _ := r.Benches.Value()
		benches[0].Def = "TableFabrication"
		got, err := DeclareArmoryOrders(r, tier, nil, nil)
		if err != nil || got.Abstain {
			t.Fatal(tier, got, err)
		}
		if want == "" {
			if len(got.Orders) != 0 {
				t.Fatal(tier, got)
			}
			continue
		}
		if len(got.Orders) != 1 || got.Orders[0].Recipe != want || got.Orders[0].Target != 2 || got.Orders[0].Mode != domain.GearBatch || got.Orders[0].BenchKind != "TableFabrication" {
			t.Fatal(tier, got)
		}
	}
}

func TestDeclareArmoryOrdersShellsAreStockTargets(t *testing.T) {
	recipe := func(def string, p Resource) GearRecipe {
		return GearRecipe{Definition: def, Products: []Resource{p}, Available: domain.Known(true), AvailableOn: domain.Known(true)}
	}
	bench := GearBench{ID: "machining", Def: "TableMachining", Bills: domain.Known([]GearBill{}), Recipes: domain.Known([]GearRecipe{recipe("Make_Shell_HighExplosive", testShellHE), recipe("Make_Shell_Incendiary", testShellIncendiary)})}
	r := gearModelFixture()
	v, _ := r.Observation.Value()
	v.Pawns[0] = gearDressedPawn("pawn", loadoutOption("Parka", GearSkinTorso))
	r.Observation = domain.Known(v)
	r.Benches = domain.Known([]GearBench{bench})
	targets := MortarShellTargets(1, ArmoryAssessment{Threat: ArmoryTierFabrication, Tier: ArmoryTierMachining}, testShells)
	got, err := DeclareArmoryOrders(r, ArmoryTierMachining, nil, targets)
	want := []OrderSpec{
		{Recipe: "Make_Shell_HighExplosive", Mode: domain.StockTarget, Target: 10, BenchKind: "TableMachining"},
		{Recipe: "Make_Shell_Incendiary", Mode: domain.StockTarget, Target: 5, BenchKind: "TableMachining"},
	}
	if err != nil || got.Abstain || !reflect.DeepEqual(got.Orders, want) {
		t.Fatal(got, err)
	}
	// A shell some bill already makes is declared as that bill stands.
	standing := OrderSpec{Recipe: "Make_Shell_HighExplosive", Mode: domain.StockTarget, Target: 7, BenchKind: "TableMachining"}
	bench.Bills = domain.Known([]GearBill{{ID: "Bill_1", Products: []Resource{testShellHE}, Spec: domain.Known(standing)}})
	r.Benches = domain.Known([]GearBench{bench})
	got, err = DeclareArmoryOrders(r, ArmoryTierMachining, nil, targets)
	if err != nil || got.Abstain || !reflect.DeepEqual(got.Orders, []OrderSpec{standing, want[1]}) {
		t.Fatal(got, err)
	}
}
