package bridge

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// What a recipe is, costs and needs at a bench is read from its RecipeDef row
// and the rows it joins; a frame carries only whether a bench offers
// the recipe now. Derivations mirror native game rules; unreadable rows
// return named errors.

// The game classes the recipe view matches by base class.
const (
	// classBillGiverBuilding is every building that is an IBillGiver: the
	// work table family (Building_MechGestator derives from it).
	classBillGiverBuilding = "RimWorld.Building_WorkTable"
	// classDoBillGiver is the work giver that serves a bill giver.
	classDoBillGiver = "RimWorld.WorkGiver_DoBill"
	// classNutritionGetter values an ingredient by its nutrition; the default
	// getter values it by volume.
	classNutritionGetter = "RimWorld.IngredientValueGetter_Nutrition"
	// classVolumeGetter is the getter a recipe that names none uses.
	classVolumeGetter = "RimWorld.IngredientValueGetter_Volume"
	// smallVolumePerUnit is ThingDef.SmallVolumePerUnit.
	smallVolumePerUnit = float32(0.1)
)

// recipeSlot is one ingredient slot resolved against the rows: the defs its
// filter allows and the count each takes.
type recipeSlot struct {
	allowed []string
	counts  []int64
}

// RecipeProducts are the distinct defs the recipe makes, in the row's order.
func (catalog *DefinitionCatalog) RecipeProducts(name string) ([]policy.Resource, error) {
	row, err := catalog.Recipe(name)
	if err != nil {
		return nil, err
	}
	return recipeProductDefs(row)
}

func recipeProductDefs(row *d.RecipeDef) ([]policy.Resource, error) {
	seen := map[policy.Resource]bool{}
	out := make([]policy.Resource, 0, len(row.GetProducts()))
	for _, product := range row.GetProducts() {
		def := policy.Resource(product.GetValue().GetThingDef())
		if def == "" {
			continue
		}
		if !seen[def] {
			seen[def] = true
			out = append(out, def)
		}
	}
	return out, nil
}

// RecipeIngredients are the recipe's ingredient slots as alternatives, each
// allowed def with the count the slot takes of it (the game's
// IngredientCount.CountRequiredOfFor, which the recipe's value getter scales).
// Unknown when a slot allows no def or takes none of one.
func (catalog *DefinitionCatalog) RecipeIngredients(name string) (domain.Fact[[][]policy.Amount], error) {
	slots, err := catalog.recipeSlots(name)
	if err != nil {
		return domain.Unknown[[][]policy.Amount](), err
	}
	out := make([][]policy.Amount, 0, len(slots))
	for _, slot := range slots {
		if len(slot.allowed) == 0 {
			return domain.Unknown[[][]policy.Amount](), nil
		}
		amounts := make([]policy.Amount, 0, len(slot.allowed))
		for i, def := range slot.allowed {
			if slot.counts[i] <= 0 {
				return domain.Unknown[[][]policy.Amount](), nil
			}
			amounts = append(amounts, policy.Amount{Resource: policy.Resource(def), Count: slot.counts[i]})
		}
		out = append(out, amounts)
	}
	return domain.Known(out), nil
}

