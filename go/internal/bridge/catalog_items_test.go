package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

func comps(values ...any) []*d.Opt_CompPropertiesAny {
	var wrapped []*d.Opt_CompPropertiesAny
	for _, any := range compsAny(values...) {
		wrapped = append(wrapped, &d.Opt_CompPropertiesAny{Value: any})
	}
	return wrapped
}

func compsAny(values ...any) []*d.CompPropertiesAny {
	var out []*d.CompPropertiesAny
	for _, v := range values {
		switch v := v.(type) {
		case *d.CompProperties_Rottable:
			out = append(out, &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Rottable{CompProperties_Rottable: v}})
		case *d.CompProperties_Drug:
			out = append(out, &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Drug{CompProperties_Drug: v}})
		case *d.CompProperties_Power:
			out = append(out, &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Power{CompProperties_Power: v}})
		case *d.CompProperties_Battery:
			out = append(out, &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Battery{CompProperties_Battery: v}})
		case *d.CompProperties_Spawner:
			out = append(out, &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Spawner{CompProperties_Spawner: v}})
		case *d.CompProperties_Book:
			out = append(out, &d.CompPropertiesAny{Value: &d.CompPropertiesAny_CompProperties_Book{CompProperties_Book: v}})
		}
	}
	return out
}

func itemCatalog() *DefinitionCatalog {
	skill := &d.ReadingOutcomePropertiesAny{Value: &d.ReadingOutcomePropertiesAny_BookOutcomeProperties_GainSkillExp{}}
	research := &d.ReadingOutcomePropertiesAny{Value: &d.ReadingOutcomePropertiesAny_BookOutcomeProperties_GainResearch{}}
	mental := &d.ReadingOutcomePropertiesAny{Value: &d.ReadingOutcomePropertiesAny_BookOutcomeProperties_MentalBreak{}}
	return &DefinitionCatalog{
		ThingDefs: map[string]*d.ThingDef{
			"Novel":    {DefName: "Novel", Comps: comps(&d.CompProperties_Book{})},
			"Textbook": {DefName: "Textbook", Comps: comps(&d.CompProperties_Book{Doers: doers(skill)})},
			"Schema":   {DefName: "Schema", Comps: comps(&d.CompProperties_Book{Doers: doers(research)})},
			"Tome":     {DefName: "Tome", Comps: comps(&d.CompProperties_Book{Doers: doers(skill, mental)})},
			"Rice": {DefName: "Rice", ThingCategories: []string{"Rice"}, Comps: comps(&d.CompProperties_Rottable{DaysToRotStart: 30}),
				Ingestible: &d.IngestibleProperties{FoodType: d.FoodTypeFlags_FOOD_TYPE_FLAGS_VEGETABLE_OR_FRUIT, BabiesCanIngest: true}},
			"Steel":  {DefName: "Steel"},
			"Corpse": {DefName: "Corpse", Ingestible: &d.IngestibleProperties{SourceDef: "Human"}},
			"Human":  {DefName: "Human", Race: &d.RaceProperties{Intelligence: d.Intelligence_INTELLIGENCE_HUMANLIKE}},
			"Lamp":   {DefName: "Lamp", Comps: comps(&d.CompProperties_Power{ShortCircuitInRain: true})},
			"Cell":   {DefName: "Cell", Comps: comps(&d.CompProperties_Battery{})},
			"Plain":  {DefName: "Plain"},
			"Chess":  {DefName: "Chess", Building: &d.BuildingProperties{JoyKind: "Gaming_Cerebral"}},
		},
		Defs: nil,
	}
}

