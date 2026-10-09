package bridge

import (
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// factsReply is a catalog reply with three joy buildings (a television, a
// chess table and a horseshoes pin), a terrain, the stat table that values
// them and the game-computed ThingDef flags.
func factsReply() *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	building := func(name, kind string) *d.ThingDef {
		return &d.ThingDef{DefName: name, DesignationCategory: "Joy", Building: &d.BuildingProperties{JoyKind: kind}}
	}
	television := building("Television", "Television")
	television.Comps = []*d.Opt_CompPropertiesAny{{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: &d.CompProperties_Power{BasePowerConsumption: 100}}}}}
	powerClass, _ := proto.GetExtension((&d.CompProperties_Power{}).ProtoReflect().Descriptor().Options(), d.E_ClrType).(string)
	v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: powerClass})
	steel := building("Steel", "")
	steel.DesignationCategory = ""
	v.ThingDefs = []*d.ThingDef{building("Chess", "Cerebral"), building("Pin", "Dexterity"), steel, television, {DefName: "Wall"}, {DefName: "MeatRaw"}, {DefName: "Meal"}}
	v.TerrainDefs = []*d.TerrainDef{{DefName: "Soil", PathCost: 2, Natural: true}, {DefName: "Lava"}}
	job := func(name string, rate float32, duration int32) *d.JobDef {
		return &d.JobDef{DefName: name, JoyGainRate: rate, JoyDuration: duration}
	}
	v.Defs = &d.DefSets{
		StatDefs: []*d.StatDef{{DefName: "MarketValue"}},
		JobDefs:  []*d.JobDef{job("Play", .5, 4000), job("Throw", 1, 1000), job("Watch", 1, 2000)},
		JoyGiverDefs: []*d.JoyGiverDef{
			{DefName: "PlayChess", ThingDefs: []string{"Chess"}, JobDef: "Play"},
			{DefName: "PlayPin", ThingDefs: []string{"Pin"}, JobDef: "Throw"},
			{DefName: "WatchTelevision", ThingDefs: []string{"Television"}, JobDef: "Watch"},
			{DefName: "Socialize", JobDef: "Missing"},
			// An ingest giver: its class hardcodes the job, so jobDef is empty.
			{DefName: "EatChocolate", ThingDefs: []string{"Meal"}},
		},
	}
	costs := func(units int64) []*o.Quantity {
		return []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(units)}}
	}
	v.StatValues = &o.DefStatTable{
		Stats: []string{"Beauty", "Cleanliness", "Flammability", "MarketValue"},
		Rows: []*o.DefStatRow{
			{DefName: "Chess", Costs: costs(40)}, {DefName: "Pin", Costs: costs(5)}, {DefName: "Television", Costs: costs(80)},
			{DefName: "Steel", Stat: []int32{3}, Value: []float32{2}}, {DefName: "Wall"}, {DefName: "MeatRaw"}, {DefName: "Meal"},
		},
		TerrainRows: []*o.DefStatRow{
			{DefName: "Soil", Stat: []int32{0, 1, 2}, Value: []float32{-3, -1, 0.5}},
			{DefName: "Lava", Stat: []int32{0}, Value: []float32{-1}},
		},
	}
	v.ThingFacts = []*o.ThingDefFacts{
		{DefName: "Chess"}, {DefName: "Pin"}, {DefName: "Steel"}, {DefName: "Television"}, {DefName: "Wall"},
		{DefName: "MeatRaw", FoodKind: o.FoodKind_FOOD_KIND_HUMAN_MEAT.Enum(), RawMeat: true},
		{DefName: "Meal", FoodKind: o.FoodKind_FOOD_KIND_MEAL_FINE.Enum(), MealIngredients: o.MealIngredients_MEAL_INGREDIENTS_NON_MEAT.Enum(), Medicine: true},
	}
	return v
}

