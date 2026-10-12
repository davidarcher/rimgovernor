package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// objectiveGraph is the station graph of a small colony: five colonists, one
// hurt, cooking from cold-stored meat and cutting blocks from stone. Every
// weight in it is the graph's own.
func objectiveGraph(t *testing.T) StationGraph {
	t.Helper()
	shape := func(def string, w, h int32) InteriorPieceDef {
		return InteriorPieceDef{Def: def, Size: domain.Cell{X: w, Z: h}}
	}
	cold := []EnvNeed{{Kind: NeedCold}}
	store := []RoleContribution{{Role: RoomRoleStoreroom, Weight: 1, Additive: true}}
	in := StationGraphInput{
		Catalog: StationCatalog{Stations: map[string]Station{
			"Bed":                {Def: "Bed", Shape: shape("Bed", 1, 2), Capacity: StationCapacity{Kind: CapacitySleepers, Slots: 1}},
			testStove:            {Def: testStove, Shape: shape(testStove, 3, 1), Capacity: StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"CookMeal", 450}}}, Contributions: []RoleContribution{{Role: RoomRoleKitchen, Weight: 1}}},
			testWorkshop:         {Def: testWorkshop, Shape: shape(testWorkshop, 3, 1), Capacity: StationCapacity{Kind: CapacityWorkers, Slots: 1, Recipes: []RecipeWorkAmount{{"MakeBlocks", 1500}}}},
			testDining.Table.Def: {Def: testDining.Table.Def, Shape: testDining.Table},
			"ShelfSmall":         {Def: "ShelfSmall", Shape: shape("ShelfSmall", 1, 1), Contributions: store, Capacity: StationCapacity{Kind: CapacityItems, Slots: 8}},
			"ColdShelf":          {Def: "ColdShelf", Shape: shape("ColdShelf", 1, 1), Needs: cold, Contributions: store, Capacity: StationCapacity{Kind: CapacityItems, Slots: 4}},
		}},
		Furniture:  testFurniture,
		Dining:     testDining,
		Items:      ItemFacts{Nutrition: map[Resource]float64{"Meal": 0.9}},
		Population: StationPopulation{Colonists: 5, Injured: 1, Prisoners: 1},
		Recipes: map[string]RecipeFlow{
			"CookMeal":   {Ingredients: [][]Amount{{{Resource: "Meat", Count: 1}}}, Products: []Amount{{Resource: "Meal", Count: 1}}, Food: true},
			"MakeBlocks": {Ingredients: [][]Amount{{{Resource: "Stone", Count: 1}}}, Products: []Amount{{Resource: "Blocks", Count: 1}}},
		},
		NutritionPerDay: 5 * 1.6,
		Orders:          []OrderSpec{{Recipe: "MakeBlocks", BenchKind: testWorkshop, Mode: domain.StockTarget, Target: 10}},
		Stock:           map[Resource]int64{"Meat": 100, "Stone": 200, "Blocks": 50},
		StackLimit:      map[Resource]int64{"Meat": 75, "Stone": 75, "Blocks": 75},
		Perishable:      map[Resource]bool{"Meat": true},
	}
	g, err := BuildStationGraph(in)
	if err != nil {
		t.Fatal(err)
	}
	return g
}

