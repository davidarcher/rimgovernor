package combatlab

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim/calibrate"
	"github.com/davidarcher/RimGovernor/go/internal/combatsim/simimport"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The ranged slice's calibration split (#2844), fixed before any measurement.
// Calibration fixtures were used while building the sim (its tests and
// approximations were checked on lab-ranged); held-out fixtures were not. A
// fixture the sim cannot represent stays in its list and fails the gate with
// the sim's refusal: the gate never drops a fixture it cannot run.
var (
	CalibrationFixtures = []string{"lab-ranged"}
	// HeldOutFixtures are the lab fixtures whose hostiles a ranged-slice sim
	// would have to fight: gunners behind or with melee raiders, a walled
	// base with turrets, and the shield-belt tank.
	HeldOutFixtures = []string{"lab-ranged-shield", "lab-open", "lab-choke", "lab-base", "lab-split-ranged"}
)

// Variant is a tactic variant: a named CombatTuning swept in the real game and
// in the sim over the same seeds. The default's real sweep is the committed
// baseline; the others' are sweep files under CalibrationVariantsDir.
type Variant struct {
	Name   string
	Tuning policy.CombatTuning
}

// Variants are the ranged slice's tactic variants: the two tuning knobs the
// ranged fallback and raid grouping read, each moved both ways.
func Variants() []Variant {
	with := func(name string, f func(*policy.CombatTuning)) Variant {
		t := policy.DefaultCombatTuning()
		f(&t)
		return Variant{Name: name, Tuning: t}
	}
	return []Variant{
		{Name: calibrate.DefaultVariant, Tuning: policy.DefaultCombatTuning()},
		with("scatter-short", func(t *policy.CombatTuning) { t.ScatterCells = 8 }),
		with("scatter-long", func(t *policy.CombatTuning) { t.ScatterCells = 45 }),
		with("group-gap-tight", func(t *policy.CombatTuning) { t.GroupGap = 3 }),
		with("group-gap-wide", func(t *policy.CombatTuning) { t.GroupGap = 20 }),
	}
}

// CalibrationDir is the calibration directory relative to the Go module root:
// report.json and report.txt (the gate's recorded output) and variants/, the
// real-game sweep files `<fixture>/<variant>.json` of the non-default variants.
const CalibrationDir = "internal/nativeaccept/cases/combatlab/calibration"

// CalibrationEnv is the SimEnv the gate imports under: the lab's, with the
// facts it cannot read stated. They are assumptions, listed in the report's
// caveats, not readings: colonists in no armor-rated apparel, the difficulty's
// friendly fire factor, and the death-on-downed inputs. The lab runs the
// RimGovernorQuiet storyteller (BaseStoryteller curves) on a fresh Custom
// difficulty: four colonists put populationIntentFactorFromPopCurve at 1, and
// the population adaptation is 0 days (reset when the colonists arrive), where
// populationIntentFactorFromPopAdaptDaysCurve is 0, so the intent is 0;
// enemyDeathOnDownedChanceFactor is 1 on every difficulty from Medium up and
// unwaveringPrisoners is off. The 0.85 this gives agrees with the real
// ledger: 56 of 67 raiders that crossed the pain-shock total died there.
//
// The real runs the gate compares with are the metrics cases, which staff
// the loaded lab with the superpawn crew (Crew{Size: 3}, StaffCrew): every
// colonist skill is na.CrewSkillLevel there, not the staging's level 8 (#2906;
// at 8 the sim's colonists aimed on target 23% of the time where the real
// ones did about 48%). The crew's SuperTraits add move speed (SpeedOffset 2 is
// +0.4 over the 4.6 of a traitless adult): the resumed baselines' movers
// arrive at ticks 29/29/57 (a 14.0 cost per cell: 60/5.0 + 2), where a pawn
// at 4.6 would take 15.04 per cell (#2933).
func CalibrationEnv() SimEnv {
	env := LabSimEnv()
	env.ColonistSkill = na.CrewSkillLevel
	env.ColonistTraits = crewTraits()
	env.FriendlyFireChanceFactor = domain.Known(0.4)
	env.DeathOnDowned = domain.Known(simimport.DeathOnDowned{PopulationIntent: 0, EnemyDeathOnDownedChanceFactor: 1})
	env.AllowInstantKillChance = domain.Known(1.0) // DifficultyDef default; the lab sets no protection
	env.ColonistWorn = domain.Known([]simimport.Wear{})
	env.AutoFleeFraction = domain.Known(calibrationAutoFleeFraction)
	return env
}

// calibrationAutoFleeFraction is the lord's Trigger_FractionPawnsLost fraction
// the real runs imply. The lab stages the assault lord with canTimeoutOrFlee
// false, but Lord.SetJob still gives the faction's lord the panic-flee
// transition (autoFlee, a fraction drawn from 0.4..0.7 by the lord's load id,
// which no committed file holds). The baselines bracket it: lab-split-ranged
// (8 raiders) shows exactly 3 fled after 4 dead and 1 down in every run that
// reached the flight and never one with 4 lost, so 4 < 8f <= 5; lab-ranged
// (4 raiders) flees at the third loss, so 2 < 4f <= 3. The midpoint of
// (0.5, 0.625] stands in for both lords.
const calibrationAutoFleeFraction = 0.5625

