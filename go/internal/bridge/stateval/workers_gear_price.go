package stateval

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

const (
	statWorkToMake  = "WorkToMake"
	statWorkToBuild = "WorkToBuild"
	// thingSteel is ThingDefOf.Steel, the stuff a relic-styled thing is priced in.
	thingSteel = "Steel"
	// smallVolumePerUnit is ThingDef.SmallVolumePerUnit.
	smallVolumePerUnit = float32(0.1)
	// melee-independent literals of PriceUtility.PawnQualityPriceFactor.
	beautyPriceFactor = float32(0.2)
	// workPriceFloor is the literal in CalculatedBaseMarketValue: work at or
	// under it adds no value.
	workPriceFloor = float32(2)
)

// workerMarketValue is StatWorker_MarketValue: a pawn's value is its def's
// value scaled by its quality; any other thing or def is its statBases value,
// else the value of its ingredients and work, plus the comps' terms.
type workerMarketValue struct{}

func (workerMarketValue) Class() string { return "StatWorker_MarketValue" }

func (workerMarketValue) Unfinalized(req *Request) (float32, error) {
	e := req.Evaluator
	ctx := req.Subject.Context
	if ctx != nil && ctx.Pawn != nil {
		price, err := need(ctx.Gear.PawnPrice, "the pawn's price facts")
		if err != nil {
			return 0, err
		}
		def := *req
		def.Subject = Subject{Def: req.Subject.Def, Stuff: req.Subject.Stuff}
		base, err := e.baseUnfinalized(&def)
		if err != nil {
			return 0, err
		}
		factor, err := e.pawnQualityPriceFactor(price)
		if err != nil {
			return 0, err
		}
		offset, err := e.pawnQualityPriceOffset(price)
		if err != nil {
			return 0, err
		}
		return float32(float32(base*factor) + offset), nil
	}
	var num float32
	if !statListContains(req.statBases(), statMarketValue) {
		// No IFixedBaseMarketValue thing class exists in the game assembly.
		stuff := req.Subject.Stuff
		if ctx != nil {
			relic, err := need(ctx.Gear.RelicStyle, "whether the thing's style source is a relic")
			if err != nil {
				return 0, err
			}
			if relic {
				stuff = thingSteel
			}
		}
		var err error
		if num, err = e.calculatedBaseMarketValue(req.Subject.Terrain, req.Subject.Def, stuff); err != nil {
			return 0, err
		}
	} else {
		if req.Stat.GetDefName() != statMarketValue {
			mv, err := e.statDef(statMarketValue)
			if err != nil {
				return 0, err
			}
			other := *req
			other.Stat = mv
			return e.unfinalized(&other)
		}
		var err error
		if num, err = e.baseUnfinalized(req); err != nil {
			return 0, err
		}
	}
	if ctx == nil {
		return num, nil
	}
	comps, err := need(ctx.Gear.Comps, "the thing's comps")
	if err != nil || comps == nil {
		return num, err
	}
	stat := req.Stat.GetDefName()
	for _, offset := range comps.Offsets[stat] {
		num = float32(num + offset)
	}
	for _, factor := range comps.Factors[stat] {
		num = float32(num * factor)
	}
	return num, nil
}

// baseMarketValue is ThingDef.BaseMarketValue: the def's MarketValue.
func (e *Evaluator) baseMarketValue(def string) (float32, error) {
	v, err := e.Value(statMarketValue, ThingSubject(def, ""))
	if err != nil {
		return 0, fmt.Errorf("MarketValue of %s: %w", def, err)
	}
	return v, nil
}

