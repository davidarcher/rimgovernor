package bridge

import (
	"slices"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func itemTestCatalog() *o.DefinitionCatalog {
	v := catalogReply(authorityTestContext(7)).GetObserved()
	v.ThingDefs = []*d.ThingDef{
		{DefName: "Steel", ThingCategories: []string{"ResourcesRaw"}, StuffProps: &d.StuffProperties{Categories: []string{"Metallic"}}},
		{DefName: "Gold", StuffProps: &d.StuffProperties{Categories: []string{"Metallic"}, StatFactors: []*d.Opt_StatModifier{{Value: &d.StatModifier{Stat: "Beauty", Value: 4}}}}},
		{DefName: "MedicineHerbal", ThingCategories: []string{"Medicine"}, StatBases: []*d.Opt_StatModifier{{Value: &d.StatModifier{Stat: "MedicalPotency", Value: 0.6}}}},
		{DefName: "RawRice", ThingCategories: []string{"PlantFoodRaw"}},
		{DefName: "Bed", StuffCategories: []string{"Metallic", "Woody"}},
		{DefName: "Silver"},
		drugRow("Beer", "Alcohol", d.DrugCategory_DRUG_CATEGORY_SOCIAL, 10, false),
		drugRow("GoJuice", "GoJuice", d.DrugCategory_DRUG_CATEGORY_HARD, 50, true),
		drugRow("Yayo", "Psychite", d.DrugCategory_DRUG_CATEGORY_HARD, 40, true),
		drugRow("PsychiteTea", "Psychite", d.DrugCategory_DRUG_CATEGORY_SOCIAL, 30, false),
		drugRow("Luciferium", "Luciferium", d.DrugCategory_DRUG_CATEGORY_HARD, 60, false),
		{DefName: "Penoxycyline", Comps: comps(&d.CompProperties_Drug{}), Ingestible: &d.IngestibleProperties{OutcomeDoers: []*d.Opt_IngestionOutcomeDoerAny{{Value: &d.IngestionOutcomeDoerAny{Value: &d.IngestionOutcomeDoerAny_IngestionOutcomeDoer_GiveHediff{IngestionOutcomeDoer_GiveHediff: &d.IngestionOutcomeDoer_GiveHediff{HediffDef: "PenoxycylineHigh", Severity: 1}}}}}}},
	}
	v.Defs = &d.DefSets{
		StatDefs: v.Defs.GetStatDefs(),
		ThingCategoryDefs: []*d.ThingCategoryDef{
			{DefName: "Foods"}, {DefName: "PlantFoodRaw", Parent: "Foods"}, {DefName: "ResourcesRaw"}, {DefName: "Medicine"},
		},
		ChemicalDefs: []*d.ChemicalDef{
			{DefName: "Alcohol", AddictionHediff: "AlcoholAddiction"}, {DefName: "GoJuice", AddictionHediff: "GoJuiceAddiction"},
			{DefName: "Psychite", AddictionHediff: "PsychiteAddiction"}, {DefName: "Luciferium", AddictionHediff: "LuciferiumAddiction"},
		},
		HediffDefs: []*d.HediffDef{
			{DefName: "AlcoholAddiction", Comps: fadingComp(-0.01)}, {DefName: "GoJuiceAddiction", Comps: fadingComp(-0.02)},
			{DefName: "PsychiteAddiction", Comps: fadingComp(-0.01)}, {DefName: "LuciferiumAddiction"},
			{DefName: "PenoxycylineHigh", Stages: []*d.Opt_HediffStage{{Value: &d.HediffStage{MakeImmuneTo: []string{"Plague", "Malaria"}}}},
				Comps: fadingComp(-0.25)},
		},
	}
	v.StatValues = &o.DefStatTable{
		Stats: []string{"MarketValue", "Nutrition"},
		Rows: []*o.DefStatRow{
			{DefName: "Steel", Stat: []int32{0}, Value: []float32{1.9}},
			{DefName: "Gold", Stat: []int32{0}, Value: []float32{10}},
			{DefName: "MedicineHerbal", Stat: []int32{0}, Value: []float32{18}},
			{DefName: "RawRice", Stat: []int32{0, 1}, Value: []float32{1.1, 0.05}},
		},
	}
	return v
}

func drugRow(name, chemical string, category d.DrugCategory, order float32, combat bool) *d.ThingDef {
	return &d.ThingDef{DefName: name, Ingestible: &d.IngestibleProperties{DrugCategory: category},
		Comps: []*d.Opt_CompPropertiesAny{{Value: &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Drug{CompProperties_Drug: &d.CompProperties_Drug{Chemical: chemical, ListOrder: order, IsCombatEnhancingDrug: combat}}}}}}
}

