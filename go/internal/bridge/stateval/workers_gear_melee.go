package stateval

import (
	"fmt"
	"slices"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The melee StatWorkers of the gear group (epic #2621, #2639).

const (
	statMeleeWeaponDamageMult   = "MeleeWeapon_DamageMultiplier"
	statMeleeWeaponCooldownMult = "MeleeWeapon_CooldownMultiplier"
	statMeleeDamageFactor       = "MeleeDamageFactor"
	statMeleeCooldownFactor     = "MeleeCooldownFactor"

	classVerbMeleeApplyHediff = "RimWorld.Verb_MeleeApplyHediff"
	// classMeleeAttackVerb is Verb_MeleeAttack (RimWorld, not Verse); show.go shares it.
	classMeleeAttackVerb = "RimWorld.Verb_MeleeAttack"

	// minEfficiencyAlwaysUsable is the floor VerbProperties.GetDamageFactorFor
	// puts under an always-usable linked body part group's efficiency.
	minEfficiencyAlwaysUsable = float32(0.4)
	// meleeAPPerDamage is the armor penetration per damage of a verb with none.
	meleeAPPerDamage = float32(0.015)
	// minWeightDamage is the damage under which a hediff-applying verb's
	// weight does not scale by its square.
	minWeightDamage = float32(0.001)
	// ticksPerSecond is 60: a cooldown's ticks over it are seconds.
	ticksPerSecond = float32(60)
)

// addProd is C# `c += a * b` as the game's runtime computes it: the product is
// not rounded to float before the sum (the recorded Gun_AssaultRifle
// MeleeWeapon_AverageArmorPenetration, 0.13499999, is only reached this way),
// so the sum rounds once.
func addProd(c, a, b float32) float32 { return float32(float64(c) + float64(a)*float64(b)) }

// pawnMeleeVerbs are the pawn's available melee verbs when the request is
// about a Pawn thing; isPawn is false for a definition or any other thing.
func pawnMeleeVerbs(req *Request) (verbs []MeleeVerbEntry, isPawn bool, err error) {
	ctx := req.Subject.Context
	if ctx == nil || ctx.Pawn == nil {
		return nil, false, nil
	}
	verbs, err = need(ctx.Gear.MeleeVerbs, "the pawn's available melee verbs")
	return verbs, true, err
}

// meleeWeightSum is the total selection weight of the melee verbs.
func meleeWeightSum(verbs []MeleeVerbEntry) float32 {
	var num float32
	for _, v := range verbs {
		if v.IsMeleeAttack {
			num = float32(num + v.SelectionWeight)
		}
	}
	return num
}

// pawnMeleeAverage is the weighted average of one figure of the pawn's melee
// verbs, or empty when the pawn has none (or their weights sum to 0).
func pawnMeleeAverage(verbs []MeleeVerbEntry, empty float32, figure func(MeleeVerbEntry) float32) float32 {
	if len(verbs) == 0 {
		return empty
	}
	num := meleeWeightSum(verbs)
	if num == 0 {
		return empty
	}
	var out float32
	for _, v := range verbs {
		if v.IsMeleeAttack {
			out = float32(float64(out) + float64(v.SelectionWeight)/float64(num)*float64(figure(v)))
		}
	}
	return out
}

// --- StatWorker_MeleeDPS ---

// workerMeleeDPS is StatWorker_MeleeDPS: a pawn's average melee damage times
// its hit chance over its average cooldown; shown for pawns only. IsDisabledFor
// only feeds the game's dev-mode log.
type workerMeleeDPS struct{}

func (workerMeleeDPS) Class() string { return "StatWorker_MeleeDPS" }

func (workerMeleeDPS) Unfinalized(req *Request) (float32, error) {
	e := req.Evaluator
	verbs, _, err := pawnMeleeVerbs(req)
	if err != nil {
		return 0, err
	}
	damage := pawnMeleeAverage(verbs, 0, func(v MeleeVerbEntry) float32 { return v.Damage })
	var hit float32
	if ctx := req.Subject.Context; ctx != nil {
		if hit, err = ctx.Gear.stat(statMeleeHitChance); err != nil {
			return 0, err
		}
	} else if hit, err = e.Value(statMeleeHitChance, Subject{Def: req.Subject.Def, Terrain: req.Subject.Terrain, Stuff: req.Subject.Stuff}); err != nil {
		return 0, err
	}
	cooldown := float32(pawnMeleeAverage(verbs, 0, func(v MeleeVerbEntry) float32 { return float32(v.CooldownTicks) }))
	if len(verbs) == 0 || meleeWeightSum(verbs) == 0 {
		cooldown = 1
	} else {
		cooldown = float32(cooldown / ticksPerSecond)
	}
	return float32(float32(damage*hit) / cooldown), nil
}

func (workerMeleeDPS) Show(req *Request, base func() (bool, error)) (bool, error) {
	ok, err := base()
	if err != nil || !ok {
		return false, err
	}
	return req.pawn() != nil, nil
}

// --- StatWorker_MeleeArmorPenetration ---

// workerMeleeArmorPenetration is StatWorker_MeleeArmorPenetration: a pawn's
// average melee armor penetration; shown for pawns only.
type workerMeleeArmorPenetration struct{}

func (workerMeleeArmorPenetration) Class() string { return "StatWorker_MeleeArmorPenetration" }

func (workerMeleeArmorPenetration) Unfinalized(req *Request) (float32, error) {
	verbs, _, err := pawnMeleeVerbs(req)
	if err != nil {
		return 0, err
	}
	return pawnMeleeAverage(verbs, 0, func(v MeleeVerbEntry) float32 { return v.ArmorPenetration }), nil
}

func (workerMeleeArmorPenetration) Show(req *Request, base func() (bool, error)) (bool, error) {
	ok, err := base()
	if err != nil || !ok {
		return false, err
	}
	return req.pawn() != nil, nil
}

// --- verbs of a weapon def ---

// verbSource is a VerbUtility.VerbPropertiesWithSource.
type verbSource struct {
	verb *d.VerbProperties
	tool *d.Tool
}

// maneuvers are the ManeuverDefs by name, the order the mirror carries (the
// catalog keeps no load order).
func (e *Evaluator) maneuvers() []*d.ManeuverDef {
	rows := e.catalog.Defs[(&d.ManeuverDef{}).ProtoReflect().Descriptor().FullName()]
	out := make([]*d.ManeuverDef, 0, len(rows))
	for _, row := range rows {
		if m, ok := row.(*d.ManeuverDef); ok {
			out = append(out, m)
		}
	}
	sort.Slice(out, func(i, j int) bool { return out[i].GetDefName() < out[j].GetDefName() })
	return out
}

// meleeSources are VerbUtility.GetAllVerbProperties(verbs, tools) restricted
// to the melee attacks: the verbs, then each tool's maneuvers' verbs.
func (e *Evaluator) meleeSources(verbs []*d.Opt_VerbProperties, tools []*d.Opt_Tool) ([]verbSource, error) {
	var all []verbSource
	for _, v := range verbs {
		all = append(all, verbSource{verb: v.GetValue()})
	}
	for _, t := range tools {
		tool := t.GetValue()
		for _, m := range e.maneuvers() {
			if slices.Contains(tool.GetCapacities(), m.GetRequiredCapacity()) {
				all = append(all, verbSource{verb: m.GetVerb(), tool: tool})
			}
		}
	}
	var out []verbSource
	for _, s := range all {
		melee, err := e.classIsA(s.verb.GetVerbClass(), classMeleeAttackVerb)
		if err != nil {
			return nil, err
		}
		if melee {
			out = append(out, s)
		}
	}
	return out, nil
}

// weaponTarget is what the average workers measure: a def with stuff, or a
// thing with its wielder.
type weaponTarget struct {
	req *Request
	// thing is the thing request's gear facts, nil for a definition request.
	thing *GearFacts
	// wielder is the pawn holding the thing, nil for none (or a definition).
	wielder *WielderState
}

// weaponTargetOf resolves the request's target; the wielder of a thing is
// StatWorker_MeleeAverageDPS.GetCurrentWeaponUser.
func weaponTargetOf(req *Request) (weaponTarget, error) {
	t := weaponTarget{req: req}
	if ctx := req.Subject.Context; ctx != nil {
		t.thing = &ctx.Gear
		w, err := need(ctx.Gear.Wielder, "the pawn holding the weapon")
		if err != nil {
			return t, err
		}
		t.wielder = w
	}
	return t, nil
}

// equipmentFactor is the equipment's stat: the thing's own, else the def's
// with its stuff.
func (t weaponTarget) equipmentStat(stat string) (float32, error) {
	if t.thing != nil {
		return t.thing.stat(stat)
	}
	return t.req.Evaluator.Value(stat, Subject{Def: t.req.Subject.Def, Stuff: t.req.Subject.Stuff})
}

// damage is VerbProperties.AdjustedMeleeDamageAmount.
func (t weaponTarget) damage(s verbSource) (float32, error) {
	e := t.req.Evaluator
	var num float32
	if s.tool != nil {
		num = s.tool.GetPower()
		// A thing multiplies in its damage multiplier always; a def only when it
		// has stuff, as Tool.AdjustedBaseMeleeDamageAmount does.
		if t.thing != nil || t.req.Subject.Stuff != "" {
			mult, err := t.equipmentStat(statMeleeWeaponDamageMult)
			if err != nil {
				return 0, err
			}
			num = float32(num * mult)
			if damageDef := s.verb.GetMeleeDamageDef(); t.req.Subject.Stuff != "" && damageDef != "" {
				row := bridge.DefRow[*d.DamageDef](e.catalog, damageDef)
				if row == nil {
					return 0, fmt.Errorf("catalog has no damage def %s", damageDef)
				}
				if row.GetArmorCategory() == "" {
					return 0, fmt.Errorf("damage def %s has no armorCategory: AdjustedBaseMeleeDamageAmount throws", damageDef)
				}
				category := bridge.DefRow[*d.DamageArmorCategoryDef](e.catalog, row.GetArmorCategory())
				if category == nil {
					return 0, fmt.Errorf("catalog has no damage armor category %s", row.GetArmorCategory())
				}
				factor, err := e.Value(category.GetMultStat(), ThingSubject(t.req.Subject.Stuff, ""))
				if err != nil {
					return 0, err
				}
				num = float32(num * factor)
			}
		}
	} else {
		num = float32(s.verb.GetMeleeDamageBaseAmount())
	}
	if t.wielder != nil {
		factor, err := t.damageFactor(s)
		if err != nil {
			return 0, err
		}
		num = float32(num * factor)
	}
	return num, nil
}

// damageFactor is VerbProperties.GetDamageFactorFor of a melee verb: the
// wielder's linked body part efficiency, life stage and stat factors.
func (t weaponTarget) damageFactor(s verbSource) (float32, error) {
	w := t.wielder
	num := float32(1)
	group := s.verb.GetLinkedBodyPartsGroup()
	ensure := s.verb.GetEnsureLinkedBodyPartsGroupAlwaysUsable()
	if s.tool != nil {
		group = s.tool.GetLinkedBodyPartsGroup()
		ensure = s.tool.GetEnsureLinkedBodyPartsGroupAlwaysUsable()
	}
	if group != "" {
		eff, ok := w.PartEfficiency[group]
		if !ok {
			return 0, fmt.Errorf("stat evaluation needs the wielder's %s part efficiency: not observed", group)
		}
		if ensure {
			eff = max32(eff, minEfficiencyAlwaysUsable)
		}
		num = float32(num * eff)
	}
	num = float32(num * w.LifeStageMeleeDamageFactor)
	factor, err := w.stat(statMeleeDamageFactor)
	if err != nil {
		return 0, err
	}
	return float32(num * factor), nil
}

// armorPenetration is VerbProperties.AdjustedArmorPenetration.
func (t weaponTarget) armorPenetration(s verbSource) (float32, error) {
	num := s.verb.GetMeleeArmorPenetrationBase()
	if s.tool != nil {
		num = s.tool.GetArmorPenetration()
	}
	if num < 0 {
		dmg, err := t.damage(s)
		if err != nil {
			return 0, err
		}
		return float32(dmg * meleeAPPerDamage), nil
	}
	mult, err := t.equipmentStat(statMeleeWeaponDamageMult)
	if err != nil {
		return 0, err
	}
	return float32(num * mult), nil
}

// weight is VerbProperties.AdjustedMeleeSelectionWeight of a verb that does
// not come from a pawn's native verbs.
func (t weaponTarget) weight(s verbSource) (float32, error) {
	if t.wielder != nil && int32(t.wielder.Intelligence) < int32(s.verb.GetMinIntelligence()) {
		return 0, nil
	}
	num := float32(1)
	dmg, err := t.damage(s)
	if err != nil {
		return 0, err
	}
	apply, err := t.req.Evaluator.classIsA(s.verb.GetVerbClass(), classVerbMeleeApplyHediff)
	if err != nil {
		return 0, err
	}
	if dmg >= minWeightDamage || !apply {
		num = float32(num * float32(dmg*dmg))
	}
	num = float32(num * s.verb.GetCommonality())
	if s.tool != nil {
		num = float32(num * s.tool.GetChanceFactor())
	}
	return num, nil
}

// cooldown is VerbProperties.AdjustedCooldown of a melee verb.
func (t weaponTarget) cooldown(s verbSource) (float32, error) {
	num := s.verb.GetDefaultCooldownTime()
	if s.tool != nil {
		mult, err := t.equipmentStat(statMeleeWeaponCooldownMult)
		if err != nil {
			return 0, err
		}
		num = float32(s.tool.GetCooldownTime() * mult)
	}
	if t.wielder != nil {
		factor, err := t.wielder.stat(statMeleeCooldownFactor)
		if err != nil {
			return 0, err
		}
		num = float32(num * factor)
	}
	return num, nil
}

// averageWeighted is GenCollection.AverageWeighted over the sources: the sum
// of value times weight over the sum of weights, NaN when there are none.
func (t weaponTarget) averageWeighted(sources []verbSource, value func(verbSource) (float32, error)) (float32, error) {
	var num, num2 float32
	for _, s := range sources {
		w, err := t.weight(s)
		if err != nil {
			return 0, err
		}
		num = float32(num + w)
		v, err := value(s)
		if err != nil {
			return 0, err
		}
		num2 = addProd(num2, v, w)
	}
	return float32(num2 / num), nil
}

// --- StatWorker_MeleeAverageDPS ---

// workerMeleeAverageDPS is StatWorker_MeleeAverageDPS: a weapon def's (or
// thing's) average melee damage over its average cooldown.
type workerMeleeAverageDPS struct{}

func (workerMeleeAverageDPS) Class() string { return "StatWorker_MeleeAverageDPS" }

// verbsAndTools is GetVerbsAndTools: the def's verbs and tools, or a tech
// hediff's hediff's verb giver's.
func (e *Evaluator) verbsAndTools(def *d.ThingDef) ([]*d.Opt_VerbProperties, []*d.Opt_Tool, error) {
	verbs, tools := def.GetVerbs(), def.GetTools()
	if !def.GetIsTechHediff() {
		return verbs, tools, nil
	}
	hediff, err := e.techHediff(def)
	if err != nil || hediff == nil {
		return verbs, tools, err
	}
	for _, comp := range hediff.GetComps() {
		if giver := comp.GetValue().GetHediffCompProperties_VerbGiver(); giver != nil {
			return giver.GetVerbs(), giver.GetTools(), nil
		}
	}
	return verbs, tools, nil
}

// techHediff is FindTechHediffHediff: the hediff the first recipe adding one
// that takes the tech hediff as an ingredient adds.
func (e *Evaluator) techHediff(tech *d.ThingDef) (*d.HediffDef, error) {
	for _, recipe := range e.recipes() {
		if recipe.GetAddsHediff() == "" {
			continue
		}
		ingredient, err := e.recipeTakes(recipe, tech.GetDefName())
		if err != nil {
			return nil, err
		}
		if !ingredient {
			continue
		}
		hediff := bridge.DefRow[*d.HediffDef](e.catalog, recipe.GetAddsHediff())
		if hediff == nil {
			return nil, fmt.Errorf("catalog has no hediff def %s", recipe.GetAddsHediff())
		}
		return hediff, nil
	}
	return nil, nil
}

// recipeTakes is RecipeDef.IsIngredient.
func (e *Evaluator) recipeTakes(recipe *d.RecipeDef, def string) (bool, error) {
	for _, slot := range recipe.GetIngredients() {
		filter := slot.GetValue().GetFilter()
		allows, err := e.catalog.FilterAccepts(filter, def)
		if err != nil {
			return false, fmt.Errorf("recipe %s: %w", recipe.GetDefName(), err)
		}
		if !allows {
			continue
		}
		fixed, err := e.fixedFilter(filter)
		if err != nil {
			return false, err
		}
		if fixed {
			return true, nil
		}
		allows, err = e.catalog.FilterAccepts(recipe.GetFixedIngredientFilter(), def)
		if err != nil {
			return false, fmt.Errorf("recipe %s: %w", recipe.GetDefName(), err)
		}
		if allows {
			return true, nil
		}
	}
	return false, nil
}

// fixedFilter is IngredientCount.IsFixedIngredient: the filter allows exactly
// one def.
func (e *Evaluator) fixedFilter(filter *d.ThingFilter) (bool, error) {
	count := 0
	for name := range e.catalog.ThingDefs {
		ok, err := e.catalog.FilterAccepts(filter, name)
		if err != nil {
			return false, err
		}
		if ok {
			if count++; count > 1 {
				return false, nil
			}
		}
	}
	return count == 1, nil
}

func (workerMeleeAverageDPS) Unfinalized(req *Request) (float32, error) {
	e := req.Evaluator
	if req.Thing == nil {
		return 0, nil
	}
	verbs, tools, err := e.verbsAndTools(req.Thing)
	if err != nil {
		return 0, err
	}
	sources, err := e.meleeSources(verbs, tools)
	if err != nil {
		return 0, err
	}
	t, err := weaponTargetOf(req)
	if err != nil {
		return 0, err
	}
	damage, err := t.averageWeighted(sources, t.damage)
	if err != nil {
		return 0, err
	}
	cooldown, err := t.averageWeighted(sources, t.cooldown)
	if err != nil {
		return 0, err
	}
	if cooldown == 0 {
		return 0, nil
	}
	return float32(damage / cooldown), nil
}

func (workerMeleeAverageDPS) Show(req *Request, _ func() (bool, error)) (bool, error) {
	e := req.Evaluator
	if req.Thing == nil {
		return false, nil
	}
	weapon, err := e.isWeapon(req.Thing)
	if err != nil || (!weapon && !req.Thing.GetIsTechHediff()) {
		return false, err
	}
	verbs, tools, err := e.verbsAndTools(req.Thing)
	if err != nil {
		return false, err
	}
	if len(tools) > 0 {
		return true, nil
	}
	for _, v := range verbs {
		melee, err := e.classIsA(v.GetValue().GetVerbClass(), classMeleeAttackVerb)
		if err != nil || melee {
			return melee, err
		}
	}
	return false, nil
}

// isWeapon is ThingDef.IsWeapon.
func (e *Evaluator) isWeapon(def *d.ThingDef) (bool, error) {
	return def.GetCategory() == d.ThingCategory_THING_CATEGORY_ITEM &&
		(len(def.GetVerbs()) > 0 || len(def.GetTools()) > 0) && def.GetApparel() == nil, nil
}

// --- StatWorker_MeleeAverageArmorPenetration ---

// workerMeleeAverageArmorPenetration is StatWorker_MeleeAverageArmorPenetration:
// a weapon def's (or thing's) average melee armor penetration.
type workerMeleeAverageArmorPenetration struct{}

func (workerMeleeAverageArmorPenetration) Class() string {
	return "StatWorker_MeleeAverageArmorPenetration"
}

func (workerMeleeAverageArmorPenetration) Unfinalized(req *Request) (float32, error) {
	e := req.Evaluator
	if req.Thing == nil {
		return 0, nil
	}
	sources, err := e.meleeSources(req.Thing.GetVerbs(), req.Thing.GetTools())
	if err != nil {
		return 0, err
	}
	t, err := weaponTargetOf(req)
	if err != nil {
		return 0, err
	}
	return t.averageWeighted(sources, t.armorPenetration)
}

func (workerMeleeAverageArmorPenetration) Show(req *Request, _ func() (bool, error)) (bool, error) {
	if req.Thing == nil {
		return false, nil
	}
	weapon, err := req.Evaluator.isWeapon(req.Thing)
	if err != nil || !weapon {
		return false, err
	}
	return len(req.Thing.GetTools()) > 0, nil
}