// costs are BuildableDef.CostList and CostStuffCount, which a cost list for
// the difficulty replaces when it applies.
func (e *Evaluator) costs(terrain bool, def string) ([]*d.Opt_ThingDefCountClass, int32, error) {
	if terrain {
		row := e.catalog.TerrainDef(def)
		if row == nil {
			return nil, 0, fmt.Errorf("catalog has no terrain def %s", def)
		}
		applies, err := e.costListApplies(row.GetCostListForDifficulty())
		if err != nil {
			return nil, 0, err
		}
		if applies {
			return row.GetCostListForDifficulty().GetCostList(), row.GetCostListForDifficulty().GetCostStuffCount(), nil
		}
		return row.GetCostList(), row.GetCostStuffCount(), nil
	}
	row := e.catalog.ThingDef(def)
	if row == nil {
		return nil, 0, fmt.Errorf("catalog has no thing def %s", def)
	}
	applies, err := e.costListApplies(row.GetCostListForDifficulty())
	if err != nil {
		return nil, 0, err
	}
	if applies {
		return row.GetCostListForDifficulty().GetCostList(), row.GetCostListForDifficulty().GetCostStuffCount(), nil
	}
	return row.GetCostList(), row.GetCostStuffCount(), nil
}

// calculatedBaseMarketValue is StatWorker_MarketValue.CalculatedBaseMarketValue:
// the value of the recipe's ingredients, or of the cost list and stuff, plus the
// work to make the thing at 0.0036 a unit, over the units a batch makes.
func (e *Evaluator) calculatedBaseMarketValue(terrain bool, def, stuff string) (float32, error) {
	game, err := e.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	consts := game.GetStatWorker_MarketValue()
	costList, costStuff, err := e.costs(terrain, def)
	if err != nil {
		return 0, err
	}
	recipe, err := e.calculableRecipe(terrain, def, costList, costStuff)
	if err != nil {
		return 0, err
	}
	var num, work, batch float32
	if recipe != nil {
		work = recipe.row.GetWorkAmount()
		batch = float32(recipe.row.GetProducts()[0].GetValue().GetCount())
		for _, ingredient := range recipe.fixed {
			bmv, err := e.baseMarketValue(ingredient.def)
			if err != nil {
				return 0, err
			}
			num = addProd(num, float32(ingredient.count), bmv)
		}
	} else {
		subject := Subject{Def: def, Terrain: terrain, Stuff: stuff}
		toMake, err := e.Value(statWorkToMake, subject)
		if err != nil {
			return 0, err
		}
		toBuild, err := e.Value(statWorkToBuild, subject)
		if err != nil {
			return 0, err
		}
		work = max32(toMake, toBuild)
		batch = 1
		for _, cost := range costList {
			bmv, err := e.baseMarketValue(cost.GetValue().GetThingDef())
			if err != nil {
				return 0, err
			}
			num = addProd(num, float32(cost.GetValue().GetCount()), bmv)
		}
		if costStuff > 0 {
			if stuff == "" {
				num = addProd(num, float32(costStuff), consts.GetDefaultGuessStuffCost())
			} else {
				row := e.catalog.ThingDef(stuff)
				if row == nil {
					return 0, fmt.Errorf("catalog has no stuff def %s", stuff)
				}
				volume := float32(1)
				if row.GetSmallVolume() {
					volume = smallVolumePerUnit
				}
				value, err := e.baseMarketValue(stuff)
				if err != nil {
					return 0, err
				}
				num = float32(num + float32(float32(float32(costStuff)/volume)*value))
			}
		}
	}
	if work > workPriceFloor {
		num = addProd(num, work, consts.GetValuePerWork())
	}
	return float32(num / batch), nil
}

// max32 is Mathf.Max.
func max32(a, b float32) float32 {
	if a > b {
		return a
	}
	return b
}

// calculable is the recipe CalculableRecipe picks and its fixed ingredients.
type calculable struct {
	row   *d.RecipeDef
	fixed []fixedIngredient
}

type fixedIngredient struct {
	def   string
	count int64
}

// calculableRecipe is StatWorker_MarketValue.CalculableRecipe: for a def with
// no cost list, the first recipe that makes only it and has only fixed
// ingredients; a first such recipe with a free ingredient answers null.
func (e *Evaluator) calculableRecipe(terrain bool, def string, costList []*d.Opt_ThingDefCountClass, costStuff int32) (*calculable, error) {
	if terrain || len(costList) > 0 || costStuff > 0 {
		return nil, nil
	}
	for _, recipe := range e.recipes() {
		products := recipe.GetProducts()
		if len(products) != 1 || products[0].GetValue().GetThingDef() != def {
			continue
		}
		fact, err := e.catalog.RecipeIngredients(recipe.GetDefName())
		if err != nil {
			return nil, err
		}
		slots, known := fact.Value()
		if !known {
			return nil, fmt.Errorf("recipe %s: its ingredient slots are not resolvable from the mirrored rows", recipe.GetDefName())
		}
		out := &calculable{row: recipe}
		for _, slot := range slots {
			if len(slot) != 1 {
				return nil, nil
			}
			out.fixed = append(out.fixed, fixedIngredient{def: string(slot[0].Resource), count: slot[0].Count})
		}
		return out, nil
	}
	return nil, nil
}