// recipeSlots resolves every slot of the recipe, once per catalog.
func (catalog *DefinitionCatalog) recipeSlots(name string) ([]recipeSlot, error) {
	row, err := catalog.Recipe(name)
	if err != nil {
		return nil, err
	}
	catalog.recipes.mu.Lock()
	defer catalog.recipes.mu.Unlock()
	if slots, ok := catalog.recipes.slots[name]; ok {
		return slots, nil
	}
	within, err := catalog.thingCategoryIndex()
	if err != nil {
		return nil, err
	}
	getter := row.GetIngredientValueGetterClass()
	if getter == "" {
		getter = classVolumeGetter
	}
	nutrition, err := catalog.ClassIsA(getter, classNutritionGetter)
	if err != nil {
		return nil, err
	}
	volume, err := catalog.ClassIsA(getter, classVolumeGetter)
	if err != nil {
		return nil, err
	}
	if !nutrition && !volume {
		return nil, contract("recipe %s values its ingredients with %s, which the recipe view does not model", name, getter)
	}
	slots := make([]recipeSlot, 0, len(row.GetIngredients()))
	for i, ingredient := range row.GetIngredients() {
		slot := recipeSlot{}
		allowed, err := catalog.allowedDefs(ingredient.GetValue().GetFilter(), within)
		if err != nil {
			return nil, contract("recipe %s: %v", name, err)
		}
		if len(allowed) == 0 && namesNothing(ingredient.GetValue().GetFilter()) {
			if allowed, err = catalog.generatedSlot(row, i); err != nil {
				return nil, err
			}
		}
		for _, def := range allowed {
			value, err := catalog.valuePerUnit(def, nutrition)
			if err != nil {
				return nil, err
			}
			if value <= 0 {
				return nil, contract("recipe %s slot allows %s, which has no value to the %s ingredient getter", name, def, getter)
			}
			slot.allowed = append(slot.allowed, def)
			slot.counts = append(slot.counts, int64(math.Ceil(float64(ingredient.GetValue().GetCount()/value))))
		}
		slots = append(slots, slot)
	}
	if catalog.recipes.slots == nil {
		catalog.recipes.slots = map[string][]recipeSlot{}
	}
	catalog.recipes.slots[name] = slots
	return slots, nil
}

// namesNothing is whether the filter's row lists no def, category or stuff
// category: the allowed set a generated recipe's filter was filled with at
// load, which no field of the row records.
func namesNothing(filter *d.ThingFilter) bool {
	return len(filter.GetThingDefs()) == 0 && len(filter.GetCategories()) == 0 && len(filter.GetStuffCategoriesToAllow()) == 0 && len(filter.GetAllowAllWhoCanMake()) == 0
}

