package stateval

import (
	"fmt"
	"math"

	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
	"google.golang.org/protobuf/proto"
)

// The gear-group StatParts (epic #2621, #2639): the difficulty yield factors,
// the worn gear's mass and stat terms, and the market value terms of a
// thing's reload state, weapon traits and wearer. Their live facts are
// StatContext.Gear (context_gear.go) and Env.Difficulty.

func gearPartList() []Part {
	return []Part{
		partDifficulty{}, partDifficultyButcherYield{}, partDifficultyFishingYield{}, partDifficultyMiningYield{},
		partGearAndInventoryMass{}, partGearStatFactor{}, partGearStatOffset{},
		partReloadMarketValue{}, partWeaponTraitsMarketValueOffset{}, partWornByCorpse{},
	}
}

const (
	classCorpse  = "Verse.Corpse"
	classApparel = "RimWorld.Apparel"

	statMeleeHitChance = "MeleeHitChance"
)

// namedStat is a stated stat value of who, or the error naming it.
func namedStat(stats map[string]float32, stat, who string) (float32, error) {
	v, ok := stats[stat]
	if !ok {
		return 0, fmt.Errorf("stat evaluation needs %s's %s stat value: not observed", who, stat)
	}
	return v, nil
}

// gearFacts is the request's gear facts: nil for a definition request.
func gearFacts(req *Request) *GearFacts {
	if c := req.Subject.Context; c != nil {
		return &c.Gear
	}
	return nil
}

// difficulty is Find.Storyteller's state, an error when the environment does
// not state it.
func (e *Evaluator) difficulty() (Difficulty, error) {
	return need(e.env.Difficulty, "the storyteller difficulty (Env.Difficulty)")
}

// --- difficulty ---

// partDifficulty is the obsolete StatPart_Difficulty: it multiplies by a
// Multiplier that always answers 1, but reads Find.Storyteller.difficultyDef,
// which throws without a storyteller.
type partDifficulty struct{ plain }

func (partDifficulty) Class() string { return "StatPart_Difficulty" }

func (partDifficulty) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	diff, err := req.Evaluator.difficulty()
	if err != nil {
		return 0, err
	}
	if diff.NoStoryteller {
		return 0, fmt.Errorf("StatPart_Difficulty reads Find.Storyteller.difficultyDef with no storyteller: the game throws")
	}
	return mul(val, 1), nil
}

// yieldFactor is the storyteller's factor, 1 without a storyteller.
func yieldFactor(req *Request, pick func(Difficulty) float32) (float32, error) {
	diff, err := req.Evaluator.difficulty()
	if err != nil {
		return 0, err
	}
	if diff.NoStoryteller {
		return 1, nil
	}
	return pick(diff), nil
}

// partDifficultyButcherYield is StatPart_Difficulty_ButcherYield.
type partDifficultyButcherYield struct{ plain }

func (partDifficultyButcherYield) Class() string { return "StatPart_Difficulty_ButcherYield" }

func (partDifficultyButcherYield) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	f, err := yieldFactor(req, func(diff Difficulty) float32 { return diff.ButcherYieldFactor })
	if err != nil {
		return 0, err
	}
	return mul(val, f), nil
}

// partDifficultyFishingYield is StatPart_Difficulty_FishingYield.
type partDifficultyFishingYield struct{ plain }

func (partDifficultyFishingYield) Class() string { return "StatPart_Difficulty_FishingYield" }

func (partDifficultyFishingYield) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	f, err := yieldFactor(req, func(diff Difficulty) float32 { return diff.FishingYieldFactor })
	if err != nil {
		return 0, err
	}
	return mul(val, f), nil
}

// partDifficultyMiningYield is StatPart_Difficulty_MiningYield: its
// TransformValue is empty (the mining yield factor is applied where ore is
// mined).
type partDifficultyMiningYield struct{ plain }

func (partDifficultyMiningYield) Class() string { return "StatPart_Difficulty_MiningYield" }

func (partDifficultyMiningYield) Transform(_ *Request, _ proto.Message, val float32) (float32, error) {
	return val, nil
}

// costListApplies is CostListForDifficulty.Applies.
func (e *Evaluator) costListApplies(c *d.CostListForDifficulty) (bool, error) {
	if c == nil {
		return false, nil
	}
	diff, err := e.difficulty()
	if err != nil || diff.NoStoryteller {
		return false, err
	}
	if c.GetDifficultyVar() == "" {
		return false, nil
	}
	applies, ok := diff.Flags[c.GetDifficultyVar()]
	if !ok {
		return false, fmt.Errorf("stat evaluation needs the difficulty setting %s: not observed", c.GetDifficultyVar())
	}
	if c.GetInvert() {
		applies = !applies
	}
	return applies, nil
}

// --- gear mass ---

// pawnOrCorpse is TryGetPawnOrCorpseStat's thing branch: whether the thing is
// a Pawn or a Corpse (whose InnerPawn's gear is read).
func (e *Evaluator) pawnOrCorpse(req *Request) (bool, error) {
	if req.Subject.Context.Pawn != nil {
		return true, nil
	}
	return e.classIsA(req.Thing.GetThingClass(), classCorpse)
}

