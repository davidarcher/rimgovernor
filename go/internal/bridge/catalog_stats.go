package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/stateval"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// decodeStatEnv is the game state the stat evaluator reads beyond the rows.
// An absent message stays nil: the catalog then evaluates no stat.
func decodeStatEnv(v *o.StatEnv) (*stateval.Env, error) {
	if v == nil {
		return nil, nil
	}
	env := &stateval.Env{ActiveMods: make(map[string]bool, len(v.ActiveMods)), ClassicMode: v.ClassicMode, ScenarioFactors: make(map[string]float32, len(v.ScenarioFactors))}
	for _, mod := range v.ActiveMods {
		if mod == "" {
			return nil, contract("catalog stat environment names an empty mod")
		}
		env.ActiveMods[mod] = true
	}
	for _, f := range v.ScenarioFactors {
		if _, dup := env.ScenarioFactors[f.GetStat()]; dup || validID(f.GetStat()) != nil || math.IsNaN(float64(f.GetFactor())) || math.IsInf(float64(f.GetFactor()), 0) {
			return nil, contract("invalid or repeated catalog scenario stat factor %q", f.GetStat())
		}
		env.ScenarioFactors[f.GetStat()] = f.GetFactor()
	}
	flags := make(map[string]bool, len(v.DifficultyFlags))
	for _, f := range v.DifficultyFlags {
		if _, dup := flags[f.GetName()]; dup || validID(f.GetName()) != nil {
			return nil, contract("invalid or repeated catalog difficulty setting %q", f.GetName())
		}
		flags[f.GetName()] = f.GetValue()
	}
	env.Difficulty = stateval.Some(stateval.Difficulty{ButcherYieldFactor: v.ButcherYieldFactor, FishingYieldFactor: v.FishingYieldFactor, Flags: flags})
	return env, nil
}

// StatEvaluator is the Go port of the game's stat evaluation over this
// catalog's rows and stat environment (stateval). A catalog that carries no
// environment is an error, never an evaluator with guessed state.
func (catalog *DefinitionCatalog) StatEvaluator() (*stateval.Evaluator, error) {
	if catalog == nil || catalog.statEnv == nil {
		return nil, contract("definition catalog carries no stat environment")
	}
	catalog.evalOnce.Do(func() { catalog.eval = stateval.New(catalog, *catalog.statEnv) })
	return catalog.eval, nil
}

// StatValue is the game's GetStatValueAbstract(stat, stuff) of def, with stuff
// empty for a def not made from stuff. A def or stuff the catalog lacks and a
// stat the game does not show for the def are errors, never a default.
func (catalog *DefinitionCatalog) StatValue(def, stuff, stat string) (float32, error) {
	value, shown, err := catalog.ShownStatValue(def, stuff, stat)
	if err == nil && !shown {
		return 0, contract("stat %s is not shown for def %s with stuff %q", stat, def, stuff)
	}
	return value, err
}

// ShownStatValue is StatValue for a stat that is legitimately absent from a
// def: shown is false when the game does not show the stat for the def (the
// def has no such property). DeteriorationRate is a planner stat of every
// ThingDef, which the game hides for a def that does not set it: it is read
// whether shown or not.
func (catalog *DefinitionCatalog) ShownStatValue(def, stuff, stat string) (value float32, shown bool, err error) {
	return catalog.shownValue(stat, stateval.ThingSubject(def, stuff), stat == StatDeteriorationRate)
}

// shownValue is the stat's value for the subject and whether the game shows it;
// a stat that is not shown is not evaluated unless planner says to read it.
func (catalog *DefinitionCatalog) shownValue(stat string, subject stateval.Subject, planner bool) (float32, bool, error) {
	eval, err := catalog.StatEvaluator()
	if err != nil {
		return 0, false, err
	}
	shown, err := eval.ShouldShowFor(stat, subject)
	if err != nil {
		return 0, false, err
	}
	if !shown && !planner {
		return 0, false, nil
	}
	value, err := eval.Value(stat, subject)
	if err != nil {
		return 0, false, err
	}
	if math.IsNaN(float64(value)) || math.IsInf(float64(value), 0) {
		return 0, false, contract("stat %s of %s with stuff %q is %v", stat, subject.Def, subject.Stuff, value)
	}
	return value, true, nil
}

// AdjustedCosts is the game's ThingDef.CostListAdjusted(stuff) of def, with
// stuff empty for a def not made from stuff; an empty list is a def with no
// cost.
func (catalog *DefinitionCatalog) AdjustedCosts(def, stuff string) ([]*o.Quantity, error) {
	eval, err := catalog.StatEvaluator()
	if err != nil {
		return nil, err
	}
	costs, err := eval.CostListAdjusted(def, stuff)
	return quantities(costs), err
}

// AllowedStuffs is the game's GenStuff.AllowedStuffsFor(def): the stuffs the
// def can be made from, by name, empty for a def not made from stuff.
func (catalog *DefinitionCatalog) AllowedStuffs(def string) ([]string, error) {
	eval, err := catalog.StatEvaluator()
	if err != nil {
		return nil, err
	}
	return eval.AllowedStuffsFor(def)
}

// TerrainAdjustedCosts is the game's CostListAdjusted(null) of a TerrainDef;
// an empty list is a free floor.
func (catalog *DefinitionCatalog) TerrainAdjustedCosts(terrain string) ([]*o.Quantity, error) {
	eval, err := catalog.StatEvaluator()
	if err != nil {
		return nil, err
	}
	costs, err := eval.TerrainCostListAdjusted(terrain)
	return quantities(costs), err
}

func quantities(costs []stateval.Cost) []*o.Quantity {
	var out []*o.Quantity
	for _, cost := range costs {
		out = append(out, &o.Quantity{DefName: &cost.Def, Units: &cost.Units})
	}
	return out
}

// TerrainWorkToBuild is the game's GetStatValueAbstract(WorkToBuild) of a
// TerrainDef. The game does not show the stat for terrain, so it is read
// whether shown or not.
func (catalog *DefinitionCatalog) TerrainWorkToBuild(terrain string) (float32, error) {
	value, _, err := catalog.shownValue(StatWorkToBuild, stateval.TerrainSubject(terrain), true)
	return value, err
}

// TerrainStatValue is the game's GetStatValueAbstract(stat) of a TerrainDef.
// A terrain the catalog lacks and a stat the game does not show for the
// terrain are errors, never a default.
func (catalog *DefinitionCatalog) TerrainStatValue(terrain, stat string) (float32, error) {
	value, shown, err := catalog.shownValue(stat, stateval.TerrainSubject(terrain), false)
	if err == nil && !shown {
		return 0, contract("stat %s is not shown for terrain %s", stat, terrain)
	}
	return value, err
}
