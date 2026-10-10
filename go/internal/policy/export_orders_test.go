package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const exportBenchKind = "TableMachining"

func exportRecipe1(name string, product Resource, work float64, ingredient Amount) GearRecipe {
	return GearRecipe{Definition: name, Products: []Resource{product}, Available: domain.Known(true), AvailableOn: domain.Known(true),
		Ingredients: domain.Known([][]Amount{{ingredient}}), WorkAmount: domain.Known(work),
		RequiredWork: domain.Known([]WorkRequirement{{Work: WorkCrafting, Skill: "Crafting"}}), Kind: LedgerProduction}
}

func exportProfile(id PawnID, level int) PawnProfile {
	return PawnProfile{ID: id, Skills: map[string]ProfileSkill{"Crafting": {Name: "Crafting", Level: level}}, Incapable: map[WorkType]bool{}, WorkSkill: testWorkSkill}
}

func exportBench(bills []GearBill, recipes ...GearRecipe) []GearBench {
	return []GearBench{{ID: "Bench_1", Def: exportBenchKind, Usable: domain.Known(true), Bills: domain.Known(bills), Recipes: domain.Known(recipes)}}
}

// exportFixture is two sale goods at one bench: a jewel from gold (60 for 20
// of gold) and a trinket from wood (40 for 12 of wood), a 500 silver gap and
// a trader that buys both.
func exportFixture(profiles ...PawnProfile) ExportRequest {
	items := CoreItemFacts()
	items.Market["Jewel"], items.Market["WoodTrinket"], items.Market["Steel"] = 60, 40, 1.9
	return ExportRequest{
		Benches: exportBench(nil,
			exportRecipe1("Make_Jewel", "Jewel", 3000, Amount{"Steel", 2}),
			exportRecipe1("Make_Trinket", "WoodTrinket", 3000, Amount{"WoodLog", 10})),
		Profiles: domain.Known(profiles), Items: items, Gap: domain.Known(500.0),
		Supply:  []Stock{{Resource: "Steel", Available: domain.Known[int64](100)}, {Resource: "WoodLog", Available: domain.Known[int64](500)}},
		Buys:    map[Resource]domain.Fact[bool]{"Jewel": domain.Known(true), "WoodTrinket": domain.Known(true)},
		CashCap: domain.Known[int64](10000), Packed: domain.Known[int64](0), Stock: map[Resource]int64{},
		Sources:      map[Resource][]SupplyCandidate{"WoodLog": cheapSource(0.1), "Steel": cheapSource(3000)},
		SurplusKnown: true,
	}
}

// cheapSource is a mine row costing perUnit pawn ticks to get one unit.
func cheapSource(perUnit float64) []SupplyCandidate {
	return []SupplyCandidate{SourceCandidate(CandidateMining, "m", domain.Known(perUnit*100), domain.Known(0.0), true, 75, SourceYield(ResourceKey{}, 100, 0, domain.Unknown[int64]()))}
}

// The top-ranked good follows what the ingredients cost to get by hand: the
// jewel on a gold mountain, the trinket where the gold is dear and wood grows.
func TestExportRankingFollowsAcquisitionCost(t *testing.T) {
	forest := exportFixture(exportProfile("w1", 10))
	forest.Sources = map[Resource][]SupplyCandidate{"WoodLog": cheapSource(0.1), "Steel": cheapSource(3000)}
	mountain := exportFixture(exportProfile("w1", 10))
	mountain.Sources = map[Resource][]SupplyCandidate{"Steel": cheapSource(0.1)}
	if got := RankExports(forest); len(got) != 2 || got[0].Product != "WoodTrinket" {
		t.Fatalf("forest: %+v", got)
	}
	if got := RankExports(mountain); len(got) != 2 || got[0].Product != "Jewel" {
		t.Fatalf("mountain: %+v", got)
	}
}

// An open gap yields a finite batch pinned to the worker, sized by the gap at
// the expected sale price; a closed gap declares nothing.
func TestExportDeclaresPinnedBatchesForAnOpenGap(t *testing.T) {
	request := exportFixture(exportProfile("w1", 10))
	got := DeclareExportOrders(request).Declared
	if got.Abstain || len(got.Orders) != 1 {
		t.Fatalf("declared = %+v", got)
	}
	o := got.Orders[0]
	if o.Recipe != "Make_Trinket" || o.Worker != "w1" || o.Mode != domain.GearBatch || o.Target != 25 || o.BenchKind != exportBenchKind {
		t.Fatalf("order = %+v", o)
	}
	request.Gap = domain.Known(0.0)
	if got := DeclareExportOrders(request).Declared; got.Abstain || len(got.Orders) != 0 {
		t.Fatalf("closed gap = %+v", got)
	}
}

