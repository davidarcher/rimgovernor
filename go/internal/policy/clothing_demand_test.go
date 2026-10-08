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

// wornShirt is a worn shirt of stuff with the given condition: 100 hit
// points at 0.4 a day, so it crosses the tattered threshold (.5) in
// (condition - .5) * 250 days.
func wornShirt(stuff Resource, condition float64) GearOption {
	return GearOption{ID: "shirt", Definition: "Apparel_BasicShirt", Stuff: stuff, Condition: condition, MaxHitPoints: 100, WearPerDay: .4, Groups: []string{"Torso"}}
}

func clothingInput(worn []GearOption, stored []GearStock, garments []ClothingGarment, stock ...Amount) ClothingDemandInput {
	var pawns []GearPawn
	for i, o := range worn {
		pawns = append(pawns, GearPawn{Pawn: PawnID(string(rune('a' + i))), LoadoutModel: domain.Known(GearLoadoutInput{Worn: []GearOption{o}})})
	}
	return ClothingDemandInput{Garments: garments, Categories: clothingStuffs, Gear: domain.Known(GearObservation{Pawns: pawns, Stored: domain.Known(stored)}), Stock: StockReader{Resources: domain.Known(stock)}}
}

// A garment projected to cross the tattered threshold within the horizon
// raises its replacement demand ahead of need, at the recipe's own count; one
// far from the threshold, or one that does not wear, does not.
func TestClothingRunwayAsksForGarmentsAboutToWearOut(t *testing.T) {
	t.Parallel()
	garments := clothingGarments(40, 30)
	// 15 days at .4 a day is 6 hit points: condition .55 crosses in 12.5 days.
	near := clothingInput([]GearOption{wornShirt("Leather_Plain", .55)}, nil, garments)
	if got := PlanClothingRunway(near).Needs; got["Leather_Light"] != 40 || len(got) != 1 {
		t.Fatalf("near the threshold: %v", got)
	}
	far := clothingInput([]GearOption{wornShirt("Leather_Plain", .6)}, nil, garments)
	if got := PlanClothingRunway(far).Needs; len(got) != 0 {
		t.Fatalf("far from the threshold: %v", got)
	}
	still := wornShirt("Leather_Plain", .51)
	still.WearPerDay = 0
	if got := PlanClothingRunway(clothingInput([]GearOption{still}, nil, garments)).Needs; len(got) != 0 {
		t.Fatalf("a garment that does not wear: %v", got)
	}
	tattered := wornShirt("Leather_Plain", .4)
	tattered.MaxHitPoints, tattered.WearPerDay = 0, 0
	if got := PlanClothingRunway(clothingInput([]GearOption{tattered}, nil, garments)).Needs; got["Leather_Light"] != 40 {
		t.Fatalf("a tattered garment with unknown wear: %v", got)
	}
}

// The demand is the stock level to reach: it sums over garments, vanishes
// where the stock holds it, and is netted by serviceable stored garments.
func TestClothingRunwayCountsStockAndSpares(t *testing.T) {
	t.Parallel()
	garments := clothingGarments(40, 30)
	two := []GearOption{wornShirt("Cloth", .5), wornShirt("Cloth", .5)}
	if got := PlanClothingRunway(clothingInput(two, nil, garments)).Needs; got["Cloth"] != 80 {
		t.Fatalf("two garments: %v", got)
	}
	if got := PlanClothingRunway(clothingInput(two, nil, garments, Amount{"Cloth", 80})).Needs; len(got) != 0 {
		t.Fatalf("stock covers: %v", got)
	}
	spare := []GearStock{{Definition: "Apparel_BasicShirt", Quality: 2, HPBand: 10, Count: 1}}
	if got := PlanClothingRunway(clothingInput(two, spare, garments)).Needs; got["Cloth"] != 40 {
		t.Fatalf("one stored spare: %v", got)
	}
	worn := PlanClothingRunway(clothingInput(two, []GearStock{{Definition: "Apparel_BasicShirt", Quality: 2, HPBand: 2, Count: 5}}, garments)).Needs
	if worn["Cloth"] != 80 {
		t.Fatalf("an unserviceable stored garment covers: %v", worn)
	}
	in := clothingInput(two, nil, garments)
	in.Gear = domain.Unknown[GearObservation]()
	if got := PlanClothingRunway(in); len(got.Needs) != 0 {
		t.Fatalf("unread census: %v", got)
	}
}

// The demand names the cheapest member (equal counts: by name) of the loadout stuff's category; the
// stuffs sharing its category serve it, others do not.
func TestClothingRunwayServes(t *testing.T) {
	t.Parallel()
	got := PlanClothingRunway(clothingInput([]GearOption{wornShirt("Leather_Plain", .5)}, nil, clothingGarments(40, 30)))
	if got.Needs["Leather_Light"] != 40 || got.Serves["Leather_Light"] != "Leather_Light" || got.Serves["Leather_Plain"] != "Leather_Light" {
		t.Fatalf("leather: %+v", got)
	}
	if _, serves := got.Serves["Cloth"]; serves {
		t.Fatalf("another category serves: %+v", got.Serves)
	}
}

// A leather shortage with food covered opens a hunt: the deer is priced at its
// leather, one candidate whatever else it yields, and a designated deer's
// leather counts toward the need.
func TestClothingLeatherShortOpensHunt(t *testing.T) {
	t.Parallel()
	runway := PlanClothingRunway(clothingInput([]GearOption{wornShirt("Leather_Plain", .5)}, nil, clothingGarments(40, 30)))
	deer := AcquisitionSource{ID: "deer1", Resource: "Corpse_Deer", Hunt: true, Food: true, Yield: 1, NutritionYield: 80, Products: []SourceProduct{{Def: "Leather_Plain", Amount: 40}}}
	const resource Resource = "Leather_Light"
	priced, ok := ResourceSourceFor(deer, resource, runway.Serves)
	if !ok || priced.Yield != 40 {
		t.Fatalf("deer priced %+v %v", priced, ok)
	}
	candidates := AcquisitionSourceCandidates(resource, []AcquisitionSource{priced}, domain.Cell{}, domain.Known(runway.Needs[resource]))
	plan, err := PlanResourceSupply([]ResourceSupplyInput{{Resource: resource, Deficit: runway.Needs[resource], Candidates: candidates, HorizonDays: ClothingHorizonDays}}, domain.Known(100000.0))
	if err != nil {
		t.Fatal(err)
	}
	if got := plan.OpenedIDs(resource, CandidateHunt); !got["deer1"] {
		t.Fatal("hunt not opened for leather", plan.Plan.Explain())
	}
	if _, ok := ResourceSourceFor(deer, "WoodLog", runway.Serves); ok {
		t.Fatal("a deer serves wood")
	}
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