// TestCatalogThingFacts: the game-computed food kind, meal
// ingredients, raw-meat and medicine flags decode per def; a def without a
// row and a malformed row are errors.
func TestCatalogThingFacts(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(factsReply(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	want := []policy.Food{{Def: "Meal", Kind: policy.FoodKindMealFine, Ingredients: policy.MealNonMeat}, {Def: "MeatRaw", Kind: policy.FoodKindHumanMeat}}
	if got := catalog.Foods(); len(got) != 2 || got[0] != want[0] || got[1] != want[1] {
		t.Fatal(got)
	}
	if meat, err := catalog.RawMeat("MeatRaw"); err != nil || !meat {
		t.Fatal(meat, err)
	}
	if meat, err := catalog.RawMeat("Meal"); err != nil || meat {
		t.Fatal(meat, err)
	}
	if medicine, err := catalog.Medicine("Meal"); err != nil || !medicine {
		t.Fatal(medicine, err)
	}
	if _, err := catalog.RawMeat("Missing"); err == nil {
		t.Fatal("a def without facts answered")
	}
	for name, mutate := range map[string]func(*o.DefinitionCatalog){
		"unknown def":    func(v *o.DefinitionCatalog) { v.ThingFacts = append(v.ThingFacts, &o.ThingDefFacts{DefName: "Ghost"}) },
		"repeated def":   func(v *o.DefinitionCatalog) { v.ThingFacts = append(v.ThingFacts, &o.ThingDefFacts{DefName: "Wall"}) },
		"meal no inputs": func(v *o.DefinitionCatalog) { v.ThingFacts[6].MealIngredients = nil },
		"ingredients on raw": func(v *o.DefinitionCatalog) {
			v.ThingFacts[5].MealIngredients = o.MealIngredients_MEAL_INGREDIENTS_ANY.Enum()
		},
		"unspecified kind": func(v *o.DefinitionCatalog) { v.ThingFacts[5].FoodKind = o.FoodKind_FOOD_KIND_UNSPECIFIED.Enum() },
	} {
		v := factsReply()
		mutate(v)
		if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
			t.Fatalf("%s accepted", name)
		}
	}
	var none *DefinitionCatalog
	if _, err := none.Medicine("Meal"); err == nil || none.Foods() != nil {
		t.Fatal("a nil catalog decided a fact")
	}
}

// TestCatalogFloorTerrain: a floor's stats come from the terrain stat
// rows and its path cost and natural flag from its def row; a stat the game
// does not show for it, a terrain without rows and a malformed row are errors.
func TestCatalogFloorTerrain(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(factsReply(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if got, err := catalog.FloorTerrain("Soil"); err != nil || !reflect.DeepEqual(got, policy.FloorTerrain{Cleanliness: -1, Beauty: -3, Flammability: .5, PathCost: 2, Natural: true}) {
		t.Fatal(got, err)
	}
	if _, err := catalog.FloorTerrain("Lava"); err == nil || !strings.Contains(err.Error(), "not shown") {
		t.Fatal("an absent stat read as a value", err)
	}
	if _, err := catalog.FloorTerrain("Missing"); err == nil {
		t.Fatal("a terrain without rows answered")
	}
	for name, mutate := range map[string]func(*o.DefStatTable){
		"unknown terrain":    func(v *o.DefStatTable) { v.TerrainRows = append(v.TerrainRows, &o.DefStatRow{DefName: "Ghost"}) },
		"repeated terrain":   func(v *o.DefStatTable) { v.TerrainRows = append(v.TerrainRows, &o.DefStatRow{DefName: "Soil"}) },
		"terrain with stuff": func(v *o.DefStatTable) { v.TerrainRows[0].StuffName = "Steel" },
	} {
		v := factsReply()
		mutate(v.StatValues)
		if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil {
			t.Fatalf("%s accepted", name)
		}
	}
}

// TestCatalogJoyBuildingsRankByJoyThenCost: joy buildings are the
// buildable defs that give a joy kind, ranked by the joy one session gives,
// then by cost, then by name; none is chosen by name.
func TestCatalogJoyBuildingsRankByJoyThenCost(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(factsReply(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	// Chess and the television both give 2000 a session; chess costs 80 and
	// the television 160. The pin gives 1000.
	want := []policy.JoyBuildingMethod{{Definition: "Chess", Kind: "Cerebral"}, {Definition: "Television", Kind: "Television", PowerW: 100}, {Definition: "Pin", Kind: "Dexterity"}}
	got, err := catalog.JoyBuildings()
	if err != nil || len(got) != len(want) {
		t.Fatal(got, err)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatal(got)
		}
	}
	for name, mutate := range map[string]func(*o.DefinitionCatalog){
		"no giver offers it":   func(v *o.DefinitionCatalog) { v.Defs.JoyGiverDefs = v.Defs.JoyGiverDefs[:1] },
		"giver without a job":  func(v *o.DefinitionCatalog) { v.Defs.JoyGiverDefs[0].JobDef = "Missing" },
		"cost without a value": func(v *o.DefinitionCatalog) { v.StatValues.Rows[3].Stat, v.StatValues.Rows[3].Value = nil, nil },
	} {
		v := factsReply()
		mutate(v)
		catalog, err := DecodeDefinitionCatalog(v, pbIdentity())
		if err != nil {
			t.Fatal(name, err)
		}
		if _, err := catalog.JoyBuildings(); err == nil {
			t.Fatalf("%s decided the rule", name)
		}
	}
	var none *DefinitionCatalog
	if methods, err := none.JoyBuildings(); err != nil || methods != nil {
		t.Fatal(methods, err)
	}
}

// TestCatalogRecreationFootholdAndWatchBuildings: the foothold is the
// cheapest joy building drawing no power and needing no research, and the
// watch buildings are those a watch-building giver offers; neither is named.
func TestCatalogRecreationFootholdAndWatchBuildings(t *testing.T) {
	build := func(mutate func(*o.DefinitionCatalog)) *DefinitionCatalog {
		v := factsReply()
		v.ClassChains = append(v.ClassChains, &o.ClassChain{Name: "RimWorld.JoyGiver_WatchBuilding", Bases: []string{"RimWorld.JoyGiver"}}, &o.ClassChain{Name: "RimWorld.JoyGiver_Other", Bases: []string{"RimWorld.JoyGiver"}})
		for _, giver := range v.Defs.JoyGiverDefs {
			switch giver.DefName {
			case "PlayPin", "WatchTelevision":
				giver.GiverClass = "RimWorld.JoyGiver_WatchBuilding"
			case "PlayChess":
				giver.GiverClass = "RimWorld.JoyGiver_Other"
			}
		}
		if mutate != nil {
			mutate(v)
		}
		catalog, err := DecodeDefinitionCatalog(v, pbIdentity())
		if err != nil {
			t.Fatal(err)
		}
		return catalog
	}
	catalog := build(nil)
	if got, err := catalog.RecreationFoothold(); err != nil || got != "Pin" {
		t.Fatal(got, err)
	}
	if got, err := catalog.WatchBuildings(); err != nil || !slices.Equal(got, []string{"Pin", "Television"}) {
		t.Fatal(got, err)
	}
	researched := func(names ...string) func(*o.DefinitionCatalog) {
		return func(v *o.DefinitionCatalog) {
			for _, row := range v.ThingDefs {
				if slices.Contains(names, row.DefName) {
					row.ResearchPrerequisites = []string{"Research"}
				}
			}
		}
	}
	if got, err := build(researched("Pin")).RecreationFoothold(); err != nil || got != "Chess" {
		t.Fatal("research did not rule the pin out", got, err)
	}
	if _, err := build(researched("Pin", "Chess")).RecreationFoothold(); err == nil {
		t.Fatal("a catalog with no foothold answered")
	}
	unknownClass := build(func(v *o.DefinitionCatalog) { v.Defs.JoyGiverDefs[0].GiverClass = "Mod.JoyGiver_Unknown" })
	if _, err := unknownClass.WatchBuildings(); err == nil {
		t.Fatal("a giver class without a chain answered")
	}
}

// TestCatalogStatRowForScenarioForcedStuff: a scenario can start the colony
// with a stuff the game would not offer for the def (the classic scenario's
// jade knife). Native sends a row for the pair so its stats read, and the
// pair is not one of the def's allowed stuffs.
func TestCatalogStatRowForScenarioForcedStuff(t *testing.T) {
	v := factsReply()
	byName := map[string]*d.ThingDef{}
	for _, row := range v.ThingDefs {
		byName[row.DefName] = row
	}
	byName["Steel"].StuffProps = &d.StuffProperties{Categories: []string{"Metallic"}}
	jade := &d.ThingDef{DefName: "Jade", StuffProps: &d.StuffProperties{Categories: []string{"Stony"}}}
	knife := &d.ThingDef{DefName: "Knife", StuffCategories: []string{"Metallic"}}
	v.ThingDefs = append(v.ThingDefs, jade, knife)
	v.ThingFacts = append(v.ThingFacts, &o.ThingDefFacts{DefName: "Jade"}, &o.ThingDefFacts{DefName: "Knife"})
	v.StatValues.Rows = append(v.StatValues.Rows,
		&o.DefStatRow{DefName: "Jade"},
		&o.DefStatRow{DefName: "Knife", StuffName: "Steel", Stat: []int32{3}, Value: []float32{20}},
		&o.DefStatRow{DefName: "Knife", StuffName: "Jade", Stat: []int32{3}, Value: []float32{30}})
	catalog, err := DecodeDefinitionCatalog(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if got, err := catalog.StatValue("Knife", "Jade", StatMarketValue); err != nil || got != 30 {
		t.Fatal(got, err)
	}
	if got, err := catalog.AllowedStuffs("Knife"); err != nil || !slices.Equal(got, []string{"Steel"}) {
		t.Fatal(got, err)
	}
}