// Quantity is also bounded by the trader's cash.
func TestExportQuantityIsBoundedByTraderCash(t *testing.T) {
	request := exportFixture(exportProfile("w1", 10))
	request.CashCap = domain.Known[int64](100)
	for _, o := range DeclareExportOrders(request).Declared.Orders {
		if o.Product == "WoodTrinket" && o.Target != 5 {
			t.Fatalf("order = %+v", o)
		}
	}
}

// A good no reachable trader buys is never ordered; an unread buyer abstains.
func TestExportNeverOrdersAGoodTheTraderDoesNotBuy(t *testing.T) {
	request := exportFixture(exportProfile("w1", 10))
	request.Buys["Jewel"], request.Buys["WoodTrinket"] = domain.Known(false), domain.Known(false)
	if got := DeclareExportOrders(request).Declared; got.Abstain || len(got.Orders) != 0 {
		t.Fatalf("unbought = %+v", got)
	}
	request.Buys["Jewel"] = domain.Unknown[bool]()
	if got := DeclareExportOrders(request).Declared; !got.Abstain {
		t.Fatalf("unread buyer = %+v", got)
	}
}

// An ingredient below its usable stock drops the candidate.
func TestExportDropsACandidateWithUnusableIngredients(t *testing.T) {
	request := exportFixture(exportProfile("w1", 10))
	request.Supply[1].Available = domain.Known[int64](5)
	got := DeclareExportOrders(request).Declared
	if len(got.Orders) != 1 || got.Orders[0].Product != "Jewel" {
		t.Fatalf("declared = %+v", got)
	}
	// The colony's own use of the stock is protected as well.
	request = exportFixture(exportProfile("w1", 10))
	request.Runways = []ResourceRunway{{Resource: "WoodLog", Reserve: 495, Stock: domain.Known[int64](500), ConsumptionPerDay: domain.Known(0.0)}}
	if got := DeclareExportOrders(request).Declared; len(got.Orders) != 1 || got.Orders[0].Product != "Jewel" {
		t.Fatalf("reserved = %+v", got)
	}
}

// The runway guard stops an order whose own draw breaches the 15-day line,
// counting the orders already declared this Round (placed or not).
func TestExportRunwayGuardCountsDeclaredDraws(t *testing.T) {
	request := exportFixture(exportProfile("w1", 10), exportProfile("w2", 10))
	request.Supply[0].Available = domain.Known[int64](0)
	request.Supply[1].Available = domain.Known[int64](100)
	// 4 a day over 15 days holds 60, leaving 40 wood: four trinkets.
	request.Runways = []ResourceRunway{{Resource: "WoodLog", Stock: domain.Known[int64](100), ConsumptionPerDay: domain.Known(4.0)}}
	got := DeclareExportOrders(request).Declared
	if len(got.Orders) != 1 || got.Orders[0].Target != 4 {
		t.Fatalf("declared = %+v", got)
	}
	// An unread use rate of a forecast ingredient is unknown.
	request.Runways = []ResourceRunway{{Resource: "WoodLog", Stock: domain.Known[int64](100)}}
	if got := DeclareExportOrders(request).Declared; !got.Abstain {
		t.Fatalf("unread rate = %+v", got)
	}
}

// MaintainTrade never declares a good with a floor, a need, food, a component
// or stone blocks.
func TestExportNeverDeclaresAFloorProduct(t *testing.T) {
	request := exportFixture(exportProfile("w1", 10))
	request.Items.Market["Cloth"], request.Items.Market["ComponentIndustrial"] = 1.3, 32
	request.Benches = exportBench(nil,
		exportRecipe1("Make_Steel", "Steel", 100, Amount{"WoodLog", 1}),
		exportRecipe1("Make_Blocks", "BlocksGranite", 100, Amount{"WoodLog", 1}),
		exportRecipe1("Make_Component", ComponentResource, 100, Amount{"WoodLog", 1}),
		exportRecipe1("Make_Needed", "Jewel", 100, Amount{"Steel", 1}),
		exportRecipe1("Make_Rice", "RawRice", 100, Amount{"WoodLog", 1}))
	request.Buys["Steel"], request.Buys["BlocksGranite"], request.Buys[ComponentResource], request.Buys["RawRice"] = domain.Known(true), domain.Known(true), domain.Known(true), domain.Known(true)
	request.Demand.Needs = map[Resource]int64{"Steel": 100, "Jewel": 5}
	plan := DeclareExportOrders(request)
	if len(plan.Declared.Orders) != 0 || len(plan.Products) != 0 {
		t.Fatalf("declared = %+v products = %v", plan.Declared, plan.Products)
	}
	request.Demand.Needs = nil
	request.Floors = map[string]int64{"Jewel": 3}
	if plan := DeclareExportOrders(request); len(plan.Products) != 0 && plan.Products[0] == "Jewel" {
		t.Fatalf("floored product ranked: %v", plan.Products)
	}
}