// crewTraits are na.SuperTraits ("Def:degree,...") as importer traits.
func crewTraits() []simimport.Trait {
	var out []simimport.Trait
	for _, part := range strings.Split(na.SuperTraits, ",") {
		def, degree, ok := strings.Cut(part, ":")
		n, err := strconv.Atoi(degree)
		if !ok || err != nil {
			panic("combatlab: na.SuperTraits is not Def:degree pairs: " + na.SuperTraits)
		}
		out = append(out, simimport.Trait{Def: def, Degree: int32(n)})
	}
	return out
}

// SimSweep plays the fight under tuning for each seed and returns the sweep
// of the scored runs, the same schema `acceptance sweep` writes.
func SimSweep(fixture string, fight combatsim.Fight, tuning policy.CombatTuning, seeds []int) (combatmetrics.Sweep, error) {
	var rows []combatmetrics.SeedRow
	for _, seed := range seeds {
		res, err := combatsim.RunFight(fight, uint64(seed), tuning)
		if err != nil {
			return combatmetrics.Sweep{}, fmt.Errorf("seed %d: %w", seed, err)
		}
		m := res.Metrics
		rows = append(rows, combatmetrics.SeedRow{Seed: seed, Metrics: &m})
	}
	return combatmetrics.NewSweep(fixture, 1, "combatsim", 0, rows), nil
}

// readVariant reads a real-game sweep file of a non-default variant, nil when
// it has not been recorded.
func readVariant(dir, fixture, variant string) (*combatmetrics.Sweep, error) {
	data, err := os.ReadFile(filepath.Join(dir, "variants", fixture, variant+".json"))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	s, err := combatmetrics.ParseSweep(data)
	if err != nil {
		return nil, fmt.Errorf("%s/%s: %w", fixture, variant, err)
	}
	return &s, nil
}

// CalibrationInputs measures every listed fixture under every variant in the
// sim and pairs it with its real sweeps. The sim side runs the baseline's
// seeds. variantsDir is the calibration directory holding variants/.
func CalibrationInputs(cat *bridge.DefinitionCatalog, variantsDir string) ([]calibrate.FixtureInput, error) {
	cx := na.LabMapSize / 2
	cz := cx
	var out []calibrate.FixtureInput
	roles := []struct {
		role  calibrate.Role
		names []string
	}{{calibrate.Calibration, CalibrationFixtures}, {calibrate.HeldOut, HeldOutFixtures}}
	for _, r := range roles {
		for _, name := range r.names {
			fx := calibrate.FixtureInput{Name: name, Role: r.role}
			fight, err := importFight(cat, name, cx, cz)
			if err != nil {
				fx.Unsupported = err
				out = append(out, fx)
				continue
			}
			base, err := Baseline(name)
			if err != nil {
				return nil, err
			}
			seeds := make([]int, len(base.Rows))
			for i, row := range base.Rows {
				seeds[i] = row.Seed
			}
			for _, v := range Variants() {
				if r.role == calibrate.Calibration && v.Name != calibrate.DefaultVariant {
					continue // a calibration fixture is checked against its baseline only
				}
				vi := calibrate.VariantInput{Name: v.Name}
				if v.Name == calibrate.DefaultVariant {
					vi.Real = &base
				} else if vi.Real, err = readVariant(variantsDir, name, v.Name); err != nil {
					return nil, err
				}
				sim, err := SimSweep(name, fight, v.Tuning, seeds)
				if err != nil {
					vi.SimErr = err
				} else {
					vi.Sim = &sim
				}
				fx.Variants = append(fx.Variants, vi)
			}
			out = append(out, fx)
		}
	}
	return out, nil
}

// importFight builds the sim fight of a fixture, or the reason the sim cannot
// run it. A fixture with a hostile the sim leaves unmodeled (a melee weapon)
// is unsupported: the AI would leave that raider doing nothing, and a result
// from the remainder is not a result for the fixture.
func importFight(cat *bridge.DefinitionCatalog, name string, cx, cz int) (combatsim.Fight, error) {
	f, err := Build(name, cx, cz)
	if err != nil {
		return combatsim.Fight{}, err
	}
	spec, err := SimSpec(f, CalibrationEnv())
	if err != nil {
		return combatsim.Fight{}, fmt.Errorf("%w: %v", combatsim.ErrUnsupported, err)
	}
	res, err := simimport.Import(cat, spec)
	if err != nil {
		return combatsim.Fight{}, err
	}
	if len(res.Unmodeled) > 0 {
		return combatsim.Fight{}, fmt.Errorf("%w: the fixture holds what the sim leaves out (melee pawns act as unarmed, E8): %d unmodeled, first: %s", combatsim.ErrUnsupported, len(res.Unmodeled), res.Unmodeled[0])
	}
	return res.Fight, nil
}
