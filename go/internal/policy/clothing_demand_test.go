package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

var clothingStuffs = map[Resource][]string{"Cloth": {"Fabric"}, "Synthread": {"Fabric"}, "Leather_Plain": {"Leathery"}, "Leather_Light": {"Leathery"}, "Steel": {"Metallic"}}

// clothingGarments is a shirt (Torso) and pants (Legs) made from fabric or
// leather at the given counts, and a flak vest that takes only steel.
func clothingGarments(shirt, pants int64) []ClothingGarment {
	cloth := func(n int64) [][]Amount {
		return [][]Amount{{{"Cloth", n}, {"Synthread", n}, {"Leather_Plain", n}, {"Leather_Light", n}}}
	}
	return []ClothingGarment{
		{Definition: "Apparel_BasicShirt", Groups: []string{"Torso"}, Slots: cloth(shirt)},
		{Definition: "Apparel_Pants", Groups: []string{"Legs"}, Slots: cloth(pants)},
		{Definition: "Apparel_FlakVest", Groups: []string{"Torso"}, Slots: [][]Amount{{{"Steel", 1}}}},
	}
}

func clothingInput(colonists int64, garments []ClothingGarment, stock ...Amount) ClothingDemandInput {
	return ClothingDemandInput{Garments: garments, Categories: clothingStuffs, Colonists: domain.Known(colonists), Stock: domain.Known(stock), Stored: domain.Known([]GearStock{})}
}

func clothingFloor(t *testing.T, in ClothingDemandInput, category string) ClothingMaterial {
	t.Helper()
	for _, m := range ClothingMaterials(in) {
		if m.Category == category {
			return m
		}
	}
	return ClothingMaterial{}
}

// The floor is the colonists times the recipes' own counts for one outfit of
// the core groups, per stuff category; it scales with both.
func TestClothingFloorScalesWithColonistsAndRecipes(t *testing.T) {
	t.Parallel()
	if got := clothingFloor(t, clothingInput(5, clothingGarments(40, 30)), "Leathery"); got.Floor != 350 {
		t.Fatalf("5 colonists: %+v", got)
	}
	if got := clothingFloor(t, clothingInput(10, clothingGarments(40, 30)), "Leathery"); got.Floor != 700 {
		t.Fatalf("10 colonists: %+v", got)
	}
	if got := clothingFloor(t, clothingInput(5, clothingGarments(60, 30)), "Fabric"); got.Floor != 450 {
		t.Fatalf("dearer shirt: %+v", got)
	}
	// Steel makes the only Torso garment of its category and no Legs one.
	if got := clothingFloor(t, clothingInput(5, clothingGarments(40, 30)), "Metallic"); got.Floor != 0 {
		t.Fatalf("metallic outfit: %+v", got)
	}
}

// The cheapest garment of each group prices the outfit, and a garment covering
// both groups is priced once.
func TestClothingFloorTakesCheapestGarmentPerGroup(t *testing.T) {
	t.Parallel()
	garments := append(clothingGarments(40, 30), ClothingGarment{Definition: "Apparel_Tribalwear", Groups: []string{"Torso", "Legs"}, Slots: [][]Amount{{{"Cloth", 50}, {"Leather_Plain", 50}}}})
	if got := clothingFloor(t, clothingInput(2, garments), "Fabric"); got.Floor != 100 {
		t.Fatalf("one dress covering both groups: %+v", got)
	}
}

// No demand when the stock holds the floor, when stored outfits cover every
// colonist, or when the facts are unknown.
func TestClothingFloorZeroWhenCovered(t *testing.T) {
	t.Parallel()
	garments := clothingGarments(40, 30)
	in := clothingInput(5, garments, Amount{"Leather_Plain", 200}, Amount{"Leather_Light", 150})
	if got := clothingFloor(t, in, "Leathery"); got.Deficit() != 0 || got.Held != 350 {
		t.Fatalf("stock covers: %+v", got)
	}
	if needs := ClothingResourceNeeds(ClothingMaterials(clothingInput(5, garments, Amount{"Leather_Plain", 350}, Amount{"Cloth", 350})), in.Stock); needs != nil {
		t.Fatalf("needs with stock covering: %v", needs)
	}
	in = clothingInput(5, garments)
	in.Stored = domain.Known([]GearStock{{Definition: "Apparel_BasicShirt", Quality: 2, HPBand: 10, Count: 5}, {Definition: "Apparel_Pants", Quality: 2, HPBand: 10, Count: 4}})
	if got := clothingFloor(t, in, "Leathery"); got.Floor != 70 {
		t.Fatalf("four stored outfits of five: %+v", got)
	}
	in.Stored = domain.Known([]GearStock{{Definition: "Apparel_BasicShirt", Quality: 2, HPBand: 10, Count: 5}, {Definition: "Apparel_Pants", Quality: 2, HPBand: 10, Count: 5}})
	if ms := ClothingMaterials(in); len(ms) != 0 {
		t.Fatalf("stored outfits cover everyone: %+v", ms)
	}
	in = clothingInput(5, garments)
	in.Colonists = domain.Unknown[int64]()
	if ms := ClothingMaterials(in); len(ms) != 0 {
		t.Fatalf("unknown colonists: %+v", ms)
	}
}

