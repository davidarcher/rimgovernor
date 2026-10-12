package simimport

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/bridge/stateval"
	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// Pawn stat names.
const (
	statMoveSpeed       = "MoveSpeed"
	statPainShock       = "PainShockThreshold"
	statIncomingDamage  = "IncomingDamageFactor"
	statShootAccuracy   = "ShootingAccuracyPawn"
	statAimingDelay     = "AimingDelayFactor"
	statRangedCooldown  = "RangedCooldownFactor"
	statShootingTouch   = "ShootingAccuracyFactor_Touch"
	statShootingShort   = "ShootingAccuracyFactor_Short"
	statShootingMedium  = "ShootingAccuracyFactor_Medium"
	statShootingLong    = "ShootingAccuracyFactor_Long"
	skillMelee          = "Melee"
	skillShooting       = "Shooting"
	developmentalAdult  = 8 // DevelopmentalStage.Adult
	lethalSeverityScale = "HealthTuning.BaseTotalDamageLethalThreshold"
)

// race is a pawn kind's race resolved to the adult the Spec describes.
type race struct {
	name       string
	def        *d.ThingDef
	lifeStage  string
	ageTicks   int64
	bodySize   float64
	healthSize float64
}

func (im *importer) race(kind string) (race, error) {
	var r race
	kd := bridge.DefRow[*d.PawnKindDef](im.cat, kind)
	if kd == nil {
		return r, unsupported("catalog has no pawn kind %q", kind)
	}
	r.name = kd.GetRace()
	r.def = im.cat.ThingDef(r.name)
	props := r.def.GetRace()
	if props == nil || props.GetIntelligence() != d.Intelligence_INTELLIGENCE_HUMANLIKE {
		return r, unsupported("pawn kind %q race %q is not a humanlike race, which is all the sim's pawn baseline covers", kind, r.name)
	}
	stages := props.GetLifeStageAges()
	if len(stages) == 0 {
		return r, unsupported("race %q has no life stages", r.name)
	}
	adult := stages[len(stages)-1].GetValue()
	stage := bridge.DefRow[*d.LifeStageDef](im.cat, adult.GetDef())
	if stage == nil {
		return r, unsupported("catalog has no life stage %q", adult.GetDef())
	}
	r.lifeStage = adult.GetDef()
	r.ageTicks = int64(float64(adult.GetMinAge()) * float64(domain.TicksPerYear))
	r.bodySize = float64(props.GetBaseBodySize()) * float64(stage.GetBodySizeFactor())
	r.healthSize = float64(props.GetBaseHealthScale()) * float64(stage.GetHealthScaleFactor())
	return r, nil
}

