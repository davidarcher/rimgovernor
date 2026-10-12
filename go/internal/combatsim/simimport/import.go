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

// importer carries one Import's catalog readers and its growing Result.
type importer struct {
	cat *bridge.DefinitionCatalog
	ev  *stateval.Evaluator
	res *Result
	// dmg is the one ranged damage def every weapon in the Spec deals; the
	// injury and armor derivations read it (A25, A26).
	dmg *damage
	// weatherCap is the weather def's maxRangeCap, negative for none.
	weatherCap float32
}

func unsupported(format string, a ...any) error {
	return fmt.Errorf("%w: %s", combatsim.ErrUnsupported, fmt.Sprintf(format, a...))
}

func (im *importer) trace(subject, field, source string) {
	im.res.Trace = append(im.res.Trace, Trace{Subject: subject, Field: field, Source: source})
}

// Import builds the Fight for spec from the catalog. It returns an error
// wrapping combatsim.ErrUnsupported when a def, stat or fact the sim needs is
// missing or outside what it models. Fight.Enemy is left for the caller.
func Import(cat *bridge.DefinitionCatalog, spec Spec) (Result, error) {
	var res Result
	if cat == nil {
		return res, unsupported("no definition catalog")
	}
	ev, err := cat.StatEvaluator()
	if err != nil {
		return res, unsupported("catalog has no stat evaluator: %v", err)
	}
	im := &importer{cat: cat, ev: ev, res: &res}
	sc := combatsim.Scenario{Width: spec.Width, Height: spec.Height, StepTicks: spec.StepTicks}
	if spec.Width <= 0 || spec.Height <= 0 || spec.StepTicks <= 0 || spec.MaxTicks <= 0 {
		return res, unsupported("map %dx%d, StepTicks %d and MaxTicks %d must be positive", spec.Width, spec.Height, spec.StepTicks, spec.MaxTicks)
	}
	if err := im.world(spec, &sc); err != nil {
		return res, err
	}
	weapons, err := im.weapons(spec)
	if err != nil {
		return res, err
	}
	facts := map[domain.PawnID]combatsim.PawnFacts{}
	for i, p := range spec.Pawns {
		ps, pf, err := im.pawn(spec, p, weapons[i])
		if err != nil {
			return res, err
		}
		sc.Pawns = append(sc.Pawns, ps)
		facts[p.ID] = pf
	}
	res.Fight = combatsim.Fight{
		Name: spec.Name, Scenario: sc, Layout: spec.Layout, Facts: facts, MaxTicks: spec.MaxTicks,
	}
	if spec.Raid != nil {
		cfg, err := im.raid(spec)
		if err != nil {
			return res, err
		}
		res.Fight.EnemyAI = &cfg
	}
	return res, nil
}

// raid builds the raid lord's EnemyConfig: the give-up range from
// LordJob_AssaultColony.AssaultTimeBeforeGiveUp and the colony health total
// from WealthWatcher.HealthTotal (A32).
func (im *importer) raid(spec Spec) (combatsim.EnemyConfig, error) {
	cfg := combatsim.EnemyConfig{CanTimeoutOrFlee: spec.Raid.CanTimeoutOrFlee, Exit: spec.Raid.Exit, AutoFleeFraction: spec.Raid.AutoFleeFraction, FirstTargets: spec.Raid.FirstTargets}
	if !cfg.CanTimeoutOrFlee {
		return cfg, nil
	}
	constants, err := im.cat.GameConstants()
	if err != nil {
		return cfg, unsupported("game constants: %v", err)
	}
	span := constants.GetLordJob_AssaultColony().GetAssaultTimeBeforeGiveUp()
	if span == nil {
		return cfg, unsupported("catalog has no LordJob_AssaultColony.AssaultTimeBeforeGiveUp")
	}
	cfg.GiveUpMinTicks, cfg.GiveUpMaxTicks = int(span.GetMin()), int(span.GetMax())
	im.trace("raid", "GiveUpTicks", "game constant LordJob_AssaultColony.AssaultTimeBeforeGiveUp")

	// WealthWatcher.HealthTotal: the hit points of every player-owned artificial
	// building plus 100 per free colonist at full health.
	total := 0.0
	for _, p := range spec.Pawns {
		if p.Side == combatmetrics.Colonist {
			total += 100
		}
	}
	for _, t := range spec.Things {
		row := im.cat.ThingDef(t.Def)
		if row.GetCategory() != d.ThingCategory_THING_CATEGORY_BUILDING || row.GetBuilding().GetIsNaturalRock() {
			continue
		}
		hp, err := im.ev.Value(statMaxHitPoints, stateval.ThingSubject(t.Def, ""))
		if err != nil {
			return cfg, unsupported("thing %q stat %s: %v", t.Def, statMaxHitPoints, err)
		}
		total += float64(hp)
	}
	cfg.ColonyHealthTotal = domain.Known(total)
	im.trace("raid", "ColonyHealthTotal", "100 per colonist plus stat "+statMaxHitPoints+" of each artificial building ThingDef")
	return cfg, nil
}