func TestCatalogItemFactsReadDefRows(t *testing.T) {
	catalog := itemCatalog()
	books := catalog.Books()
	want := map[string]policy.BookKind{"Novel": policy.Novel, "Textbook": policy.Textbook, "Schema": policy.Schematic, "Tome": policy.Tome}
	if len(books) != len(want) {
		t.Fatal(books)
	}
	for _, book := range books {
		if want[book.Def] != book.Kind {
			t.Fatal(book)
		}
	}
	if days, perishable, err := catalog.RotDays("Rice"); err != nil || !perishable || days != 30 {
		t.Fatal(days, perishable, err)
	}
	if _, perishable, err := catalog.RotDays("Steel"); err != nil || perishable {
		t.Fatal(perishable, err)
	}
	if baby, err := catalog.BabyEdible("Rice"); err != nil || !baby {
		t.Fatal(baby, err)
	}
	if veg, err := catalog.Vegetable("Rice"); err != nil || !veg {
		t.Fatal(veg, err)
	}
	if human, err := catalog.HumanlikeCorpse("Corpse"); err != nil || !human {
		t.Fatal(human, err)
	}
	if rain, err := catalog.RainVulnerable("Lamp"); err != nil || !rain {
		t.Fatal(rain, err)
	}
	if rain, err := catalog.RainVulnerable("Cell"); err != nil || rain {
		t.Fatal(rain, err)
	}
}

func TestRawFoodClassWalksCategoryParents(t *testing.T) {
	catalog := itemCatalog()
	var category *d.ThingCategoryDef
	name := string(category.ProtoReflect().Descriptor().FullName())
	catalog.Defs = map[protoreflect.FullName]map[string]proto.Message{protoreflect.FullName(name): {
		"Rice":         &d.ThingCategoryDef{DefName: "Rice", Parent: "PlantFoodRaw"},
		"Loop":         &d.ThingCategoryDef{DefName: "Loop", Parent: "Loop"},
		"MeatRaw":      &d.ThingCategoryDef{DefName: "MeatRaw"},
		"PlantFoodRaw": &d.ThingCategoryDef{DefName: "PlantFoodRaw"},
	}}
	if class, err := catalog.RawFoodClass("Rice"); err != nil || class != policy.IngredientVegetable {
		t.Fatal(class, err)
	}
	catalog.ThingDefs["Steel"].ThingCategories = []string{"Loop"}
	if class, err := catalog.RawFoodClass("Steel"); err != nil || class != "" {
		t.Fatal("a category loop outside the raw categories is no raw food", class, err)
	}
	catalog.ThingDefs["Steel"].ThingCategories = []string{"MeatRaw", "Rice"}
	if class, err := catalog.RawFoodClass("Steel"); err != nil || class != policy.IngredientMeat {
		t.Fatal("meat takes the first place", class, err)
	}
}

func TestCatalogItemFactsRefuseMissingRows(t *testing.T) {
	catalog := itemCatalog()
	if _, _, err := catalog.RotDays("Missing"); err == nil {
		t.Fatal("a def without a row was read")
	}
	if _, err := catalog.RainVulnerable("Plain"); err == nil {
		t.Fatal("a def without a power comp has no rain fact")
	}
	if _, err := catalog.RawFoodClass("Rice"); err == nil {
		t.Fatal("a thing category without a row was read")
	}
	var none *DefinitionCatalog
	if _, err := none.Vegetable("Rice"); err == nil || none.Books() != nil {
		t.Fatal("a nil catalog decided a fact")
	}
}

func doers(values ...*d.ReadingOutcomePropertiesAny) []*d.Opt_ReadingOutcomePropertiesAny {
	var out []*d.Opt_ReadingOutcomePropertiesAny
	for _, v := range values {
		out = append(out, &d.Opt_ReadingOutcomePropertiesAny{Value: v})
	}
	return out
}

func TestSpawnForbiddenProductsAreTheForbiddenSpawnerOutputs(t *testing.T) {
	catalog := &DefinitionCatalog{ThingDefs: map[string]*d.ThingDef{
		"Hive":  {DefName: "Hive", Comps: comps(&d.CompProperties_Spawner{ThingToSpawn: "InsectJelly", SpawnForbidden: true})},
		"Chem":  {DefName: "Chem", Comps: comps(&d.CompProperties_Spawner{ThingToSpawn: "Chemfuel"})},
		"Steel": {DefName: "Steel"},
		"Husky": {DefName: "Husky"},
	}}
	got := catalog.SpawnForbiddenProducts()
	if len(got) != 1 || !got["InsectJelly"] {
		t.Fatal(got)
	}
	if (*DefinitionCatalog)(nil).SpawnForbiddenProducts() != nil {
		t.Fatal("a nil catalog has none")
	}
}