// The floor is asked of the member holding the most, as that member's stock
// plus the category's deficit, so the ordinary deficit is the category's.
func TestClothingNeedsAskTheHeldMember(t *testing.T) {
	t.Parallel()
	stock := domain.Known([]Amount{{"Leather_Light", 100}, {"Leather_Plain", 20}})
	materials := ClothingMaterials(clothingInput(5, clothingGarments(40, 30), []Amount{{"Leather_Light", 100}, {"Leather_Plain", 20}}...))
	needs := ClothingResourceNeeds(materials, stock)
	if needs["Leather_Light"] != 100+230 {
		t.Fatalf("needs %v", needs)
	}
	if _, asked := needs["Leather_Plain"]; asked {
		t.Fatalf("a second member asked: %v", needs)
	}
}

// A leather shortage with food covered opens a hunt: the deer is priced at its
// leather, one candidate whatever else it yields, and a designated deer's
// leather counts toward the need.
func TestClothingLeatherShortOpensHunt(t *testing.T) {
	t.Parallel()
	materials := ClothingMaterials(clothingInput(5, clothingGarments(40, 30)))
	targets := ClothingResourceNeeds(materials, domain.Known([]Amount{}))
	serves := ClothingServes(materials, targets)
	deer := AcquisitionSource{ID: "deer1", Resource: "Corpse_Deer", Hunt: true, Food: true, Yield: 1, NutritionYield: 80, Products: []SourceProduct{{Def: "Leather_Light", Amount: 40}}}
	var resource Resource
	for r := range targets {
		if serves[r] == r && materialIn(materials, "Leathery", r) {
			resource = r
		}
	}
	if resource == "" {
		t.Fatalf("leather not targeted: %v %v", targets, serves)
	}
	priced, ok := ResourceSourceFor(deer, resource, serves)
	if !ok || priced.Yield != 40 {
		t.Fatalf("deer priced %+v %v", priced, ok)
	}
	candidates := AcquisitionSourceCandidates(resource, []AcquisitionSource{priced}, domain.Cell{}, domain.Known(targets[resource]))
	plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: resource, Deficit: targets[resource], Candidates: candidates, HorizonDays: ClothingHorizonDays}}, domain.Known(100000.0))
	if err != nil {
		t.Fatal(err)
	}
	if got := plan.OpenedIDs(resource, CandidateHunt); !got["deer1"] {
		t.Fatal("hunt not opened for leather", plan.Plan.Explain())
	}
	if _, ok := ResourceSourceFor(deer, "WoodLog", serves); ok {
		t.Fatal("a deer serves wood")
	}
}

func materialIn(ms []ClothingMaterial, category string, r Resource) bool {
	for _, m := range ms {
		if m.Category == category {
			for _, member := range m.Members {
				if member == r {
					return true
				}
			}
		}
	}
	return false
}

// A cotton field is a harvest candidate with a lead, an upfront sowing cost
// and a stock cap: it serves a deficit wanted within the horizon and not one
// wanted now.
func TestClothingCottonFieldNeedsHorizon(t *testing.T) {
	t.Parallel()
	field, ok := FieldHarvestCandidate("Cloth", "field", FieldHarvest{Cells: 20, GrowDays: 8, UnitsPerCell: 6, SetupTicks: 1200, WorkPerDay: 600})
	if !ok {
		t.Fatal("no field candidate")
	}
	open := func(horizon float64) int {
		plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: "Cloth", Deficit: 100, HorizonDays: horizon, Candidates: []SupplyCandidate{field}}}, domain.Known(100000.0))
		if err != nil {
			t.Fatal(err)
		}
		return len(plan.Opened("Cloth"))
	}
	if open(0) != 0 || open(ClothingHorizonDays) != 1 {
		t.Fatalf("field opened %d now, %d within the horizon", open(0), open(ClothingHorizonDays))
	}
	if _, ok := FieldHarvestCandidate("Cloth", "x", FieldHarvest{Cells: 0, GrowDays: 8, UnitsPerCell: 6}); ok {
		t.Fatal("an empty field is a candidate")
	}
}
