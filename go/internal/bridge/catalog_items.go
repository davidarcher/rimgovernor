package bridge

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Item facts the generated def rows state (#1733). A frame carries what
// varies; what a def says about itself is read here, once per load, from its
// ThingDef row. A def without a row is a contract error, never a default.

// StatNutrition is the StatDef whose abstract value is a food's nutrition.
const StatNutrition = "Nutrition"

// thingRow is name's ThingDef row.
func (catalog *DefinitionCatalog) thingRow(name string) (*d.ThingDef, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	row := catalog.ThingDefs[name]
	if row == nil {
		return nil, contract("catalog has no def row for %s", name)
	}
	return row, nil
}

// compOf is the first comp of row that pick returns non-nil for.
func compOf[T any](row *d.ThingDef, pick func(*d.CompPropertiesAny) *T) *T {
	for _, comp := range row.GetComps() {
		if found := pick(comp); found != nil {
			return found
		}
	}
	return nil
}

// Books is every book def (a def carrying CompProperties_Book) with what
// reading it does, by name: skill experience is a textbook, research a
// schematic, Anomaly research or a mental break a tome, otherwise a novel.
func (catalog *DefinitionCatalog) Books() []policy.Book {
	if catalog == nil {
		return nil
	}
	var books []policy.Book
	for name, row := range catalog.ThingDefs {
		book := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Book)
		if book == nil {
			continue
		}
		var tome, textbook, schematic bool
		for _, doer := range book.GetDoers() {
			switch doer.GetValue().(type) {
			case *d.ReadingOutcomePropertiesAny_BookOutcomeProperties_GainAnomalyResearch, *d.ReadingOutcomePropertiesAny_BookOutcomeProperties_MentalBreak:
				tome = true
			case *d.ReadingOutcomePropertiesAny_BookOutcomeProperties_GainSkillExp:
				textbook = true
			case *d.ReadingOutcomePropertiesAny_BookOutcomeProperties_GainResearch:
				schematic = true
			}
		}
		kind := policy.Novel
		switch {
		case tome:
			kind = policy.Tome
		case textbook:
			kind = policy.Textbook
		case schematic:
			kind = policy.Schematic
		}
		books = append(books, policy.Book{Def: name, Kind: kind})
	}
	slices.SortFunc(books, func(a, b policy.Book) int { return strings.Compare(a.Def, b.Def) })
	return books
}

// RotDays is the days a def's items last before they rot (its
// CompProperties_Rottable); perishable is false for a def that never rots.
func (catalog *DefinitionCatalog) RotDays(name string) (days float64, perishable bool, err error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return 0, false, err
	}
	rot := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Rottable)
	if rot == nil {
		return 0, false, nil
	}
	return float64(rot.GetDaysToRotStart()), true, nil
}

// BabyEdible is IngestibleProperties.babiesCanIngest of the def.
func (catalog *DefinitionCatalog) BabyEdible(name string) (bool, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return false, err
	}
	return row.GetIngestible().GetBabiesCanIngest(), nil
}

// Vegetable is whether the def's food type includes vegetable or fruit.
func (catalog *DefinitionCatalog) Vegetable(name string) (bool, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return false, err
	}
	return row.GetIngestible().GetFoodType()&d.FoodTypeFlags_FOOD_TYPE_FLAGS_VEGETABLE_OR_FRUIT != 0, nil
}

// The ThingCategoryDefs a raw food ingredient sits within, in the order the
// ingredient class takes them.
const (
	categoryMeatRaw          = "MeatRaw"
	categoryPlantFoodRaw     = "PlantFoodRaw"
	categoryAnimalProductRaw = "AnimalProductRaw"
)

// RawFoodClass is the raw-ingredient class of a def: meat, vegetable or
// animal product by the first of MeatRaw, PlantFoodRaw and AnimalProductRaw
// that one of its thing categories sits within (the category itself or any
// parent of it); "" for a def in none. Milk has the fluid food type and is
// still an animal product by its category.
func (catalog *DefinitionCatalog) RawFoodClass(name string) (policy.FoodIngredientClass, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return "", err
	}
	within := map[string]bool{}
	for _, category := range row.GetThingCategories() {
		// A category already walked has its parents in within, which also ends a cycle.
		for category != "" && !within[category] {
			within[category] = true
			row := DefRow[*d.ThingCategoryDef](catalog, category)
			if row == nil {
				return "", contract("catalog has no thing category %s (of %s)", category, name)
			}
			category = row.GetParent()
		}
	}
	switch {
	case within[categoryMeatRaw]:
		return policy.IngredientMeat, nil
	case within[categoryPlantFoodRaw]:
		return policy.IngredientVegetable, nil
	case within[categoryAnimalProductRaw]:
		return policy.IngredientAnimalProduct, nil
	}
	return "", nil
}

// HumanlikeCorpse is whether a corpse def is of a humanlike race: the race
// def its ingestible names as its source.
func (catalog *DefinitionCatalog) HumanlikeCorpse(name string) (bool, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return false, err
	}
	source := row.GetIngestible().GetSourceDef()
	if source == "" {
		return false, contract("corpse def %s names no source race", name)
	}
	race, err := catalog.thingRow(source)
	if err != nil {
		return false, err
	}
	return race.GetRace().GetIntelligence() >= d.Intelligence_INTELLIGENCE_HUMANLIKE, nil
}

// RainVulnerable is whether a power building shorts out in rain, from its
// power or battery comp.
func (catalog *DefinitionCatalog) RainVulnerable(name string) (bool, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return false, err
	}
	if power := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Power); power != nil {
		return power.GetShortCircuitInRain(), nil
	}
	if battery := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Battery); battery != nil {
		return battery.GetShortCircuitInRain(), nil
	}
	return false, contract("def %s has no power comp", name)
}

// JoyKind is the JoyKindDef a building gives (BuildingProperties.joyKind), ""
// for a def that is no joy building.
func (catalog *DefinitionCatalog) JoyKind(name string) (string, error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return "", err
	}
	return row.GetBuilding().GetJoyKind(), nil
}