// partGearAndInventoryMass is StatPart_GearAndInventoryMass: add the carried
// mass of a pawn, or of a corpse's pawn. A pawn or corpse def adds 0.
type partGearAndInventoryMass struct{}

func (partGearAndInventoryMass) Class() string { return "StatPart_GearAndInventoryMass" }

func (partGearAndInventoryMass) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	e := req.Evaluator
	if req.Subject.Context == nil {
		// PawnOrCorpseStatUtility, no thing: a pawn def or a corpse def
		// answers 0 (a terrain is no ThingDef).
		if req.Thing == nil {
			return val, nil
		}
		if req.Thing.GetCategory() == d.ThingCategory_THING_CATEGORY_PAWN {
			return float32(val + 0), nil
		}
		corpse, err := e.classIsA(req.Thing.GetThingClass(), classCorpse)
		if err != nil || !corpse {
			return val, err
		}
		if req.Thing.GetIngestible() == nil {
			return 0, fmt.Errorf("corpse def %s has no ingestible: the game dereferences its sourceDef", req.Subject.Def)
		}
		return float32(val + 0), nil
	}
	ok, err := e.pawnOrCorpse(req)
	if err != nil || !ok {
		return val, err
	}
	m, err := need(req.Subject.Context.Gear.Mass, "the pawn's gear and inventory mass")
	if err != nil {
		return 0, err
	}
	// MassUtility.GearMass (apparel, then equipment) + InventoryMass.
	var gear float32
	for _, v := range m.Apparel {
		gear = float32(gear + v)
	}
	for _, v := range m.Equipment {
		gear = float32(gear + v)
	}
	var inventory float32
	for _, s := range m.Inventory {
		inventory = float32(inventory + float32(float32(s.Count)*s.Mass))
	}
	return float32(val + float32(gear+inventory)), nil
}

func (partGearAndInventoryMass) ForceShow(req *Request, _ proto.Message) (bool, error) {
	ctx := req.Subject.Context
	if ctx == nil {
		// req.Pawn is null and the request has no thing.
		return false, nil
	}
	if ctx.Pawn != nil {
		return true, nil
	}
	return need(ctx.Gear.ForPawn, "whether the request carries a pawn")
}

// --- gear stat terms ---

// partGearStatFactor is StatPart_GearStatFactor: multiply by the apparelStat
// of each worn apparel and, with includeWeapon, of the primary weapon.
type partGearStatFactor struct{ plain }

func (partGearStatFactor) Class() string { return "StatPart_GearStatFactor" }

func (partGearStatFactor) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_GearStatFactor](row, "StatPart_GearStatFactor")
	if err != nil {
		return 0, err
	}
	ctx := req.Subject.Context
	if ctx == nil || ctx.Pawn == nil {
		return val, nil
	}
	apparel, err := need(ctx.Gear.Apparel, "the pawn's worn apparel")
	if err != nil {
		return 0, err
	}
	for _, item := range apparel {
		v, err := item.stat(p.GetApparelStat())
		if err != nil {
			return 0, err
		}
		val = mul(val, v)
	}
	if p.GetIncludeWeapon() {
		primary, err := need(ctx.Gear.Primary, "the pawn's primary weapon")
		if err != nil {
			return 0, err
		}
		if primary != nil {
			v, err := primary.stat(p.GetApparelStat())
			if err != nil {
				return 0, err
			}
			val = mul(val, v)
		}
	}
	return val, nil
}

// partGearStatOffset is StatPart_GearStatOffset: add (or subtract) the
// apparelStat plus the equipped stat offset of each worn apparel and, with
// includeWeapon, of the primary weapon.
type partGearStatOffset struct{ plain }

func (partGearStatOffset) Class() string { return "StatPart_GearStatOffset" }

func (partGearStatOffset) Transform(req *Request, row proto.Message, val float32) (float32, error) {
	p, err := rowOf[*d.StatPart_GearStatOffset](row, "StatPart_GearStatOffset")
	if err != nil {
		return 0, err
	}
	ctx := req.Subject.Context
	if ctx == nil || ctx.Pawn == nil {
		return val, nil
	}
	e := req.Evaluator
	stat := DefRow[*d.StatDef](e.catalog, p.GetApparelStat())
	if stat == nil {
		return 0, fmt.Errorf("catalog has no stat def %s", p.GetApparelStat())
	}
	apply := func(item GearItem) error {
		v, err := item.stat(p.GetApparelStat())
		if err != nil {
			return err
		}
		offset, err := e.statOffsetFromGear(item, stat)
		if err != nil {
			return err
		}
		v = float32(v + offset)
		if p.GetSubtract() {
			val = float32(val - v)
		} else {
			val = float32(val + v)
		}
		return nil
	}
	apparel, err := need(ctx.Gear.Apparel, "the pawn's worn apparel")
	if err != nil {
		return 0, err
	}
	for _, item := range apparel {
		if err := apply(item); err != nil {
			return 0, err
		}
	}
	if p.GetIncludeWeapon() {
		primary, err := need(ctx.Gear.Primary, "the pawn's primary weapon")
		if err != nil {
			return 0, err
		}
		if primary != nil {
			if err := apply(*primary); err != nil {
				return 0, err
			}
		}
	}
	return val, nil
}