// baselineAdult is the stat context of a healthy adult with no hediffs and
// normal needs, carrying only the given traits: the state a lab-staged pawn is
// in (A28). Skills are the Spec's; every pawn capacity is at full level.
func (im *importer) baselineAdult(r race, kind string, skills map[string]int32, traits []Trait) (*stateval.StatContext, error) {
	capacities := map[string]float32{}
	for name := range im.cat.Rows((&d.PawnCapacityDef{}).ProtoReflect().Descriptor().FullName()) {
		capacities[name] = 1
	}
	if len(capacities) == 0 {
		return nil, unsupported("catalog has no pawn capacity defs")
	}
	some := func(b bool) stateval.Known[bool] { return stateval.Some(b) }
	p := &stateval.PawnState{
		Age:    stateval.Some(stateval.AgeState{Tracked: true, BiologicalTicks: r.ageTicks}),
		Female: some(false), IsSlave: some(false), IsWildMan: some(false), Deathresting: some(false),
		Shambler: some(false), Blind: some(false), IdeoPrefersDarkness: some(false), GenesAffectedByDarkness: some(false),
		Resting:          stateval.Some(stateval.RestingState{}),
		PainTotal:        stateval.Some(float32(0)),
		Mood:             stateval.Some(stateval.MoodState{}),
		Rest:             stateval.Some(stateval.NeedState{}),
		Food:             stateval.Some(stateval.NeedState{}),
		Malnutrition:     stateval.Some(stateval.MalnutritionState{}),
		FertilityFactors: stateval.Some([]float32{}),
	}
	p.PawnStat.IsMechanitor, p.PawnStat.IsAnimal = some(false), some(false)
	p.PawnStat.MutantDef = stateval.Some("")
	p.Body.CurLifeStage = stateval.Some(r.lifeStage)
	p.Body.Hediffs = stateval.Some([]stateval.HediffState{})
	p.Body.Genes = stateval.Some(stateval.GenesState{})
	p.Body.Ideo = stateval.Some(stateval.IdeoState{})
	p.Body.NaturalCoverage = stateval.Some(float32(1))
	p.Body.Subhuman, p.Body.Crawling, p.Body.DigLearned, p.Body.PlayerFactionLeader = some(false), some(false), some(false), some(false)
	p.Body.SightSourcesAllMissing = some(false)
	p.Body.SightEfficiency = stateval.Some(float32(1))
	p.Body.Terror = stateval.Some(float32(0))
	p.Body.TicksGame = stateval.Some(int32(0))
	p.Body.Overseer = stateval.Some(stateval.OverseerState{})
	p.Body.Bed = stateval.Some(stateval.BedState{Kind: stateval.BedNone})
	p.Body.Revenant = stateval.Some(stateval.RevenantState{KindDef: kind})
	p.Base.Skills = stateval.Some(stateval.SkillsState{Present: true, Levels: skills})
	p.Base.Capacities = stateval.Some(capacities)
	story := stateval.StoryState{Present: true}
	for _, t := range traits {
		story.Traits = append(story.Traits, stateval.TraitState{Def: t.Def, Degree: t.Degree})
	}
	p.Base.Story = stateval.Some(story)
	p.Base.Apparel = stateval.Some(stateval.WornGear{Present: true})
	p.Base.Primary = stateval.Some[*stateval.GearPiece](nil)
	p.Base.Inspiration = stateval.Some("")
	p.Base.IsColonyMech = some(false)
	p.Base.KindDef = stateval.Some(kind)
	p.Base.Developmental = stateval.Some(int32(developmentalAdult))
	return &stateval.StatContext{Pawn: p, Spawned: some(true), Roofed: some(false)}, nil
}

// deathOnDowned ports the chance Pawn_HealthTracker.CheckForStateChange rolls
// when a non-player, non-prisoner humanlike becomes downable from violence: the
// pawn kind's forceDeathOnDowned (always) or overrideDeathOnDownedChance, else
// the HealthTuning population-intent curve (the wavering-prisoners one unless
// the difficulty is unwavering) times the difficulty's
// enemyDeathOnDownedChanceFactor. The returned string is the trace source.
func (im *importer) deathOnDowned(spec Spec, kind string) (float64, string, error) {
	kd := bridge.DefRow[*d.PawnKindDef](im.cat, kind)
	if kd == nil {
		return 0, "", unsupported("catalog has no pawn kind %q", kind)
	}
	if kd.GetForceDeathOnDowned() {
		return 1, "PawnKindDef " + kind + " forceDeathOnDowned", nil
	}
	if kd.OverrideDeathOnDownedChance != nil {
		return float64(kd.GetOverrideDeathOnDownedChance()), "PawnKindDef " + kind + " overrideDeathOnDownedChance", nil
	}
	in, ok := spec.DeathOnDowned.Value()
	if !ok {
		return 0, "", unsupported("death-on-downed inputs (population intent, difficulty) unknown")
	}
	if in.EnemyDeathOnDownedChanceFactor < 0 {
		return 0, "", unsupported("enemyDeathOnDownedChanceFactor %v is negative", in.EnemyDeathOnDownedChanceFactor)
	}
	constants, err := im.cat.GameConstants()
	if err != nil {
		return 0, "", unsupported("game constants: %v", err)
	}
	tuning := constants.GetHealthTuning()
	curve, name := tuning.GetDeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve_WaveringPrisoners(), "..._WaveringPrisoners"
	if in.UnwaveringPrisoners {
		curve, name = tuning.GetDeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve(), ""
	}
	v, err := stateval.EvaluateCurve(curve, float32(in.PopulationIntent))
	if err != nil {
		return 0, "", unsupported("HealthTuning death-on-downed curve: %v", err)
	}
	chance := min(float64(v)*in.EnemyDeathOnDownedChanceFactor, 1)
	return chance, "HealthTuning.DeathOnDownedChance_NonColonyHumanlikeFromPopulationIntentCurve" + name + " at population intent * difficulty enemyDeathOnDownedChanceFactor", nil
}

