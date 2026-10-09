package bridge

import (
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Item facts the generated def rows state. A frame carries what
// varies; what a def says about itself is read here, once per load, from its
// ThingDef row. A def without a row is a contract error, never a default.

// StatNutrition is the StatDef whose abstract value is a food's nutrition.
const StatNutrition = "Nutrition"

// StatDeteriorationRate is the StatDef whose abstract value is how fast a
// def left outside deteriorates.
const StatDeteriorationRate = "DeteriorationRate"

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
		if found := pick(comp.GetValue()); found != nil {
			return found
		}
	}
	return nil
}

// SpawnForbiddenProducts is the set of item defs some def's spawner makes
// forbidden on purpose (CompProperties_Spawner.spawnForbidden): a hive's
// insect jelly. A nil catalog has none.
func (catalog *DefinitionCatalog) SpawnForbiddenProducts() map[string]bool {
	if catalog == nil {
		return nil
	}
	if cached := catalog.spawnForbidden.Load(); cached != nil {
		return *cached
	}
	out := map[string]bool{}
	for _, row := range catalog.ThingDefs {
		if spawner := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Spawner); spawner != nil && spawner.GetSpawnForbidden() && spawner.GetThingToSpawn() != "" {
			out[spawner.GetThingToSpawn()] = true
		}
	}
	catalog.spawnForbidden.Store(&out)
	return out
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
			switch doer.GetValue().GetValue().(type) {
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
	within, err := catalog.categoriesWithin(name, row)
	if err != nil {
		return "", err
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

// TradeFood is what a food def's rows say about buying it as a routine
// ingredient: the raw class by thing category, a meal food type as
// the prepared "any" class, whether it never rots, and whether it is a raw
// crop (the vegetable class). ok is false for a def that is neither raw
// ingredient nor meal, and for human meat. Nutrition is the caller's (a stat).
func (catalog *DefinitionCatalog) TradeFood(name string) (good policy.TradeFoodGood, ok bool, err error) {
	row, err := catalog.thingRow(name)
	if err != nil {
		return policy.TradeFoodGood{}, false, err
	}
	class, err := catalog.RawFoodClass(name)
	if err != nil {
		return policy.TradeFoodGood{}, false, err
	}
	if class == "" && row.GetIngestible().GetFoodType()&d.FoodTypeFlags_FOOD_TYPE_FLAGS_MEAL != 0 {
		class = policy.IngredientAny
	}
	if class == "" {
		return policy.TradeFoodGood{}, false, nil
	}
	if class == policy.IngredientMeat {
		if source := row.GetIngestible().GetSourceDef(); source != "" {
			race, err := catalog.thingRow(source)
			if err != nil {
				return policy.TradeFoodGood{}, false, err
			}
			if race.GetRace().GetIntelligence() >= d.Intelligence_INTELLIGENCE_HUMANLIKE {
				return policy.TradeFoodGood{}, false, nil
			}
		}
	}
	_, perishable, err := catalog.RotDays(name)
	if err != nil {
		return policy.TradeFoodGood{}, false, err
	}
	return policy.TradeFoodGood{Class: class, Prepared: class == policy.IngredientAny, NonPerishable: !perishable, Crop: class == policy.IngredientVegetable}, true, nil
}

// categoriesWithin is every ThingCategoryDef the def sits within: its own
// thing categories and all their parents.
func (catalog *DefinitionCatalog) categoriesWithin(name string, row *d.ThingDef) (map[string]bool, error) {
	within := map[string]bool{}
	for _, category := range row.GetThingCategories() {
		// A category already walked has its parents in within, which also ends a cycle.
		for category != "" && !within[category] {
			within[category] = true
			parent := DefRow[*d.ThingCategoryDef](catalog, category)
			if parent == nil {
				return nil, contract("catalog has no thing category %s (of %s)", category, name)
			}
			category = parent.GetParent()
		}
	}
	return within, nil
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

var foodKinds = map[o.FoodKind]policy.FoodKind{
	o.FoodKind_FOOD_KIND_MEAL_AWFUL:     policy.FoodKindMealAwful,
	o.FoodKind_FOOD_KIND_MEAL_SIMPLE:    policy.FoodKindMealSimple,
	o.FoodKind_FOOD_KIND_MEAL_FINE:      policy.FoodKindMealFine,
	o.FoodKind_FOOD_KIND_MEAL_LAVISH:    policy.FoodKindMealLavish,
	o.FoodKind_FOOD_KIND_RAW_MEAT:       policy.FoodKindRawMeat,
	o.FoodKind_FOOD_KIND_HUMAN_MEAT:     policy.FoodKindHumanMeat,
	o.FoodKind_FOOD_KIND_INSECT_MEAT:    policy.FoodKindInsectMeat,
	o.FoodKind_FOOD_KIND_VEGETABLE:      policy.FoodKindVegetable,
	o.FoodKind_FOOD_KIND_FUNGUS:         policy.FoodKindFungus,
	o.FoodKind_FOOD_KIND_ANIMAL_PRODUCT: policy.FoodKindAnimalProduct,
	o.FoodKind_FOOD_KIND_OTHER:          policy.FoodKindOther,
	o.FoodKind_FOOD_KIND_KIBBLE:         policy.FoodKindKibble,
	o.FoodKind_FOOD_KIND_HAY:            policy.FoodKindHay,
}

var mealIngredients = map[o.MealIngredients]policy.MealIngredients{
	o.MealIngredients_MEAL_INGREDIENTS_ANY:      policy.MealAnyIngredients,
	o.MealIngredients_MEAL_INGREDIENTS_MEAT:     policy.MealMeatOnly,
	o.MealIngredients_MEAL_INGREDIENTS_NON_MEAT: policy.MealNonMeat,
}

// decodeThingFacts indexes the game-computed ThingDef flags: a row
// names a known ThingDef once, a food kind and meal ingredients are known
// enum values, and the ingredients are set on meals only. An absent list
// stays nil.
func decodeThingFacts(rows []*o.ThingDefFacts, things map[string]*d.ThingDef) (map[string]*o.ThingDefFacts, error) {
	if rows == nil {
		return nil, nil
	}
	out := make(map[string]*o.ThingDefFacts, len(rows))
	for _, row := range rows {
		name := row.GetDefName()
		if things[name] == nil || out[name] != nil {
			return nil, contract("catalog thing facts for unknown or repeated def %q", name)
		}
		kind := row.GetFoodKind()
		meal := kind >= o.FoodKind_FOOD_KIND_MEAL_AWFUL && kind <= o.FoodKind_FOOD_KIND_MEAL_LAVISH
		if row.FoodKind != nil && foodKinds[kind] == "" || row.MealIngredients != nil && (!meal || mealIngredients[row.GetMealIngredients()] == "") || row.FoodKind != nil && meal && row.MealIngredients == nil {
			return nil, contract("catalog thing facts of %s carry an invalid food kind %v or ingredients %v", name, kind, row.GetMealIngredients())
		}
		roles := row.GetRoomRoles()
		for i, role := range roles {
			if !slices.Contains(policy.FurnitureRoles, policy.FurnitureRole(role)) || i > 0 && roles[i-1] >= role {
				return nil, contract("catalog thing facts of %s carry invalid room roles %v", name, roles)
			}
		}
		out[name] = row
	}
	return out, nil
}

// thingFactsRow is name's game-computed flags row.
func (catalog *DefinitionCatalog) thingFactsRow(name string) (*o.ThingDefFacts, error) {
	if catalog == nil || catalog.thingFacts == nil {
		return nil, contract("definition catalog carries no thing facts")
	}
	row := catalog.thingFacts[name]
	if row == nil {
		return nil, contract("catalog has no thing facts for %s", name)
	}
	return row, nil
}

// Foods is every food a policy can allow (a nutrition-giving ingestible that
// is no drug and no corpse) with the kind the game's own classification gives
// it and, for a meal, its ingredients, sorted by name.
func (catalog *DefinitionCatalog) Foods() []policy.Food {
	if catalog == nil {
		return nil
	}
	var foods []policy.Food
	for name, row := range catalog.thingFacts {
		if row.FoodKind != nil {
			foods = append(foods, policy.Food{Def: name, Kind: foodKinds[row.GetFoodKind()], Ingredients: mealIngredients[row.GetMealIngredients()]})
		}
	}
	slices.SortFunc(foods, func(a, b policy.Food) int { return strings.Compare(a.Def, b.Def) })
	return foods
}

// FoodKindOf is the kind the game's own classification gives the def; empty
// for a def that is no food.
func (catalog *DefinitionCatalog) FoodKindOf(name string) (policy.FoodKind, error) {
	row, err := catalog.thingFactsRow(name)
	if err != nil || row.FoodKind == nil {
		return "", err
	}
	return foodKinds[row.GetFoodKind()], nil
}

// RawMeat is ThingDef.IsMeat of the def.
func (catalog *DefinitionCatalog) RawMeat(name string) (bool, error) {
	row, err := catalog.thingFactsRow(name)
	return row.GetRawMeat(), err
}

// Medicine is ThingDef.IsMedicine of the def.
func (catalog *DefinitionCatalog) Medicine(name string) (bool, error) {
	row, err := catalog.thingFactsRow(name)
	return row.GetMedicine(), err
}
