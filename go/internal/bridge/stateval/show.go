package stateval

import (
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// StatCategoryDefOf names ShouldShowFor branches on.
const (
	catBasicsPawn             = "BasicsPawn"
	catBasicsPawnImportant    = "BasicsPawnImportant"
	catPawnCombat             = "PawnCombat"
	catAnimals                = "Animals"
	catPawnResistances        = "PawnResistances"
	catPawnHealth             = "PawnHealth"
	catPawnFood               = "PawnFood"
	catPawnPsyfocus           = "PawnPsyfocus"
	catPawnMisc               = "PawnMisc"
	catPawnSocial             = "PawnSocial"
	catPawnWork               = "PawnWork"
	catBuilding               = "Building"
	catApparel                = "Apparel"
	catWeapon                 = "Weapon"
	catWeaponRanged           = "Weapon_Ranged"
	catWeaponMelee            = "Weapon_Melee"
	catBasicsNonPawn          = "BasicsNonPawn"
	catBasicsNonPawnImportant = "BasicsNonPawnImportant"
	catTerrain                = "Terrain"
	catPsychicRituals         = "PsychicRituals"

	statDoorOpenSpeed = "DoorOpenSpeed"
	statMarketValue   = "MarketValue"

	modAnomaly = "ludeon.rimworld.anomaly"
	modOdyssey = "ludeon.rimworld.odyssey"

	classBuildingWorkTable = "RimWorld.Building_WorkTable"
	classBuildingDoor      = "RimWorld.Building_Door"
	classCompPowerPlant    = "RimWorld.CompPowerPlant"
)

// shown is ShouldShowFor: the worker's override, else the base worker's.
func (e *Evaluator) shown(req *Request) (bool, error) {
	w, err := e.worker(req.Stat, "the worker's ShouldShowFor")
	if err != nil {
		return false, err
	}
	if sw, ok := w.(showWorker); ok {
		return sw.Show(req, func() (bool, error) { return e.baseShown(req) })
	}
	return e.baseShown(req)
}

// baseShown is the base StatWorker.ShouldShowFor for a definition request, in
// the game's order, so a stat that is hidden before it reaches an unowned
// class is answered without the class.
func (e *Evaluator) baseShown(req *Request) (bool, error) {
	stat := req.Stat
	if stat.GetAlwaysHide() {
		return false, nil
	}
	if !stat.GetShowIfUndefined() && !statListContains(req.statBases(), stat.GetDefName()) {
		return false, nil
	}
	ok, err := e.canShowWithLoadedMods(stat)
	if err != nil || !ok {
		return false, err
	}
	if stat.GetHideInClassicMode() && e.env.ClassicMode {
		return false, nil
	}
	parts, err := e.orderedParts(stat)
	if err != nil {
		return false, err
	}
	for _, rp := range parts {
		part, err := e.part(rp.class, "ForceShow")
		if err != nil {
			return false, err
		}
		force, err := part.ForceShow(req, rp.row)
		if err != nil || force {
			return force, err
		}
	}
	if p := req.pawn(); p != nil {
		if len(stat.GetShowIfHediffsPresent()) > 0 {
			hediffs, err := need(p.Body.Hediffs, "the pawn's hediffs")
			if err != nil {
				return false, err
			}
			for _, want := range stat.GetShowIfHediffsPresent() {
				if !slices.ContainsFunc(hediffs, func(h HediffState) bool { return h.Def == want }) {
					return false, nil
				}
			}
		}
		if stat.GetShowOnSlavesOnly() {
			slave, err := need(p.IsSlave, "whether the pawn is a slave")
			if err != nil || !slave {
				return false, err
			}
		}
	}
	if stat.GetDefName() == statMaxHitPoints && req.ctx() != nil {
		return false, nil
	}
	if !stat.GetShowOnUntradeables() {
		trade, err := e.displayTradeStats(req)
		if err != nil || !trade {
			return false, err
		}
	}
	thing := req.Thing
	if thing != nil {
		if thing.GetCategory() == d.ThingCategory_THING_CATEGORY_PAWN {
			ok, err := e.pawnDefShown(req)
			if err != nil || !ok {
				return false, err
			}
		}
		if !stat.GetShowOnUnhaulables() && !everHaulable(thing) && !minifiable(thing) {
			return false, nil
		}
	}
	return e.shownByCategory(req)
}

// pawnDefShown is the ThingCategory.Pawn block of ShouldShowFor.
func (e *Evaluator) pawnDefShown(req *Request) (bool, error) {
	stat, thing := req.Stat, req.Thing
	if !stat.GetShowOnPawns() {
		return false, nil
	}
	race := thing.GetRace()
	if race == nil {
		return false, fmt.Errorf("pawn def %s has no race", thing.GetDefName())
	}
	humanlike := humanlike(race)
	if !stat.GetShowOnHumanlikes() && humanlike {
		return false, nil
	}
	// Without a pawn (a definition request) a humanlike is never a wild man.
	if !stat.GetShowOnNonWildManHumanlikes() && humanlike {
		wild := false
		if p := req.pawn(); p != nil {
			var err error
			if wild, err = need(p.IsWildMan, "whether the pawn is a wild man"); err != nil {
				return false, err
			}
		}
		if !wild {
			return false, nil
		}
	}
	flesh := fleshType(race)
	anomaly, err := e.anomalyEntity(flesh)
	if err != nil {
		return false, err
	}
	if !stat.GetShowOnAnimals() {
		isAnimal, err := e.animal(race, anomaly)
		if err != nil || isAnimal {
			return false, err
		}
	}
	if !stat.GetShowOnEntities() && anomaly {
		return false, nil
	}
	if !stat.GetShowOnMechanoids() && flesh == fleshMechanoid {
		return false, nil
	}
	if !stat.GetShowOnDrones() {
		odyssey, err := e.modActive(modOdyssey)
		if err != nil {
			return false, err
		}
		if odyssey && flesh == fleshDrone {
			return false, nil
		}
	}
	if p := req.pawn(); p != nil {
		// StatDef.showDevelopmentalStageFilter.Has(pawn.DevelopmentalStage).
		stage, err := need(p.Base.Developmental, "the pawn's developmental stage")
		if err != nil {
			return false, err
		}
		if int32(stat.GetShowDevelopmentalStageFilter())&stage == 0 {
			return false, nil
		}
	}
	return true, nil
}

func (e *Evaluator) shownByCategory(req *Request) (bool, error) {
	stat := req.Stat
	thing := req.Thing
	isPawn := thing != nil && thing.GetCategory() == d.ThingCategory_THING_CATEGORY_PAWN
	category := stat.GetCategory()
	switch category {
	case catBasicsPawn, catBasicsPawnImportant, catPawnCombat, catAnimals, catPawnResistances, catPawnHealth, catPawnFood, catPawnPsyfocus:
		return isPawn, nil
	case catPawnMisc, catPawnSocial, catPawnWork:
		if !isPawn {
			return false, nil
		}
		if p := req.pawn(); p != nil {
			if stat.GetShowOnPlayerMechanoids() {
				mech, err := need(p.Base.IsColonyMech, "whether the pawn is a colony mech")
				if err != nil || mech {
					return mech, err
				}
			}
			if kinds := stat.GetShowOnPawnKind(); len(kinds) > 0 {
				kind, err := need(p.Base.KindDef, "the pawn's kind def")
				if err != nil {
					return false, err
				}
				if slices.Contains(kinds, kind) {
					return true, nil
				}
			}
		}
		return humanlike(thing.GetRace()), nil
	case catBuilding:
		if thing == nil {
			return false, nil
		}
		if stat.GetDefName() == statDoorOpenSpeed {
			return e.classIsA(thing.GetThingClass(), classBuildingDoor)
		}
		if !stat.GetShowOnNonWorkTables() {
			work, err := e.classIsA(thing.GetThingClass(), classBuildingWorkTable)
			if err != nil || !work {
				return false, err
			}
		}
		if !stat.GetShowOnNonPowerPlants() {
			plant, err := e.hasCompFrom(thing, classCompPowerPlant)
			if err != nil || !plant {
				return false, err
			}
		}
		return thing.GetCategory() == d.ThingCategory_THING_CATEGORY_BUILDING, nil
	case catApparel:
		if thing == nil {
			return false, nil
		}
		return thing.GetApparel() != nil || isPawn, nil
	case catWeapon:
		if thing == nil {
			return false, nil
		}
		melee, ranged, err := e.weaponKind(thing)
		return melee || ranged, err
	case catWeaponRanged:
		if thing == nil {
			return false, nil
		}
		_, ranged, err := e.weaponKind(thing)
		return ranged, err
	case catWeaponMelee:
		if thing == nil {
			return false, nil
		}
		melee, _, err := e.weaponKind(thing)
		return melee, err
	case catBasicsNonPawn, catBasicsNonPawnImportant:
		return thing == nil || !isPawn, nil
	case catTerrain:
		return req.Terrain != nil, nil
	}
	if category == catPsychicRituals {
		anomaly, err := e.modActive(modAnomaly)
		if err != nil {
			return false, err
		}
		if anomaly {
			return false, nil
		}
	}
	row := bridge.DefRow[*d.StatCategoryDef](e.catalog, category)
	if row == nil {
		return false, fmt.Errorf("catalog has no stat category %s", category)
	}
	// The game logs an unhandled category and answers false.
	return row.GetDisplayAllByDefault(), nil
}

// weaponKind is ThingDef.IsMeleeWeapon and IsRangedWeapon.
func (e *Evaluator) weaponKind(thing *d.ThingDef) (melee, ranged bool, err error) {
	if !(thing.GetCategory() == d.ThingCategory_THING_CATEGORY_ITEM && (len(thing.GetVerbs()) > 0 || len(thing.GetTools()) > 0) && thing.GetApparel() == nil) {
		return false, false, nil
	}
	for _, v := range thing.GetVerbs() {
		isMelee, err := e.classIsA(v.GetValue().GetVerbClass(), classMeleeAttackVerb)
		if err != nil {
			return false, false, err
		}
		if !isMelee {
			return false, true, nil
		}
	}
	return true, false, nil
}

// displayTradeStats is StatWorker.DisplayTradeStats.
func (e *Evaluator) displayTradeStats(req *Request) (bool, error) {
	thing := req.Thing
	if thing == nil {
		return false, nil
	}
	if c := req.ctx(); c != nil {
		biotech, err := e.modActive(modBiotech)
		if err != nil {
			return false, err
		}
		if p := req.pawn(); biotech && p != nil {
			mech, err := need(p.Base.IsColonyMech, "whether the pawn is a colony mech")
			if err != nil || mech {
				return mech, err
			}
		}
		biocodable, err := e.hasCompFrom(thing, classCompBiocodable)
		if err != nil {
			return false, err
		}
		if biocodable {
			coded, err := need(c.Base.Biocoded, "whether the thing is biocoded")
			if err != nil || coded {
				return false, err
			}
		}
	}
	if thing.GetCategory() == d.ThingCategory_THING_CATEGORY_BUILDING && minifiable(thing) {
		return true, nil
	}
	sellable, err := e.everPlayerSellable(thing)
	if err != nil || sellable {
		return sellable, err
	}
	trader := thing.GetTradeability() == d.Tradeability_TRADEABILITY_ALL || thing.GetTradeability() == d.Tradeability_TRADEABILITY_BUYABLE
	return trader && (thing.GetCategory() == d.ThingCategory_THING_CATEGORY_ITEM || thing.GetCategory() == d.ThingCategory_THING_CATEGORY_PAWN), nil
}

// everPlayerSellable is TradeUtility.EverPlayerSellable. It reads the def's
// MarketValue; a failure evaluating it (an unowned class, an unstated fact) is
// returned, never read as unsellable.
func (e *Evaluator) everPlayerSellable(thing *d.ThingDef) (bool, error) {
	if t := thing.GetTradeability(); t != d.Tradeability_TRADEABILITY_ALL && t != d.Tradeability_TRADEABILITY_SELLABLE {
		return false, nil
	}
	value, err := e.Value(statMarketValue, ThingSubject(thing.GetDefName(), ""))
	if err != nil {
		return false, fmt.Errorf("MarketValue of %s: %w", thing.GetDefName(), err)
	}
	if value <= 0 {
		return false, nil
	}
	switch thing.GetCategory() {
	case d.ThingCategory_THING_CATEGORY_ITEM, d.ThingCategory_THING_CATEGORY_PAWN:
		return true, nil
	case d.ThingCategory_THING_CATEGORY_BUILDING:
		return minifiable(thing), nil
	}
	return false, nil
}

// canShowWithLoadedMods is StatDef.CanShowWithLoadedMods.
func (e *Evaluator) canShowWithLoadedMods(stat *d.StatDef) (bool, error) {
	for _, mod := range stat.GetShowIfModsLoaded() {
		active, err := e.modActive(mod)
		if err != nil || !active {
			return false, err
		}
	}
	if len(stat.GetShowIfModsLoadedAny()) == 0 {
		return true, nil
	}
	for _, mod := range stat.GetShowIfModsLoadedAny() {
		active, err := e.modActive(mod)
		if err != nil {
			return false, err
		}
		if active {
			return true, nil
		}
	}
	return false, nil
}

func statListContains(list []*d.Opt_StatModifier, stat string) bool {
	return slices.ContainsFunc(list, func(m *d.Opt_StatModifier) bool { return m.GetValue().GetStat() == stat })
}