// traitSource names a pawn's traits for a trace source, "" for none.
func traitSource(traits []Trait) string {
	if len(traits) == 0 {
		return ""
	}
	s := " with traits"
	for _, t := range traits {
		s += fmt.Sprintf(" %s:%d", t.Def, t.Degree)
	}
	return s
}

// pawn derives one combatant and the view facts about it.
func (im *importer) pawn(spec Spec, p Pawn, w weapon) (combatsim.PawnSpec, combatsim.PawnFacts, error) {
	id := string(p.ID)
	ps := combatsim.PawnSpec{ID: p.ID, Pos: p.Cell, Side: p.Side}
	var pf combatsim.PawnFacts
	if p.Side != combatmetrics.Colonist && p.Side != combatmetrics.Hostile {
		return ps, pf, unsupported("pawn %q side %q", id, p.Side)
	}
	if p.Skills == nil {
		return ps, pf, unsupported("pawn %q skills unknown", id)
	}
	worn, ok := p.Worn.Value()
	if !ok {
		return ps, pf, unsupported("pawn %q worn apparel unknown", id)
	}
	r, err := im.race(p.Kind)
	if err != nil {
		return ps, pf, err
	}
	ctx, err := im.baselineAdult(r, p.Kind, p.Skills, p.Traits)
	if err != nil {
		return ps, pf, err
	}
	stat := func(name string) (float64, error) {
		v, err := im.ev.Value(name, stateval.Subject{Def: r.name, Context: ctx})
		if err != nil {
			return 0, unsupported("pawn %q stat %s: %v", id, name, err)
		}
		return float64(v), nil
	}

	speed, err := stat(statMoveSpeed)
	if err != nil {
		return ps, pf, err
	}
	ps.MoveSpeed = domain.Known(speed)
	im.trace(id, "MoveSpeed", "stat "+statMoveSpeed+" of race "+r.name+traitSource(p.Traits))

	shock, err := stat(statPainShock)
	if err != nil {
		return ps, pf, err
	}
	incoming, err := stat(statIncomingDamage)
	if err != nil {
		return ps, pf, err
	}
	if p.Side == combatmetrics.Colonist {
		// A player-faction pawn's would-be instant kill is turned into a
		// survivable injury below this chance; the per-part model has no such
		// protection (A39).
		chance, ok := spec.AllowInstantKillChance.Value()
		if !ok {
			return ps, pf, unsupported("pawn %q: the difficulty's allowInstantKillChance is unknown", id)
		}
		if chance < 1 {
			return ps, pf, unsupported("pawn %q: allowInstantKillChance %v protects colonists from instant kills, which the sim does not model", id, chance)
		}
	}
	kd := bridge.DefRow[*d.PawnKindDef](im.cat, p.Kind)
	body, err := im.body(r.def.GetRace(), r.healthSize, shock, incoming, kd.GetIgnoresPainShock())
	if err != nil {
		return ps, pf, err
	}
	cs := &combatsim.CombatSpec{
		BodySize:            domain.Known(r.bodySize),
		CanBenefitFromCover: r.def.GetCategory() == d.ThingCategory_THING_CATEGORY_PAWN,
		Body:                body,
	}
	if p.Side == combatmetrics.Hostile {
		chance, source, err := im.deathOnDowned(spec, p.Kind)
		if err != nil {
			return ps, pf, err
		}
		cs.DeathOnDownedChance = chance
		im.trace(id, "DeathOnDownedChance", source)
	} else {
		im.trace(id, "DeathOnDownedChance", "Pawn_HealthTracker.CheckForStateChange: a player-faction pawn is never rolled")
	}
	im.trace(id, "BodySize", "race "+r.name+" baseBodySize * LifeStageDef "+r.lifeStage+" bodySizeFactor")
	im.trace(id, "CanBenefitFromCover", "ThingDef "+r.name+" category")
	im.trace(id, "Body", "BodyDef "+r.def.GetRace().GetBody()+" parts, coverage, depth and tags, BodyPartDef hitPoints, hitChanceFactors, solid and destroyableByDamage * healthScale of race "+r.name+
		"; DamageDef "+im.dmg.def+" harmAllLayersUntilOutside and overkillPctToDestroyPart; HediffDef "+im.dmg.hediff+" and "+missingPartHediff+" injuryProps.painPerSeverity; PawnCapacityDef workers; stat "+
		statPainShock+" and "+statIncomingDamage+"; "+lethalSeverityScale+" * healthScale")

	// Armor: the worn apparel's ratings outermost first, then the natural one
	// (A26). No armor category on the damage means nothing is rated.
	layers := []float64{}
	if im.dmg.armorStat != "" {
		for _, wear := range worn {
			v, err := im.ev.Value(im.dmg.armorStat, stateval.ThingSubject(wear.Def, wear.Stuff))
			if err != nil {
				return ps, pf, unsupported("pawn %q apparel %q stat %s: %v", id, wear.Def, im.dmg.armorStat, err)
			}
			layers = append(layers, float64(v))
			im.trace(id, "Armor."+wear.Def, "stat "+im.dmg.armorStat+" of ThingDef "+wear.Def)
		}
		natural, err := stat(im.dmg.armorStat)
		if err != nil {
			return ps, pf, err
		}
		layers = append(layers, natural)
		im.trace(id, "Armor.natural", "stat "+im.dmg.armorStat+" of race "+r.name)
	}
	cs.Armor = domain.Known(layers)
	composite := 1.0
	for _, rating := range layers {
		composite *= 1 - rating
	}
	pf.Armor = domain.Known(1 - composite)
	im.trace(id, "Facts.Armor", "armor layers combined as 1 - product(1 - rating)")

	if w.ranged != nil {
		cs.Weapon = w.ranged
		for _, s := range []struct {
			dst  *domain.Fact[float64]
			stat string
		}{
			{&cs.Shooter.ShootingAccuracyPawn, statShootAccuracy}, {&cs.Shooter.AccuracyTouch, statShootingTouch},
			{&cs.Shooter.AccuracyShort, statShootingShort}, {&cs.Shooter.AccuracyMedium, statShootingMedium},
			{&cs.Shooter.AccuracyLong, statShootingLong}, {&cs.Shooter.AimingDelayFactor, statAimingDelay},
			{&cs.Shooter.RangedCooldownFactor, statRangedCooldown},
		} {
			v, err := stat(s.stat)
			if err != nil {
				return ps, pf, err
			}
			*s.dst = domain.Known(v)
			im.trace(id, "Shooter."+s.stat, "stat "+s.stat+" of race "+r.name)
		}
	}
	ps.Combat = cs

	// Melee power: the catalog's melee DPS of the wielded weapon, else of the
	// race's natural tools, at full health (A29).
	source := "natural tools of race " + r.name
	meleeDPS := 0.0
	if w.melee {
		source = "ThingDef " + p.Weapon + " tools"
		meleeDPS = w.meleeDPS
	} else {
		natural, err := im.cat.WeaponOf(r.name)
		if err != nil {
			return ps, pf, unsupported("pawn %q natural melee: %v", id, err)
		}
		meleeDPS = natural.DPS
	}
	// policy.FrontLine for a pawn with no traits: the melee line is whoever
	// wields no ranged weapon and is at least as good at melee as at shooting.
	melee, hasMelee := p.Skills[skillMelee]
	shooting, hasShooting := p.Skills[skillShooting]
	if !hasMelee || !hasShooting {
		return ps, pf, unsupported("pawn %q has no %s or %s skill level for the front line split", id, skillMelee, skillShooting)
	}
	im.trace(id, "Facts.FrontLine", "no ranged weapon and skill "+skillMelee+" >= "+skillShooting)
	pf = combatsim.PawnFacts{
		Kind: p.Kind, FrontLine: w.ranged == nil && melee >= shooting, Armor: pf.Armor, MeleePower: domain.Known(meleeDPS),
		LordJobClass: p.LordJobClass, LordToilClass: p.LordToilClass,
	}
	im.trace(id, "Facts.MeleePower", "bridge WeaponOf DPS of "+source)
	return ps, pf, nil
}