// The door-to-bed weight is the graph's meal flow, not a constant, and a
// stove's door traffic is the ingredients it fetches plus the meals it
// carries out.
func TestGraphFlowsWeightsComeFromTheGraph(t *testing.T) {
	g := objectiveGraph(t)
	bed := NewInteriorPiece("bed", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: 0, Z: 0})
	stove := NewInteriorPiece("stove", testStove, domain.Cell{X: 3, Z: 1}, domain.North, domain.Cell{X: 0, Z: 3})
	doorWeight := func(slot string, pieces ...InteriorPiece) float64 {
		w := 0.0
		for _, e := range (GraphFlows{Graph: g, Sleep: PurposeSleep, Furniture: testFurniture}).Edges(pieces) {
			if e.From.Door && e.To.Slot == slot {
				w += e.Weight
			}
		}
		return w
	}
	table := testDining.Table.Def + "/dine"
	meal := g.Weight("Bed/sleep", table)
	if meal <= 0 {
		t.Fatalf("graph has no bed to table flow: %+v", g.Edges)
	}
	if got := doorWeight("bed", bed); math.Abs(got-meal) > 1e-9 {
		t.Errorf("door to bed %v, want the meal flow %v", got, meal)
	}
	want := g.Weight("ColdShelf/store_cold", testStove+"/work") + g.Weight(testStove+"/work", table)
	if got := doorWeight("stove", stove); math.Abs(got-want) > 1e-9 || want <= 0 {
		t.Errorf("door to stove %v, want ingredients plus meals %v", got, want)
	}
	shelf := NewInteriorPiece("shelf", "ColdShelf", domain.Cell{X: 1, Z: 1}, domain.North, domain.Cell{X: 4, Z: 3})
	for _, e := range (GraphFlows{Graph: g, Sleep: PurposeSleep, Furniture: testFurniture}).Edges([]InteriorPiece{stove, shelf}) {
		if !e.From.Door && !e.To.Door && e.Weight != g.Weight("ColdShelf/store_cold", testStove+"/work") {
			t.Errorf("stove-shelf edge %v, want the ingredient flow", e.Weight)
		}
	}
}

// objectiveFlows are the graph flows of a room of the role, its beds keyed to
// the role's sleep purpose.
func objectiveFlows(t *testing.T, role RoomRole) GraphFlows {
	return GraphFlows{Graph: objectiveGraph(t), Sleep: SleepPurposeOf(role), Furniture: testFurniture}
}

// One Bed def serves the colonist, patient and prisoner: a bed's flows follow
// the room's purpose, a prison cell's bed takes no meal flow, a hospital's
// plain bed takes the patients' share, and sleep traffic is informational
// while the stove's recipe traffic is gated.
func TestGraphFlowsKeyBedsOnPurposeNotDef(t *testing.T) {
	g := objectiveGraph(t)
	bed := NewInteriorPiece("bed", "Bed", domain.Cell{X: 1, Z: 2}, domain.North, domain.Cell{X: 0, Z: 0})
	table := testDining.Table.Def + "/dine"
	doorTo := func(role RoomRole, p InteriorPiece) (w float64, info bool) {
		for _, e := range objectiveFlows(t, role).Edges([]InteriorPiece{p}) {
			if e.From.Door && e.To.Slot == p.Slot {
				w += e.Weight
				info = e.Informational
			}
		}
		return w, info
	}
	if w, info := doorTo(RoomRoleBedroom, bed); math.Abs(w-g.Weight("Bed/sleep", table)) > 1e-9 || w <= 0 || !info {
		t.Errorf("bedroom bed: weight %v informational %v, want the colonists' meal flow %v", w, info, g.Weight("Bed/sleep", table))
	}
	if w, info := doorTo(RoomRoleHospital, bed); math.Abs(w-g.Weight("Bed/sleep_medical", table)) > 1e-9 || w <= 0 || !info {
		t.Errorf("hospital plain bed: weight %v informational %v, want the patients' flow %v", w, info, g.Weight("Bed/sleep_medical", table))
	}
	if w, _ := doorTo(RoomRolePrisonCell, bed); w != 0 {
		t.Errorf("prison bed picked up %v of meal flow", w)
	}
	if w, _ := doorTo(RoomRoleWorkshop, bed); w != 0 {
		t.Errorf("a room with no sleep purpose took bed flow %v", w)
	}
	stove := NewInteriorPiece("stove", testStove, domain.Cell{X: 3, Z: 1}, domain.North, domain.Cell{X: 0, Z: 3})
	for _, e := range objectiveFlows(t, RoomRoleKitchen).Edges([]InteriorPiece{stove}) {
		if e.Informational {
			t.Errorf("stove edge %+v is informational", e)
		}
	}
}
