package bridge

import (
	"math"
	"slices"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The StatDefs of an animal race row.
const (
	StatCarryingCapacity     = "CarryingCapacity"
	StatWildness             = "Wildness"
	StatMinimumHandlingSkill = "MinimumHandlingSkill"
	StatLeatherAmount        = "LeatherAmount"
)

// AnimalRaces is every animal race of the catalog: a ThingDef with
// RaceProperties that the game's own race code calls an animal
// (ThingDefFacts.race), built once from the def rows, the stat table and the
// game-computed race facts. A catalog without race facts has no races; one
// with them but no stat table is a contract breach, never a default.
func (catalog *DefinitionCatalog) AnimalRaces() (policy.AnimalRaceCatalog, error) {
	if catalog == nil {
		return policy.AnimalRaceCatalog{}, nil
	}
	catalog.racesOnce.Do(func() { catalog.races, catalog.racesErr = catalog.buildRaces() })
	return catalog.races, catalog.racesErr
}

func (catalog *DefinitionCatalog) buildRaces() (policy.AnimalRaceCatalog, error) {
	out := policy.AnimalRaceCatalog{Races: map[policy.Resource]policy.AnimalRace{}, Interaction: catalog.animalInteraction()}
	if catalog.thingFacts == nil {
		return out, nil
	}
	power := catalog.combatPowers()
	produced := catalog.producedIngestibles()
	names := make([]string, 0, len(catalog.thingFacts))
	for name, facts := range catalog.thingFacts {
		if facts.GetRace() != nil {
			names = append(names, name)
		}
	}
	slices.Sort(names)
	for _, name := range names {
		facts := catalog.thingFacts[name].GetRace()
		row := catalog.ThingDefs[name]
		if row == nil || row.GetRace() == nil {
			return out, contract("race facts for def %s without race properties", name)
		}
		// A corpse def shares its pawn's RaceProperties; the race is the pawn def.
		if row.GetCategory() != d.ThingCategory_THING_CATEGORY_PAWN {
			continue
		}
		if animal, err := catalog.raceIsAnimal(name, row.GetRace()); err != nil {
			return out, err
		} else if !animal {
			continue
		}
		race, err := catalog.animalRace(name, row, facts, power, produced)
		if err != nil {
			return out, err
		}
		out.Races[race.Def] = race
	}
	return out, nil
}

func (catalog *DefinitionCatalog) animalRace(name string, row *d.ThingDef, facts *o.RaceFacts, power map[string]float64, produced []string) (policy.AnimalRace, error) {
	props := row.GetRace()
	race := policy.AnimalRace{Def: policy.Resource(name), Predator: props.GetPredator(),
		BodySize: domain.Known(float64(props.GetBaseBodySize())), LifeExpectancy: domain.Known(float64(props.GetLifeExpectancy())),
		ManhunterOnTameFail: domain.Known(float64(props.GetManhunterOnTameFailChance())), ManhunterOnDamage: domain.Known(float64(props.GetManhunterOnDamageChance())),
		Edible: slices.Clone(facts.GetEdibleDefs())}
	race.Mechanoid, race.Insect = catalog.RaceFlags(name)
	var err error
	if race.Trainables, err = catalog.raceTrainables(name, props); err != nil {
		return race, err
	}
	// A pest is a tree eater: RaceProperties.Eats(FoodTypeFlags.Tree).
	race.Pest = int32(props.GetFoodType())&int32(d.FoodTypeFlags_FOOD_TYPE_FLAGS_TREE) != 0
	race.EatsPlant = int32(props.GetFoodType())&int32(d.FoodTypeFlags_FOOD_TYPE_FLAGS_PLANT) != 0
	race.GestationDays = float64(props.GetGestationPeriodDays())
	if t := props.GetTrainability(); t != "" {
		race.Trainability = domain.Known(t)
	}
	for stat, into := range map[string]*domain.Fact[float64]{StatCarryingCapacity: &race.CarryingCapacity, StatWildness: &race.Wildness, StatMarketValue: &race.MarketValue} {
		value, shown, err := catalog.ShownStatValue(name, "", stat)
		if err != nil {
			return race, err
		}
		if shown {
			*into = domain.Known(float64(value))
		}
	}
	comfort, err := catalog.animalComfort(name)
	if err != nil {
		return race, err
	}
	race.Comfort = comfort
	skill, shown, err := catalog.ShownStatValue(name, "", StatMinimumHandlingSkill)
	if err != nil {
		return race, err
	}
	if shown && skill >= 0 {
		race.MinimumHandlingSkill = domain.Known(int(math.Round(float64(skill))))
	}
	if leather := props.GetLeatherDef(); leather != "" {
		amount, shown, err := catalog.ShownStatValue(name, "", StatLeatherAmount)
		if err != nil {
			return race, err
		}
		if shown && amount > 0 {
			race.Butchery = append(race.Butchery, policy.SourceProduct{Def: policy.Resource(leather), Amount: float64(amount)})
		}
	}
	for _, product := range row.GetButcherProducts() {
		if def, count := product.GetValue().GetThingDef(), product.GetValue().GetCount(); def != "" && count > 0 {
			amount := float64(count)
			if chance := product.GetValue().Chance; chance != nil {
				amount *= float64(*chance)
			}
			race.Butchery = append(race.Butchery, policy.SourceProduct{Def: policy.Resource(def), Amount: amount})
		}
	}
	if p, ok := power[name]; ok {
		race.CombatPower = domain.Known(p)
	}
	race.MateMtbHours = domain.Known(float64(props.GetMateMtbHours()))
	ages, err := catalog.raceStageAges(name, props)
	if err != nil {
		return race, err
	}
	race.AdultMinAgeTicks = domain.Known(ages.Adult)
	for _, stage := range []struct {
		from *int64
		into *domain.Fact[int64]
	}{{ages.Reproductive, &race.ReproductiveMinAgeTicks}, {ages.Milkable, &race.MilkableMinAgeTicks}, {ages.Shearable, &race.ShearableMinAgeTicks}} {
		if stage.from != nil {
			*stage.into = domain.Known(*stage.from)
		}
	}
	race.LifeStages = catalog.raceLifeStages(props)
	race.LitterSize = litterSizeMean(props.GetLitterSizeCurve())
	race.TamenessCanDecay = domain.Known(facts.GetTamenessCanDecay())
	race.TamenessDecayPeriodTicks = domain.Known(int(facts.GetTamenessDecayPeriodTicks()))
	race.TameChanceFactor = domain.Known(float64(facts.GetTameChanceFactor()))
	if feed := facts.AdultFeedPerDay; feed != nil && *feed > 0 {
		race.AdultFeedPerDay = domain.Known(*feed)
	}
	if meat := catalog.raceMeatDef(row); meat != "" {
		amount, shown, err := catalog.ShownStatValue(name, "", StatMeatAmount)
		if err != nil {
			return race, err
		}
		if !shown {
			return race, contract("race %s has meat %s but the stat table shows no %s", name, meat, StatMeatAmount)
		}
		race.MeatDef = policy.Resource(meat)
		race.MeatAmount = domain.Known(float64(amount))
	}
	if race.Products, err = catalog.raceProducts(row); err != nil {
		return race, err
	}
	nutritionOf := func(def string) (domain.Fact[float64], error) {
		n, shown, err := catalog.ShownStatValue(def, "", StatNutrition)
		if err != nil || !shown || n <= 0 {
			return domain.Unknown[float64](), err
		}
		return domain.Known(float64(n)), nil
	}
	for i := range race.Products {
		if race.Products[i].Kind == "wool" {
			continue
		}
		if race.Products[i].NutritionPerUnit, err = nutritionOf(string(race.Products[i].Def)); err != nil {
			return race, err
		}
	}
	if race.MeatDef != "" {
		if race.MeatNutritionPerUnit, err = nutritionOf(string(race.MeatDef)); err != nil {
			return race, err
		}
	}
	for _, item := range produced {
		if !slices.Contains(race.Edible, item) {
			continue
		}
		nutrition, shown, err := catalog.ShownStatValue(item, "", StatNutrition)
		if err != nil {
			return race, err
		}
		if shown && nutrition > 0 {
			race.FeedItems = append(race.FeedItems, policy.RaceFeedItem{Def: policy.Resource(item), Nutrition: float64(nutrition)})
		}
	}
	return race, nil
}

// raceLifeStages is the race's lifeStageAges with each stage's hunger rate
// factor; nil when an entry or its LifeStageDef row is absent.
func (catalog *DefinitionCatalog) raceLifeStages(props *d.RaceProperties) []policy.RaceLifeStage {
	var out []policy.RaceLifeStage
	for _, entry := range props.GetLifeStageAges() {
		age := entry.GetValue()
		stage := DefRow[*d.LifeStageDef](catalog, age.GetDef())
		if age == nil || stage == nil {
			return nil
		}
		out = append(out, policy.RaceLifeStage{MinAgeTicks: int64(math.Round(float64(age.GetMinAge()) * domain.TicksPerDay * domain.DaysPerYear)), HungerRateFactor: float64(stage.GetHungerRateFactor()), BodySizeFactor: float64(stage.GetBodySizeFactor())})
	}
	return out
}

// litterSizeMean is the mean litter of a birth, as Hediff_Pregnant
// rolls it: one child without a curve, else Rand.ByCurveAverage of the curve,
// never below the one child a birth gives. Unknown for a curve Rand.ByCurve
// rejects (fewer than three points, or ends not at y = 0).
func litterSizeMean(curve *d.SimpleCurve) domain.Fact[float64] {
	points := curve.GetPoints()
	if curve == nil {
		return domain.Known(1.0)
	}
	n := len(points)
	if n < 3 || points[0].GetLoc().GetY() != 0 || points[n-1].GetLoc().GetY() != 0 {
		return domain.Unknown[float64]()
	}
	var area, moment float64
	for i := 0; i < n-1; i++ {
		x0, y0 := float64(points[i].GetLoc().GetX()), float64(points[i].GetLoc().GetY())
		x1, y1 := float64(points[i+1].GetLoc().GetX()), float64(points[i+1].GetLoc().GetY())
		if y0 < 0 {
			return domain.Unknown[float64]()
		}
		area += (x1 - x0) * (y0 + y1)
		moment += (x1 - x0) * (x0*(2*y0+y1) + x1*(y0+2*y1))
	}
	if area <= 0 {
		return domain.Unknown[float64]()
	}
	return domain.Known(math.Max(1, moment/area/3))
}

// animalInteraction is the game's animal interaction job constants; a
// constant a catalog does not carry stays unknown.
func (catalog *DefinitionCatalog) animalInteraction() policy.AnimalInteraction {
	var out policy.AnimalInteraction
	game, err := catalog.GameConstants()
	if err != nil {
		return out
	}
	animal, train := game.GetJobDriver_InteractAnimal(), game.GetTrainableUtility()
	count := func(v int32, into *domain.Fact[int]) {
		if v > 0 {
			*into = domain.Known(int(v))
		}
	}
	amount := func(v float32, into *domain.Fact[float64]) {
		if v > 0 {
			*into = domain.Known(float64(v))
		}
	}
	count(animal.GetTalkDuration(), &out.TalkTicks)
	count(animal.GetFeedDuration(), &out.FeedTicks)
	count(animal.GetFeedCount(), &out.Feeds)
	count(train.GetMinTrainInterval(), &out.MinTrainIntervalTicks)
	amount(animal.GetNutritionPercentagePerFeed(), &out.FeedNutritionFraction)
	amount(animal.GetMaxMinNutritionPerFeed(), &out.FeedNutritionCap)
	return out
}

// combatPowers is the highest PawnKindDef.combatPower of each race.
func (catalog *DefinitionCatalog) combatPowers() map[string]float64 {
	out := map[string]float64{}
	for _, row := range catalog.pawnKinds() {
		if p, seen := out[row.GetRace()]; !seen || float64(row.GetCombatPower()) > p {
			out[row.GetRace()] = float64(row.GetCombatPower())
		}
	}
	return out
}

func (catalog *DefinitionCatalog) pawnKinds() []*d.PawnKindDef {
	var rows []*d.PawnKindDef
	for _, msg := range catalog.Defs[(&d.PawnKindDef{}).ProtoReflect().Descriptor().FullName()] {
		rows = append(rows, msg.(*d.PawnKindDef))
	}
	return rows
}

// producedIngestibles are the defs some recipe produces, sorted.
func (catalog *DefinitionCatalog) producedIngestibles() []string {
	seen := map[string]bool{}
	for _, msg := range catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()] {
		for _, product := range msg.(*d.RecipeDef).GetProducts() {
			if def := product.GetValue().GetThingDef(); def != "" {
				seen[def] = true
			}
		}
	}
	names := make([]string, 0, len(seen))
	for name := range seen {
		names = append(names, name)
	}
	slices.SortFunc(names, strings.Compare)
	return names
}

