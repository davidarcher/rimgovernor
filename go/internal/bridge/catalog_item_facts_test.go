package bridge

import (
	"strings"
	"testing"

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