// statOffsetFromGear is StatWorker.StatOffsetFromGear: the gear def's equipped
// offsets and its bladelink traits', then the stat's parts run over the gear
// as a thing request.
func (e *Evaluator) statOffsetFromGear(item GearItem, stat *d.StatDef) (float32, error) {
	row := e.catalog.ThingDef(item.Def)
	if row == nil {
		return 0, fmt.Errorf("catalog has no thing def %s", item.Def)
	}
	val, err := modifierFromList(row.GetEquippedStatOffsets(), stat.GetDefName(), 0)
	if err != nil {
		return 0, err
	}
	traits, err := need(item.Bladelink, "gear "+item.Def+"'s bladelink weapon traits")
	if err != nil {
		return 0, err
	}
	for _, name := range traits {
		trait := DefRow[*d.WeaponTraitDef](e.catalog, name)
		if trait == nil {
			return 0, fmt.Errorf("catalog has no weapon trait def %s", name)
		}
		offset, err := modifierFromList(trait.GetEquippedStatOffsets(), stat.GetDefName(), 0)
		if err != nil {
			return 0, err
		}
		val = float32(val + offset)
	}
	if abs32(val) > float32(math.SmallestNonzeroFloat32) && len(stat.GetParts()) > 0 {
		sub, err := e.request(stat.GetDefName(), Subject{Def: item.Def, Stuff: item.Stuff, Quality: item.Quality, Context: item.context()})
		if err != nil {
			return 0, err
		}
		parts, err := e.orderedParts(stat)
		if err != nil {
			return 0, err
		}
		for _, rp := range parts {
			part, err := e.part(rp.class, "TransformValue")
			if err != nil {
				return 0, err
			}
			if val, err = part.Transform(sub, rp.row, val); err != nil {
				return 0, err
			}
		}
	}
	return val, nil
}

// --- market value terms ---

// partReloadMarketValue is StatPart_ReloadMarketValue: a partly spent
// reloadable loses the value of the missing ammo, or (a charged apparel that
// destroys itself on empty) is scaled by its remaining charges.
type partReloadMarketValue struct{ plain }

func (partReloadMarketValue) Class() string { return "StatPart_ReloadMarketValue" }

func (partReloadMarketValue) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	g := gearFacts(req)
	if g == nil {
		return val, nil
	}
	comp, err := need(g.Reloadable, "the thing's reloadable comp")
	if err != nil || comp == nil || comp.RemainingCharges == comp.MaxCharges {
		return val, err
	}
	switch {
	case comp.AmmoDef != "":
		ammo, err := req.Evaluator.Value(statMarketValue, ThingSubject(comp.AmmoDef, ""))
		if err != nil {
			return 0, fmt.Errorf("MarketValue of ammo %s: %w", comp.AmmoDef, err)
		}
		val = float32(val + float32(float32(0-ammo)*float32(comp.MaxAmmoNeeded)))
	case comp.ChargedDestroyOnEmpty:
		val = mul(val, float32(float32(comp.RemainingCharges)/float32(comp.MaxCharges)))
	}
	if val < 0 {
		val = 0
	}
	return val, nil
}

// partWeaponTraitsMarketValueOffset is StatPart_WeaponTraitsMarketValueOffset:
// add each bladelink trait's market value offset.
type partWeaponTraitsMarketValueOffset struct{ plain }

func (partWeaponTraitsMarketValueOffset) Class() string {
	return "StatPart_WeaponTraitsMarketValueOffset"
}

func (partWeaponTraitsMarketValueOffset) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	g := gearFacts(req)
	if g == nil {
		return val, nil
	}
	traits, err := need(g.Bladelink, "the thing's bladelink weapon traits")
	if err != nil {
		return 0, err
	}
	for _, name := range traits {
		trait := DefRow[*d.WeaponTraitDef](req.Evaluator.catalog, name)
		if trait == nil {
			return 0, fmt.Errorf("catalog has no weapon trait def %s", name)
		}
		val = float32(val + trait.GetMarketValueOffset())
	}
	return val, nil
}

// partWornByCorpse is StatPart_WornByCorpse: apparel taken off a corpse is
// worth a tenth.
type partWornByCorpse struct{ plain }

func (partWornByCorpse) Class() string { return "StatPart_WornByCorpse" }

func (partWornByCorpse) Transform(req *Request, _ proto.Message, val float32) (float32, error) {
	g := gearFacts(req)
	if g == nil {
		return val, nil
	}
	apparel, err := req.Evaluator.classIsA(req.Thing.GetThingClass(), classApparel)
	if err != nil || !apparel {
		return val, err
	}
	worn, err := need(g.WornByCorpse, "whether the apparel was worn by a corpse")
	if err != nil || !worn {
		return val, err
	}
	game, err := req.Evaluator.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	return mul(val, game.GetStatPart_WornByCorpse().GetFactor()), nil
}