// raceProducts are the periodic yields of the race's comps: milk, wool, eggs
// and spawned items (chemfuel).
func (catalog *DefinitionCatalog) raceProducts(row *d.ThingDef) ([]policy.RaceProduct, error) {
	var out []policy.RaceProduct
	known := func(v float64) domain.Fact[float64] { return domain.Known(v) }
	if milk := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Milkable); milk != nil && milk.GetMilkDef() != "" {
		out = append(out, policy.RaceProduct{Kind: "milk", Def: policy.Resource(milk.GetMilkDef()), Amount: known(float64(milk.GetMilkAmount())), IntervalDays: known(float64(milk.GetMilkIntervalDays()))})
	}
	if wool := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Shearable); wool != nil && wool.GetWoolDef() != "" {
		out = append(out, policy.RaceProduct{Kind: "wool", Def: policy.Resource(wool.GetWoolDef()), Amount: known(float64(wool.GetWoolAmount())), IntervalDays: known(float64(wool.GetShearIntervalDays()))})
	}
	if egg := compOf(row, (*d.CompPropertiesAny).GetCompProperties_EggLayer); egg != nil {
		def := egg.GetEggUnfertilizedDef()
		if def == "" {
			def = egg.GetEggFertilizedDef()
		}
		if def != "" {
			count := float64(egg.GetEggCountRange().GetMin()+egg.GetEggCountRange().GetMax()) * 0.5
			product := policy.RaceProduct{Kind: "eggs", Def: policy.Resource(def), Amount: known(count), IntervalDays: known(float64(egg.GetEggLayIntervalDays())),
				FertilizationCountMax: int(egg.GetEggFertilizationCountMax()), FemaleOnly: egg.GetEggLayFemaleOnly()}
			if fertilized := egg.GetEggFertilizedDef(); fertilized != "" {
				hatcher := compOf(catalog.ThingDef(fertilized), (*d.CompPropertiesAny).GetCompProperties_Hatcher)
				if hatcher == nil {
					return nil, contract("race %s lays fertilized egg %s with no hatcher comp", row.GetDefName(), fertilized)
				}
				product.FertilizedDef = policy.Resource(fertilized)
				product.HatchDays = known(float64(hatcher.GetHatcherDaystoHatch()))
				product.HatchPawn = policy.Resource(hatcher.GetHatcherPawn())
			}
			out = append(out, product)
		}
	}
	if spawner := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Spawner); spawner != nil && spawner.GetThingToSpawn() != "" {
		ticks := float64(spawner.GetSpawnIntervalRange().GetMin()+spawner.GetSpawnIntervalRange().GetMax()) * 0.5
		out = append(out, policy.RaceProduct{Kind: "spawner", Def: policy.Resource(spawner.GetThingToSpawn()), Amount: known(float64(spawner.GetSpawnCount())), IntervalDays: known(ticks / float64(domain.TicksPerDay))})
	}
	return out, nil
}