func fadingComp(perDay float32) []*d.Opt_HediffCompPropertiesAny {
	return []*d.Opt_HediffCompPropertiesAny{{Value: &d.HediffCompPropertiesAny{Value: &d.HediffCompPropertiesAny_HediffCompProperties_SeverityPerDay{HediffCompProperties_SeverityPerDay: &d.HediffCompProperties_SeverityPerDay{SeverityPerDay: perDay}}}}}
}

// TestDefinitionCatalogDrugFacts (#1734): the drugs, their preference order,
// which chemicals' addictions fade, the preventive drug and the currency are
// the def rows', with no name in Go.
func TestDefinitionCatalogDrugFacts(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(itemTestCatalog(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	items, err := catalog.ItemFacts()
	if err != nil {
		t.Fatal(err)
	}
	names := func(drugs []policy.Drug) (out []string) {
		for _, drug := range drugs {
			out = append(out, string(drug.Def))
		}
		return out
	}
	if got := names(items.Drugs); !slices.Equal(got, []string{"Beer", "PsychiteTea", "Yayo", "GoJuice", "Luciferium"}) {
		t.Fatalf("drugs, social first then by list order: %v", got)
	}
	if got := names(items.RecreationDrugs()); !slices.Equal(got, []string{"Beer", "PsychiteTea"}) {
		t.Fatalf("recreation %v", got)
	}
	if got := names(items.CombatDrugs()); !slices.Equal(got, []string{"Yayo", "GoJuice"}) {
		t.Fatalf("combat %v", got)
	}
	if drug, ok := items.DependencyDrug("Psychite"); !ok || drug != "PsychiteTea" {
		t.Fatalf("dependency drug %v %v", drug, ok)
	}
	if !items.Chemicals["Alcohol"].Weanable || items.Chemicals["Luciferium"].Weanable {
		t.Fatalf("addictions that fade: %+v", items.Chemicals)
	}
	if p := items.Prevention; p == nil || p.Drug != "Penoxycyline" || p.Days != 4 || !slices.Equal(p.Diseases, []string{"Malaria", "Plague"}) {
		t.Fatalf("prevention %+v", p)
	}
	if items.Currency != "Silver" {
		t.Fatalf("currency %q", items.Currency)
	}
	if got := items.Categories["RawRice"]; !slices.Equal(got, []string{"Foods", "PlantFoodRaw"}) {
		t.Fatalf("categories carry their parents: %v", got)
	}

	missing := itemTestCatalog()
	missing.Defs.ChemicalDefs = missing.Defs.ChemicalDefs[1:]
	catalog, err = DecodeDefinitionCatalog(missing, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := catalog.ItemFacts(); err == nil || !strings.Contains(err.Error(), "Alcohol") {
		t.Fatalf("a drug whose chemical has no row built facts: %v", err)
	}
}

// TestDefinitionCatalogItemFacts (#1734): the item facts are the catalog's
// own market values, nutrition, potencies, categories and stuff factors; a
// stuff the catalog could not price is refused, and a catalog without a stat
// table gives facts every lookup on which fails.
func TestDefinitionCatalogItemFacts(t *testing.T) {
	catalog, err := DecodeDefinitionCatalog(itemTestCatalog(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	items, err := catalog.ItemFacts()
	if err != nil {
		t.Fatal(err)
	}
	if v, err := items.MarketValue("Steel"); err != nil || v != float64(float32(1.9)) {
		t.Fatalf("steel %v %v", v, err)
	}
	if score, err := items.StuffScore("Gold"); err != nil || score != 40 {
		t.Fatalf("gold stuff score %v %v (beauty factor 4 x market 10)", score, err)
	}
	if score, err := items.StuffScore("Steel"); err != nil || score != float64(float32(1.9)) {
		t.Fatalf("a stuff with no beauty factor scores by market value alone: %v %v", score, err)
	}
	if herbal, err := items.MedicineAt(0); err != nil || herbal != "MedicineHerbal" || !items.IsMedicine("MedicineHerbal") || items.IsMedicine("Steel") {
		t.Fatalf("medicine %v %v", herbal, err)
	}
	if got := items.StuffsFor("Bed"); len(got) != 2 || got[0] != "Gold" || got[1] != "Steel" {
		t.Fatalf("bed stuffs %v", got)
	}
	if again, _ := catalog.ItemFacts(); len(again.Market) != len(items.Market) {
		t.Fatal("the load's item facts are built once")
	}

	bare, err := DecodeDefinitionCatalog(catalogReply(authorityTestContext(7)).GetObserved(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if none, err := bare.ItemFacts(); err != nil || none.IsMedicine("MedicineHerbal") {
		t.Fatalf("bare catalog %v", err)
	} else if _, err := none.MarketValue("Steel"); err == nil {
		t.Fatal("a catalog without a stat table priced steel")
	}

	unpriced := itemTestCatalog()
	unpriced.StatValues.Rows = unpriced.StatValues.Rows[1:]
	catalog, err = DecodeDefinitionCatalog(unpriced, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := catalog.ItemFacts(); err == nil || !strings.Contains(err.Error(), "Steel") {
		t.Fatalf("an unpriced stuff built facts: %v", err)
	}
}

// TestDefinitionCatalogRefusesADifferentCalendar (#1734): Go states the
// calendar once; a game whose tick constants differ is not planned against.
func TestDefinitionCatalogRefusesADifferentCalendar(t *testing.T) {
	for _, change := range []func(*o.CatalogConstants){
		func(c *o.CatalogConstants) { c.TicksPerHour = 1000 },
		func(c *o.CatalogConstants) { c.TicksPerDay = 24000 },
		func(c *o.CatalogConstants) { c.DaysPerYear = 15 },
	} {
		v := catalogReply(authorityTestContext(7)).GetObserved()
		change(v.Constants)
		if _, err := DecodeDefinitionCatalog(v, pbIdentity()); err == nil || !strings.Contains(err.Error(), "calendar") {
			t.Fatalf("a different calendar decoded: %v", err)
		}
	}
	_ = proto.Int32
}

// ApparelIsArmor on the outfit tags the game's own XML gives real defs (Core,
// Royalty, Biotech 1.6 Data/*/Defs; see the rule's comment).
func TestApparelIsArmorOnRealOutfitTags(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		def   string
		tags  []string
		armor bool
	}{
		{"Apparel_FlakVest", []string{"Soldier"}, true},
		{"Apparel_PlateArmor", []string{"Soldier"}, true},
		{"Apparel_SimpleHelmet", []string{"Soldier"}, true},
		{"Apparel_PowerArmor", []string{"Soldier", "Spacefarer"}, true},
		{"Apparel_ArmorCataphract", []string{"Soldier", "Spacefarer"}, true},
		{"Apparel_PsyfocusVest", []string{"Soldier"}, true},
		{"Apparel_KidHelmet", []string{"Soldier"}, true},
		{"Apparel_Parka", []string{"Worker", "Soldier"}, false},
		{"Apparel_Tuque", []string{"Worker", "Soldier"}, false},
		{"Apparel_Pants", []string{"Worker", "Soldier", "Spacefarer"}, false},
		{"Apparel_TribalA", []string{"Worker", "Soldier", "Spacefarer"}, false},
		{"Apparel_Duster", []string{"Worker"}, false},
		{"Apparel_Vacsuit", []string{"Spacefarer"}, false},
		{"Apparel_IntegratorHeadset", nil, false},
		{"Apparel_ShieldBelt", nil, false},
	} {
		if got := ApparelIsArmor(&d.ApparelProperties{DefaultOutfitTags: tc.tags}); got != tc.armor {
			t.Errorf("%s %v: armor %v, want %v", tc.def, tc.tags, got, tc.armor)
		}
	}
}

// The armory-versus-wardrobe split is the catalog's: an apparel def only the
// Soldier outfit tag names (not Worker too) is armor, sorted by name.
func TestDefinitionCatalogItemFactsArmorSplit(t *testing.T) {
	v := itemTestCatalog()
	apparel := func(name string, tags ...string) *d.ThingDef {
		return &d.ThingDef{DefName: name, Apparel: &d.ApparelProperties{DefaultOutfitTags: tags}}
	}
	v.ThingDefs = append(v.ThingDefs,
		apparel("Apparel_PlateArmor", "Soldier"), apparel("Apparel_FlakVest", "Soldier"),
		apparel("Apparel_Parka", "Soldier", "Worker"), apparel("Apparel_TShirt", "Worker"), apparel("Apparel_Robe"))
	catalog, err := DecodeDefinitionCatalog(v, pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	items, err := catalog.ItemFacts()
	if err != nil {
		t.Fatal(err)
	}
	if got := items.Armor; len(got) != 2 || got[0] != "Apparel_FlakVest" || got[1] != "Apparel_PlateArmor" {
		t.Fatalf("armor %v", got)
	}
}