// recipes are the catalog's RecipeDefs by name.
func (e *Evaluator) recipes() []*d.RecipeDef {
	rows := e.catalog.Defs[(&d.RecipeDef{}).ProtoReflect().Descriptor().FullName()]
	out := make([]*d.RecipeDef, 0, len(rows))
	for _, row := range rows {
		if recipe, ok := row.(*d.RecipeDef); ok {
			out = append(out, recipe)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].GetDefName() < out[j].GetDefName() })
	return out
}

// pawnQualityPriceFactor is PriceUtility.PawnQualityPriceFactor.
func (e *Evaluator) pawnQualityPriceFactor(p PawnPriceFacts) (float32, error) {
	game, err := e.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	price := game.GetPriceUtility()
	num := float32(1)
	num = float32(num * lerp32(float32(1-price.GetSummaryHealthImpact()), 1, p.SummaryHealthPercent))
	capacities := e.catalog.Defs[(&d.PawnCapacityDef{}).ProtoReflect().Descriptor().FullName()]
	names := make([]string, 0, len(capacities))
	for name := range capacities {
		names = append(names, name)
	}
	slices.Sort(names)
	for _, name := range names {
		state, ok := p.Capacities[name]
		if !ok {
			return 0, fmt.Errorf("stat evaluation needs the pawn's %s capacity: not observed", name)
		}
		if !state.Capable {
			num = float32(num * price.GetMissingCapacityFactor())
			continue
		}
		num = float32(num * lerp32(float32(1-price.GetCapacityImpact()), 1, state.TradeLevel))
	}
	if p.HasSkills {
		if len(p.SkillLevels) == 0 {
			return 0, fmt.Errorf("the pawn has a skill tracker with no skills: Average throws")
		}
		// Enumerable.Average over floats sums in double.
		var sum float64
		for _, level := range p.SkillLevels {
			sum += float64(float32(level))
		}
		average := float32(sum / float64(len(p.SkillLevels)))
		curve, err := EvaluateCurve(price.GetAverageSkillCurve(), average)
		if err != nil {
			return 0, err
		}
		num = float32(num * curve)
	}
	stage := bridge.DefRow[*d.LifeStageDef](e.catalog, p.LifeStage)
	if stage == nil {
		return 0, fmt.Errorf("catalog has no life stage def %q", p.LifeStage)
	}
	num = float32(num * stage.GetMarketValueFactor())
	for _, offset := range p.TraitValueOffsets {
		num = float32(num + offset)
	}
	num = addProd(num, p.PawnBeauty, beautyPriceFactor)
	if num < price.GetMinFactor() {
		num = price.GetMinFactor()
	}
	return num, nil
}

// pawnQualityPriceOffset is PriceUtility.PawnQualityPriceOffset.
func (e *Evaluator) pawnQualityPriceOffset(p PawnPriceFacts) (float32, error) {
	var num float32
	for _, name := range p.Hediffs {
		hediff := bridge.DefRow[*d.HediffDef](e.catalog, name)
		if hediff == nil {
			return 0, fmt.Errorf("catalog has no hediff def %s", name)
		}
		if !hediff.GetPriceImpact() && hediff.GetPriceOffset() == 0 {
			continue
		}
		offset := hediff.GetPriceOffset()
		if offset == 0 && hediff.GetSpawnThingOnRemoved() != "" {
			var err error
			if offset, err = e.baseMarketValue(hediff.GetSpawnThingOnRemoved()); err != nil {
				return 0, err
			}
		}
		if !(offset < 1) || !(offset > -1) {
			num = float32(num + offset)
		}
	}
	return num, nil
}
