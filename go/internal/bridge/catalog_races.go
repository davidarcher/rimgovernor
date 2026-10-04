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

// The StatDefs of an animal race row (#1722).
const (
	StatCarryingCapacity     = "CarryingCapacity"
	StatWildness             = "Wildness"
	StatMinimumHandlingSkill = "MinimumHandlingSkill"
)

// AnimalRaces is every animal race of the catalog (#1722): a ThingDef with
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

// RaceFlags are the game's own flags of def's race (animal, mechanoid,
// insect); false and no error for a def without RaceProperties or in a
// catalog that carries no thing facts.
func (catalog *DefinitionCatalog) RaceFlags(def string) (animal, mechanoid, insect bool) {
	if catalog == nil || catalog.thingFacts == nil {
		return false, false, false
	}
	race := catalog.thingFacts[def].GetRace()
	return race.GetAnimal(), race.GetMechanoid(), race.GetInsect()
}

func (catalog *DefinitionCatalog) buildRaces() (policy.AnimalRaceCatalog, error) {
	out := policy.AnimalRaceCatalog{Races: map[policy.Resource]policy.AnimalRace{}}
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
		if !facts.GetAnimal() {
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
	race := policy.AnimalRace{Def: policy.Resource(name), Predator: props.GetPredator(), Mechanoid: facts.GetMechanoid(), Insect: facts.GetInsect(),
		BodySize: domain.Known(float64(props.GetBaseBodySize())), LifeExpectancy: domain.Known(float64(props.GetLifeExpectancy())),
		ManhunterOnTameFail: domain.Known(float64(props.GetManhunterOnTameFailChance())), ManhunterOnDamage: domain.Known(float64(props.GetManhunterOnDamageChance())),
		Trainables: slices.Clone(facts.GetTrainables()), Edible: slices.Clone(facts.GetEdibleDefs())}
	// A pest is a tree eater: RaceProperties.Eats(FoodTypeFlags.Tree).
	race.Pest = int32(props.GetFoodType())&int32(d.FoodTypeFlags_FOOD_TYPE_FLAGS_TREE) != 0
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
	if p, ok := power[name]; ok {
		race.CombatPower = domain.Known(p)
	}
	race.Products = raceProducts(row)
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
func raceProducts(row *d.ThingDef) []policy.RaceProduct {
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
			out = append(out, policy.RaceProduct{Kind: "eggs", Def: policy.Resource(def), Amount: known(count), IntervalDays: known(float64(egg.GetEggLayIntervalDays()))})
		}
	}
	if spawner := compOf(row, (*d.CompPropertiesAny).GetCompProperties_Spawner); spawner != nil && spawner.GetThingToSpawn() != "" {
		ticks := float64(spawner.GetSpawnIntervalRange().GetMin()+spawner.GetSpawnIntervalRange().GetMax()) * 0.5
		out = append(out, policy.RaceProduct{Kind: "spawner", Def: policy.Resource(spawner.GetThingToSpawn()), Amount: known(float64(spawner.GetSpawnCount())), IntervalDays: known(ticks / float64(domain.TicksPerDay))})
	}
	return out
}