// generatedSlot is what RecipeDefGenerator.SetIngredients allowed in slot i of
// a recipe it made from its product's ThingDef: first, when the product is
// made from stuff, every stuff that can make it, then one slot per costList
// entry, which allows exactly that def. A recipe with no product (drug
// administration, which allows the drug it administers) or one the generator
// did not make has no such slot: none is allowed, which leaves the slot unknown.
func (catalog *DefinitionCatalog) generatedSlot(row *d.RecipeDef, i int) ([]string, error) {
	if len(row.GetProducts()) != 1 {
		return nil, nil
	}
	product, err := catalog.thingRow(row.GetProducts()[0].GetValue().GetThingDef())
	if err != nil {
		return nil, err
	}
	stuffed := len(product.GetStuffCategories()) > 0
	if stuffed != row.GetProductHasIngredientStuff() {
		return nil, nil
	}
	costs := product.GetCostList()
	if difficulty := product.GetCostListForDifficulty(); difficulty != nil {
		costs = difficulty.GetCostList()
	}
	slot := i
	if stuffed {
		if i == 0 {
			var out []string
			for name, stuff := range catalog.ThingDefs {
				if stuff.GetStuffProps() != nil && canMake(stuff, product) {
					out = append(out, name)
				}
			}
			slices.Sort(out)
			return out, nil
		}
		slot--
	}
	if len(row.GetIngredients()) != boolInt(stuffed)+len(costs) {
		return nil, contract("recipe %s has %d ingredient slots but its product %s costs %d stuff and %d listed defs", row.GetDefName(), len(row.GetIngredients()), row.GetProducts()[0].GetValue().GetThingDef(), boolInt(stuffed), len(costs))
	}
	return []string{costs[slot].GetValue().GetThingDef()}, nil
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

// valuePerUnit is IngredientValueGetter.ValuePerUnitOf: the nutrition of a
// nutrition-giving ingestible for the nutrition getter, and for the volume
// getter the volume of a stuff (its unit is 0.1 when small) and 1 for any other def.
func (catalog *DefinitionCatalog) valuePerUnit(def string, nutrition bool) (float32, error) {
	row, err := catalog.thingRow(def)
	if err != nil {
		return 0, err
	}
	if nutrition {
		if row.GetIngestible() == nil {
			return 0, nil
		}
		value, shown, err := catalog.ShownStatValue(def, "", StatNutrition)
		if err != nil || !shown {
			return 0, err
		}
		return value, nil
	}
	if row.GetStuffProps() != nil && row.GetSmallVolume() {
		return smallVolumePerUnit, nil
	}
	return 1, nil
}

// thingCategoryIndex is every ThingDef's within-categories set (own thing
// categories and all their parents), built once per catalog.
func (catalog *DefinitionCatalog) thingCategoryIndex() (map[string]map[string]bool, error) {
	catalog.recipes.withinOnce.Do(func() {
		index := make(map[string]map[string]bool, len(catalog.ThingDefs))
		for name, row := range catalog.ThingDefs {
			within, err := catalog.categoriesWithin(name, row)
			if err != nil {
				catalog.recipes.withinErr = err
				return
			}
			index[name] = within
		}
		catalog.recipes.within = index
	})
	return catalog.recipes.within, catalog.recipes.withinErr
}

// allowedDefs is ThingFilter.AllowedThingDefs: every def the filter allows,
// sorted by name. A nil filter allows none.
func (catalog *DefinitionCatalog) allowedDefs(filter *d.ThingFilter, within map[string]map[string]bool) ([]string, error) {
	if filter == nil {
		return nil, nil
	}
	if field := unmodelledFilterField(filter); field != "" {
		return nil, contract("thing filter sets %s, which the recipe evaluator does not model", field)
	}
	var out []string
	for name := range catalog.ThingDefs {
		if filterAccepts(filter, name, within[name]) {
			out = append(out, name)
		}
	}
	slices.Sort(out)
	return out, nil
}

// RecipeWork is the one work requirement a bill on this recipe at the bench
// definition needs covered: the work type of the DoBill giver serving the
// bench together with the recipe's own work skill and the highest minimum
// level over its skill requirements. Unknown when no giver serves the bench.
func (catalog *DefinitionCatalog) RecipeWork(name, bench string) (domain.Fact[[]policy.WorkRequirement], error) {
	row, err := catalog.Recipe(name)
	if err != nil {
		return domain.Unknown[[]policy.WorkRequirement](), err
	}
	work, found, err := catalog.billWorkType(row, bench)
	if err != nil || !found {
		return domain.Unknown[[]policy.WorkRequirement](), err
	}
	minimum := 0
	for _, skill := range row.GetSkillRequirements() {
		if skill.GetValue().GetMinLevel() < 0 {
			return domain.Unknown[[]policy.WorkRequirement](), nil
		}
		minimum = max(minimum, int(skill.GetValue().GetMinLevel()))
	}
	return domain.Known([]policy.WorkRequirement{{Work: policy.WorkType(work), Skill: row.GetWorkSkill(), Minimum: minimum}}), nil
}

// billWorkType is the work type whose DoBill giver serves the bench
// definition, so a worker must have it enabled to take the bill: the first
// such giver by name whose work type the recipe's required giver work type
// allows. Not found when no giver serves the bench.
func (catalog *DefinitionCatalog) billWorkType(recipe *d.RecipeDef, bench string) (string, bool, error) {
	givers, err := catalog.billGivers()
	if err != nil {
		return "", false, err
	}
	for _, giver := range givers[bench] {
		if recipe.GetRequiredGiverWorkType() == "" || recipe.GetRequiredGiverWorkType() == giver.GetWorkType() {
			return giver.GetWorkType(), true, nil
		}
	}
	return "", false, nil
}

// billGivers are the DoBill work givers of each bench definition they serve,
// by giver name, built once.
func (catalog *DefinitionCatalog) billGivers() (map[string][]*d.WorkGiverDef, error) {
	catalog.recipes.giversOnce.Do(func() { catalog.recipes.givers, catalog.recipes.giversErr = catalog.buildBillGivers() })
	return catalog.recipes.givers, catalog.recipes.giversErr
}

func (catalog *DefinitionCatalog) buildBillGivers() (map[string][]*d.WorkGiverDef, error) {
	rows := catalog.Defs[(&d.WorkGiverDef{}).ProtoReflect().Descriptor().FullName()]
	names := make([]string, 0, len(rows))
	for name := range rows {
		names = append(names, name)
	}
	slices.SortFunc(names, strings.Compare)
	out := map[string][]*d.WorkGiverDef{}
	for _, name := range names {
		giver := rows[name].(*d.WorkGiverDef)
		if giver.GetWorkType() == "" || len(giver.GetFixedBillGiverDefs()) == 0 {
			continue
		}
		doBill, err := catalog.ClassIsA(giver.GetGiverClass(), classDoBillGiver)
		if err != nil {
			return nil, err
		}
		if !doBill {
			continue
		}
		for _, bench := range giver.GetFixedBillGiverDefs() {
			out[bench] = append(out[bench], giver)
		}
	}
	return out, nil
}

// RecipeMechKind is the PawnKindDef of the mech a Biotech gestation recipe
// makes: the catalog's mech kind of the race it produces. "" for every other
// recipe; a gestation recipe that produces no mech race, or one with several
// kinds, is an error.
func (catalog *DefinitionCatalog) RecipeMechKind(name string) (string, error) {
	row, err := catalog.Recipe(name)
	if err != nil {
		return "", err
	}
	if row.GetMechResurrection() {
		return "", nil
	}
	if len(row.GetProducts()) == 1 && len(row.GetSpecialProducts()) == 0 && catalog.Biotech != nil {
		race := row.GetProducts()[0].GetValue().GetThingDef()
		var kinds []string
		for kind, mech := range catalog.Biotech.MechKinds {
			if mech.GetRace() == race {
				kinds = append(kinds, kind)
			}
		}
		slices.Sort(kinds)
		if len(kinds) > 1 {
			return "", contract("recipe %s produces race %s, which has several mech kinds %v", name, race, kinds)
		}
		if len(kinds) == 1 {
			return kinds[0], nil
		}
	}
	if row.GetGestationCycles() > 0 {
		return "", contract("gestation recipe %s does not produce a mechanoid race", name)
	}
	return "", nil
}

// RecipeHosts are the recipes that make the product, with the player-buildable
// bill giver buildings that host each, read before any such bench exists so a
// workshop project can choose which bench to stage. A recipe is available when
// every research project it requires is finished; one gated by ideology (a
// meme, a faction tag, a building precept) is left out, as nothing but the
// game can say it is available.
func (catalog *DefinitionCatalog) RecipeHosts(product string, finished map[string]bool) ([]policy.RecipeHost, error) {
	if catalog == nil {
		return nil, contract("no definition catalog")
	}
	rows := catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	names := make([]string, 0, len(rows))
	for name := range rows {
		names = append(names, name)
	}
	slices.SortFunc(names, strings.Compare)
	var out []policy.RecipeHost
	for _, name := range names {
		row := rows[name].(*d.RecipeDef)
		products, err := recipeProductDefs(row)
		if err != nil {
			return nil, err
		}
		if !slices.Contains(products, policy.Resource(product)) {
			continue
		}
		if len(row.GetMemePrerequisitesAny()) > 0 || len(row.GetFactionPrerequisiteTags()) > 0 || row.GetFromIdeoBuildingPreceptOnly() {
			continue
		}
		benches, err := catalog.recipeBenches(row)
		if err != nil {
			return nil, err
		}
		if len(benches) == 0 {
			continue
		}
		host := policy.RecipeHost{Definition: name, Products: products, Benches: benches, Available: true}
		if host.Ingredients, err = catalog.RecipeIngredients(name); err != nil {
			return nil, err
		}
		host.RequiredWork = domain.Unknown[[]policy.WorkRequirement]()
		for _, bench := range benches {
			if host.RequiredWork, err = catalog.RecipeWork(name, bench); err != nil {
				return nil, err
			}
			if _, known := host.RequiredWork.Value(); known {
				break
			}
		}
		for _, project := range append([]string{row.GetResearchPrerequisite()}, row.GetResearchPrerequisites()...) {
			if project == "" || slices.Contains(host.Research, project) {
				continue
			}
			host.Research = append(host.Research, project)
			host.Available = host.Available && finished[project]
		}
		slices.Sort(host.Research)
		out = append(out, host)
	}
	return out, nil
}

// recipeBenches are the player-buildable bill giver buildings hosting the
// recipe, sorted: the defs it names as users and the defs that list it.
func (catalog *DefinitionCatalog) recipeBenches(recipe *d.RecipeDef) ([]string, error) {
	users := map[string]bool{}
	for _, user := range recipe.GetRecipeUsers() {
		users[user] = true
	}
	for name, row := range catalog.ThingDefs {
		if slices.Contains(row.GetRecipes(), recipe.GetDefName()) {
			users[name] = true
		}
	}
	var out []string
	for name := range users {
		row, err := catalog.thingRow(name)
		if err != nil {
			return nil, err
		}
		if row.GetCategory() != d.ThingCategory_THING_CATEGORY_BUILDING || !Buildable(row) {
			continue
		}
		giver, err := catalog.ClassIsA(row.GetThingClass(), classBillGiverBuilding)
		if err != nil {
			return nil, err
		}
		if giver {
			out = append(out, name)
		}
	}
	slices.Sort(out)
	return out, nil
}

// MealFacts are the facts a meal planner reads of a recipe at a bench: what the
// recipe is worth as food. A measure the rows do not settle is unknown.
type MealFacts struct {
	NeedsPower                                 domain.Fact[bool]
	Mood, NutrientEfficiency, WorkPerNutrition domain.Fact[float64]
	IngredientClasses                          domain.Fact[[]policy.FoodIngredientSlot]
	CookSkillFloor                             domain.Fact[int32]
}

// RecipeMealFacts reads the meal facts of a recipe at a bench definition. A
// recipe that makes one nutrition-giving ingestible has a mood (its taste
// thought's base effect), work per nutrition and, valued by nutrition from
// raw ingredient slots, the nutrient efficiency (product nutrition over
// ingredient nutrition), the raw class alternatives of each slot and the
// cooking skill floor; a recipe that fails any of these steps keeps the
// measures before it.
func (catalog *DefinitionCatalog) RecipeMealFacts(name, bench string) (MealFacts, error) {
	facts := MealFacts{NeedsPower: domain.Unknown[bool](), Mood: domain.Unknown[float64](), NutrientEfficiency: domain.Unknown[float64](), WorkPerNutrition: domain.Unknown[float64](),
		IngredientClasses: domain.Unknown[[]policy.FoodIngredientSlot](), CookSkillFloor: domain.Unknown[int32]()}
	if catalog == nil {
		return facts, nil
	}
	row, err := catalog.Recipe(name)
	if err != nil {
		return facts, err
	}
	benchRow, err := catalog.thingRow(bench)
	if err != nil {
		return facts, err
	}
	powered, err := catalog.HasComp(benchRow, ClassPowerComp)
	if err != nil {
		return facts, err
	}
	facts.NeedsPower = domain.Known(powered)
	if len(row.GetProducts()) != 1 {
		return facts, nil
	}
	product := row.GetProducts()[0].GetValue()
	productRow, err := catalog.thingRow(product.GetThingDef())
	if err != nil {
		return facts, err
	}
	if productRow.GetIngestible() == nil {
		return facts, nil
	}
	value, shown, err := catalog.ShownStatValue(product.GetThingDef(), "", StatNutrition)
	if err != nil {
		return facts, err
	}
	nutrition := value * float32(product.GetCount())
	if !shown || !positive(float64(nutrition)) {
		return facts, nil
	}
	if thought := productRow.GetIngestible().GetTasteThought(); thought == "" {
		facts.Mood = domain.Known(0.0)
	} else {
		def := DefRow[*d.ThoughtDef](catalog, thought)
		if def == nil {
			return facts, contract("catalog has no thought %s (taste of %s)", thought, product.GetThingDef())
		}
		if len(def.GetStages()) == 1 && finite(float64(def.GetStages()[0].GetValue().GetBaseMoodEffect())) {
			facts.Mood = domain.Known(float64(def.GetStages()[0].GetValue().GetBaseMoodEffect()))
		}
	}
	work := row.GetWorkAmount()
	if work < 0 {
		stat, shown, err := catalog.ShownStatValue(product.GetThingDef(), "", statWorkToMake)
		if err != nil {
			return facts, err
		}
		if shown {
			work = stat
		}
	}
	if finite(float64(work)) && work >= 0 {
		facts.WorkPerNutrition = domain.Known(float64(work / nutrition))
	}
	getter := row.GetIngredientValueGetterClass()
	if getter == "" {
		getter = classVolumeGetter
	}
	byNutrition, err := catalog.ClassIsA(getter, classNutritionGetter)
	if err != nil {
		return facts, err
	}
	if !byNutrition || len(row.GetIngredients()) == 0 {
		return facts, nil
	}
	slots, input, ok, err := catalog.mealSlots(row)
	if err != nil || !ok || !positive(input) {
		return facts, err
	}
	facts.IngredientClasses = domain.Known(slots)
	facts.NutrientEfficiency = domain.Known(float64(nutrition) / input)
	if catalog.Constants == nil {
		return facts, contract("catalog has no constants (skill max level)")
	}
	limit := catalog.Constants.GetSkillMaxLevel()
	floor := int32(0)
	for _, skill := range row.GetSkillRequirements() {
		if skill.GetValue().GetSkill() != row.GetWorkSkill() {
			continue
		}
		if minimum := skill.GetValue().GetMinLevel(); minimum < 0 || minimum > limit {
			return facts, nil
		} else {
			floor = max(floor, minimum)
		}
	}
	facts.CookSkillFloor = domain.Known(floor)
	return facts, nil
}

// mealSlots reads each ingredient slot of a nutrition-valued recipe as the raw
// food classes of the defs the slot, the recipe's fixed filter and its default
// filter all allow, with the summed base count of the slots. ok is false when
// a slot has a zero count, no def, a def that is no ingestible or one outside
// every raw class.
func (catalog *DefinitionCatalog) mealSlots(row *d.RecipeDef) (slots []policy.FoodIngredientSlot, input float64, ok bool, err error) {
	within, err := catalog.thingCategoryIndex()
	if err != nil {
		return nil, 0, false, err
	}
	fixed := row.GetFixedIngredientFilter()
	if fixed == nil {
		return nil, 0, false, contract("recipe %s has no fixed ingredient filter", row.GetDefName())
	}
	standard := row.GetDefaultIngredientFilter()
	if standard == nil {
		standard = fixed
	}
	for _, filter := range []*d.ThingFilter{fixed, standard} {
		if field := unmodelledFilterField(filter); field != "" {
			return nil, 0, false, contract("recipe %s filter sets %s, which the recipe evaluator does not model", row.GetDefName(), field)
		}
	}
	for _, ingredient := range row.GetIngredients() {
		count := float64(ingredient.GetValue().GetCount())
		if !positive(count) {
			return nil, 0, false, nil
		}
		input += count
		allowed, err := catalog.allowedDefs(ingredient.GetValue().GetFilter(), within)
		if err != nil {
			return nil, 0, false, contract("recipe %s: %v", row.GetDefName(), err)
		}
		var classes []policy.FoodIngredientClass
		for _, def := range allowed {
			if !filterAccepts(fixed, def, within[def]) || !filterAccepts(standard, def, within[def]) {
				continue
			}
			if catalog.ThingDefs[def].GetIngestible() == nil {
				return nil, 0, false, nil
			}
			class, err := catalog.RawFoodClass(def)
			if err != nil {
				return nil, 0, false, err
			}
			if class == "" {
				return nil, 0, false, nil
			}
			if !slices.Contains(classes, class) {
				classes = append(classes, class)
			}
		}
		if len(classes) == 0 {
			return nil, 0, false, nil
		}
		slices.SortFunc(classes, func(a, b policy.FoodIngredientClass) int { return rawClassOrder(a) - rawClassOrder(b) })
		if len(classes) == 3 {
			classes = []policy.FoodIngredientClass{policy.IngredientAny}
		}
		slots = append(slots, policy.FoodIngredientSlot{Alternatives: classes})
	}
	return slots, input, true, nil
}

// rawClassOrder is the order a slot lists its classes in: meat, vegetable, animal product.
func rawClassOrder(class policy.FoodIngredientClass) int {
	switch class {
	case policy.IngredientMeat:
		return 1
	case policy.IngredientVegetable:
		return 2
	}
	return 3
}

func positive(n float64) bool { return finite(n) && n > 0 }