const statMaxHitPoints = "MaxHitPoints"

// world fills the map: terrain palette, thing palette and grid, weather and
// the friendly fire factor.
func (im *importer) world(spec Spec, sc *combatsim.Scenario) error {
	terrain := im.cat.TerrainDef(spec.Terrain)
	if terrain == nil {
		return unsupported("catalog has no terrain def %q", spec.Terrain)
	}
	sc.Terrain = []combatsim.TerrainDef{{
		Name: spec.Terrain, PathCost: int(terrain.GetPathCost()),
		Passable: terrain.GetPassability() != d.Traversability_TRAVERSABILITY_IMPASSABLE,
	}}
	im.trace("map", "Terrain", "TerrainDef "+spec.Terrain+" pathCost, passability")

	n := spec.Width * spec.Height
	index := map[string]uint8{}
	for _, t := range spec.Things {
		c := t.Cell
		if c.X < 0 || c.Z < 0 || int(c.X) >= spec.Width || int(c.Z) >= spec.Height {
			return unsupported("thing %q at %v is off the %dx%d map", t.Def, c, spec.Width, spec.Height)
		}
		id, ok := index[t.Def]
		if !ok {
			def, err := im.thingDef(t.Def)
			if err != nil {
				return err
			}
			if len(sc.Things) >= 255 {
				return unsupported("more than 255 distinct thing defs")
			}
			sc.Things = append(sc.Things, def)
			id = uint8(len(sc.Things))
			index[t.Def] = id
			im.trace("map", "Things."+t.Def, "ThingDef "+t.Def+" fillPercent, passability, pathCost, thingClass")
		}
		if sc.ThingIDs == nil {
			sc.ThingIDs = make([]uint8, n)
		}
		at := int(c.Z)*spec.Width + int(c.X)
		if sc.ThingIDs[at] != 0 {
			return unsupported("two things on cell %v (%q and %q): a cell holds one", c, sc.Things[sc.ThingIDs[at]-1].Name, t.Def)
		}
		sc.ThingIDs[at] = id
	}

	weather := bridge.DefRow[*d.WeatherDef](im.cat, spec.Weather)
	if weather == nil {
		return unsupported("catalog has no weather def %q", spec.Weather)
	}
	im.weatherCap = weather.GetMaxRangeCap()
	if weather.GetMaxRangeCap() >= 0 {
		// A range cap would shorten every weapon; the weapons derivation reads it.
		im.trace("map", "WeatherRangeCap", "WeatherDef "+spec.Weather+" maxRangeCap")
	}
	sc.WeatherMoveSpeed = domain.Known(float64(weather.GetMoveSpeedMultiplier()))
	sc.WeatherAccuracy = domain.Known(float64(weather.GetAccuracyMultiplier()))
	im.trace("map", "WeatherMoveSpeed", "WeatherDef "+spec.Weather+" moveSpeedMultiplier")
	im.trace("map", "WeatherAccuracy", "WeatherDef "+spec.Weather+" accuracyMultiplier")
	if _, ok := spec.FriendlyFireChanceFactor.Value(); !ok {
		return unsupported("friendly fire chance factor unknown")
	}
	sc.FriendlyFireChanceFactor = spec.FriendlyFireChanceFactor
	return nil
}

// fillage ports ThingDef.Fillage: the fill category from fillPercent.
func fillage(percent float32) combatsim.Fill {
	switch {
	case percent < 0.01:
		return combatsim.FillNone
	case percent > 0.99:
		return combatsim.FillFull
	}
	return combatsim.FillPartial
}

// thingDef reads the values the sim wants of a thing def.
func (im *importer) thingDef(name string) (combatsim.ThingDef, error) {
	row := im.cat.ThingDef(name)
	if row == nil {
		return combatsim.ThingDef{}, unsupported("catalog has no thing def %q", name)
	}
	if row.GetBuilding().GetTurretGunDef() != "" {
		return combatsim.ThingDef{}, unsupported("thing %q is a turret, which the sim does not fire", name)
	}
	out := combatsim.ThingDef{
		Name: name, Fillage: fillage(row.GetFillPercent()), FillPercent: float64(row.GetFillPercent()),
		PathCost: int(row.GetPathCost()),
	}
	switch row.GetPassability() {
	case d.Traversability_TRAVERSABILITY_STANDABLE:
		out.Passability = combatsim.Standable
	case d.Traversability_TRAVERSABILITY_PASS_THROUGH_ONLY:
		out.Passability = combatsim.PassThroughOnly
	case d.Traversability_TRAVERSABILITY_IMPASSABLE:
		out.Passability = combatsim.Impassable
	default:
		return out, unsupported("thing %q has passability %v", name, row.GetPassability())
	}
	if class := row.GetThingClass(); class != "" {
		door, err := im.cat.ClassIsA(class, "RimWorld.Building_Door")
		if err != nil {
			return out, unsupported("thing %q class %q: %v", name, class, err)
		}
		out.Door = door
	}
	return out, nil
}