// Anything unread abstains: the gap, the workers, the cash.
func TestExportAbstainsOnUnreadInputs(t *testing.T) {
	for name, mutate := range map[string]func(*ExportRequest){
		"gap":      func(r *ExportRequest) { r.Gap = domain.Unknown[float64]() },
		"profiles": func(r *ExportRequest) { r.Profiles = domain.Unknown[[]PawnProfile]() },
		"cash":     func(r *ExportRequest) { r.CashCap = domain.Unknown[int64]() },
		"bench": func(r *ExportRequest) {
			r.Benches[0].Recipes = domain.Unknown[[]GearRecipe]()
		},
	} {
		request := exportFixture(exportProfile("w1", 10))
		mutate(&request)
		if got := DeclareExportOrders(request).Declared; !got.Abstain || len(got.Orders) != 0 {
			t.Errorf("%s: declared = %+v", name, got)
		}
	}
}

// Goods held and on standing bills reduce the gap; a standing batch stays
// declared as it stands, and its worker is busy.
func TestExportInFlightGoodsReduceTheGap(t *testing.T) {
	// 20 trinkets held at 20 silver each cover 400 of the 500.
	request := exportFixture(exportProfile("w1", 10))
	request.Stock = map[Resource]int64{"WoodTrinket": 20}
	got := DeclareExportOrders(request).Declared
	if len(got.Orders) != 1 || got.Orders[0].Target*20 < 100 || got.Orders[0].Target*20 >= 140 {
		t.Fatalf("held = %+v", got)
	}
	// A standing batch of 10 covers 200 more and keeps its worker.
	spec := OrderSpec{Recipe: "Make_Trinket", Ingredients: []string{"WoodLog"}, Worker: "w1", Mode: domain.GearBatch, Target: 10, BenchKind: exportBenchKind, Product: "WoodTrinket", Class: ResourceMaterial}
	bill := GearBill{ID: "Bill_1", Recipe: "Make_Trinket", Active: domain.Known(true), Worker: domain.Known("w1"), Products: []Resource{"WoodTrinket"}, Spec: domain.Known(spec)}
	request = exportFixture(exportProfile("w1", 10), exportProfile("w2", 10))
	recipes, _ := request.Benches[0].Recipes.Value()
	request.Benches = exportBench([]GearBill{bill}, recipes...)
	got = DeclareExportOrders(request).Declared
	if len(got.Orders) != 2 || got.Orders[0].Key() != spec.Key() || got.Orders[1].Worker != "w2" || got.Orders[1].Target != 15 {
		t.Fatalf("standing = %+v", got)
	}
}

// The held stock of the export products joins the sale surplus, retaining
// none of it, only while the colony is short of silver.
func TestExportSaleSurplus(t *testing.T) {
	items := CoreItemFacts()
	need := domain.Known(TradeNeed{MedicineReplenish: 10})
	resources := domain.Known([]Amount{{Resource: "Silver", Count: 10}, {Resource: "WoodTrinket", Count: 7}})
	got, _ := ExportSaleSurplus(items, need, resources, domain.Known[int64](3), []Resource{"WoodTrinket"}).Value()
	if len(got.Surplus) != 1 || got.Surplus[0].Resource != "WoodTrinket" || got.Surplus[0].Count != 7 {
		t.Fatalf("short: %+v", got)
	}
	rich := domain.Known([]Amount{{Resource: "Silver", Count: 5000}, {Resource: "WoodTrinket", Count: 7}})
	if got, _ := ExportSaleSurplus(items, need, rich, domain.Known[int64](3), []Resource{"WoodTrinket"}).Value(); len(got.Surplus) != 0 {
		t.Fatalf("covered: %+v", got)
	}
}

func TestSaleGearSurplusSellsTheCheapestFirstUntilTheGapIsCovered(t *testing.T) {
	row := func(id, def string, price float64) TradeSheetRowFact {
		return TradeSheetRowFact{ThingID: id, DefName: def, SellPrice: price, SellPriceKnown: true}
	}
	rows := []TradeSheetRowFact{row("a", "Gun_Revolver", 90), row("b", "Gun_Revolver", 40), row("c", "Gun_Revolver", 60), row("d", "MeleeWeapon_Knife", 10)}
	got := SaleGearSurplus(rows, nil, GearSurplus{Weapons: map[Resource]int{"Gun_Revolver": 2}}, 80)
	if len(got) != 2 || !got["b"] || !got["c"] {
		t.Fatalf("sold = %v", got)
	}
	if got := SaleGearSurplus(rows, nil, GearSurplus{Weapons: map[Resource]int{"Gun_Revolver": 2}}, 0); len(got) != 0 {
		t.Fatalf("closed gap sold = %v", got)
	}
	if got := SaleGearSurplus(rows, nil, GearSurplus{}, 80); len(got) != 0 {
		t.Fatalf("no surplus sold = %v", got)
	}
}

func TestApparelSurplusIsUnknownWithoutTheCensus(t *testing.T) {
	if _, known := ApparelSurplus(ClothingDemandInput{Gear: domain.Unknown[GearObservation]()}); known {
		t.Fatal("an unread census is a known surplus")
	}
}
