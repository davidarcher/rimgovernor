package stateval

import (
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	d "github.com/davidarcher/RimGovernor/go/internal/wire/defspb"
)

// The pawnstat-group StatWorkers (epic #2621, #2639): the pawn-only stats whose
// worker overrides ShouldShowFor or the value. StatWorker_LeatherAmount and
// StatWorker_CaravanRidingSpeedFactor override only hyperlinks and
// explanations, so they are plain workers.

func pawnStatWorkerList() []Worker {
	return []Worker{
		workerMechanitor{}, workerTerror{}, workerWildness{}, workerSuppressionFallRate{},
		workerVacuumResistance{}, workerMinimumHandlingSkill{}, workerForagedNutritionPerDay{},
		workerMeatAmount{}, workerLeatherAmount{}, workerCaravanRidingSpeedFactor{},
	}
}

// requirePawn is the `(Pawn)req.Thing` cast: a request without a pawn is an error.
func requirePawn(req *Request, what string) (*PawnState, error) {
	p := req.pawn()
	if p == nil {
		return nil, fmt.Errorf("%s needs a pawn: the request has none", what)
	}
	return p, nil
}

// isFlesh is RaceProperties.IsFlesh: the flesh type's isOrganic.
func (e *Evaluator) isFlesh(r *d.RaceProperties) (bool, error) {
	flesh := bridge.DefRow[*d.FleshTypeDef](e.catalog, fleshType(r))
	if flesh == nil {
		return false, fmt.Errorf("catalog has no flesh type %s", fleshType(r))
	}
	return flesh.GetIsOrganic(), nil
}

// --- StatWorker_Mechanitor ---

type workerMechanitor struct{}

func (workerMechanitor) Class() string { return "StatWorker_Mechanitor" }

func (workerMechanitor) Show(req *Request, base func() (bool, error)) (bool, error) {
	if ok, err := base(); err != nil || !ok {
		return false, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return false, nil
	}
	return need(pawn.PawnStat.IsMechanitor, "MechanitorUtility.IsMechanitor of the pawn")
}

// --- StatWorker_Terror ---

type workerTerror struct{}

func (workerTerror) Class() string { return "StatWorker_Terror" }

func (workerTerror) Show(req *Request, base func() (bool, error)) (bool, error) {
	return showSlavePawn(req, base)
}

// showSlavePawn is the ShouldShowFor shared by Terror and SuppressionFallRate:
// the base's, and the thing is a slave pawn.
func showSlavePawn(req *Request, base func() (bool, error)) (bool, error) {
	if ok, err := base(); err != nil || !ok {
		return false, err
	}
	pawn := req.pawn()
	if pawn == nil {
		return false, nil
	}
	return need(pawn.IsSlave, "Pawn.IsSlave")
}

// Unfinalized is Pawn.GetTerrorLevel: zero for a non-slave, else the summed
// terror thought intensity capped at 100, over 100.
func (workerTerror) Unfinalized(req *Request) (float32, error) {
	pawn, err := requirePawn(req, "StatWorker_Terror")
	if err != nil {
		return 0, err
	}
	slave, err := need(pawn.IsSlave, "Pawn.IsSlave")
	if err != nil || !slave {
		return 0, err
	}
	intensities, err := need(pawn.PawnStat.TerrorIntensities, "the pawn's terror thought intensities")
	if err != nil {
		return 0, err
	}
	var sum int32
	for _, n := range intensities {
		sum += n
	}
	return float32(float32(min(sum, 100)) / 100), nil
}

// --- StatWorker_Wildness ---

type workerWildness struct{}

func (workerWildness) Class() string { return "StatWorker_Wildness" }

// Show does not call the base: a pawn that is a wild man or an animal, else a
// def whose race is Animal.
func (workerWildness) Show(req *Request, _ func() (bool, error)) (bool, error) {
	if pawn := req.pawn(); pawn != nil {
		wild, err := need(pawn.IsWildMan, "Pawn.IsWildMan")
		if err != nil || wild {
			return wild, err
		}
		animal, err := need(pawn.PawnStat.IsAnimal, "Pawn.IsAnimal")
		if err != nil || animal {
			return animal, err
		}
	}
	return req.Evaluator.defRaceAnimal(req)
}

// defRaceAnimal is `req.Def is ThingDef { race: { Animal: not false } }`.
func (e *Evaluator) defRaceAnimal(req *Request) (bool, error) {
	if req.Thing == nil || req.Thing.GetRace() == nil {
		return false, nil
	}
	r := req.Thing.GetRace()
	anomaly, err := e.anomalyEntity(fleshType(r))
	if err != nil {
		return false, err
	}
	return e.animal(r, anomaly)
}

// --- StatWorker_SuppressionFallRate ---

type workerSuppressionFallRate struct{}

func (workerSuppressionFallRate) Class() string { return "StatWorker_SuppressionFallRate" }

func (workerSuppressionFallRate) Show(req *Request, base func() (bool, error)) (bool, error) {
	return showSlavePawn(req, base)
}

// Unfinalized is CurrentFallRateBasedOnSuppression of the pawn's Need_Suppression.
func (workerSuppressionFallRate) Unfinalized(req *Request) (float32, error) {
	pawn, err := requirePawn(req, "StatWorker_SuppressionFallRate")
	if err != nil {
		return 0, err
	}
	suppression, err := need(pawn.PawnStat.Suppression, "the pawn's Need_Suppression")
	if err != nil {
		return 0, err
	}
	if !suppression.Present {
		return 0, fmt.Errorf("stat evaluation needs the pawn's Need_Suppression: the pawn has none")
	}
	game, err := req.Evaluator.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	c := game.GetStatWorker_SuppressionFallRate()
	if c == nil {
		return 0, fmt.Errorf("catalog game constants lack StatWorker_SuppressionFallRate")
	}
	switch p := suppression.CurLevelPercentage; {
	case p > c.GetFastFallRateThreshold():
		return c.GetFastFallRate(), nil
	case p > c.GetMediumFallRateThreshold():
		return c.GetMediumFallRate(), nil
	}
	return c.GetSlowFallRate(), nil
}

// --- StatWorker_VacuumResistance ---

type workerVacuumResistance struct{}

func (workerVacuumResistance) Class() string { return "StatWorker_VacuumResistance" }

func (workerVacuumResistance) Show(req *Request, base func() (bool, error)) (bool, error) {
	if ok, err := base(); err != nil || !ok {
		return false, err
	}
	if req.pawn() == nil {
		return false, nil
	}
	r, err := race(req)
	if err != nil {
		return false, err
	}
	return req.Evaluator.isFlesh(r)
}

// Unfinalized is 1 for a mutant that does not breathe air, else the base's.
func (workerVacuumResistance) Unfinalized(req *Request) (float32, error) {
	if pawn := req.pawn(); pawn != nil {
		name, err := need(pawn.PawnStat.MutantDef, "the pawn's mutant def")
		if err != nil {
			return 0, err
		}
		if name != "" {
			mutant := bridge.DefRow[*d.MutantDef](req.Evaluator.catalog, name)
			if mutant == nil {
				return 0, fmt.Errorf("catalog has no mutant def %s", name)
			}
			if !mutant.GetBreathesAir() {
				return 1, nil
			}
		}
	}
	return req.Evaluator.baseUnfinalized(req)
}

// --- StatWorker_MinimumHandlingSkill ---

type workerMinimumHandlingSkill struct{}

func (workerMinimumHandlingSkill) Class() string { return "StatWorker_MinimumHandlingSkill" }

// Unfinalized is ValueFromReq: 0 for a humanlike, else the handling-skill curve
// of the Wildness stat, clamped to 0..20. A thing's Wildness is its own stat
// value (req.Thing.GetStatValue); a definition's is GetStatValueAbstract with
// no stuff.
func (workerMinimumHandlingSkill) Unfinalized(req *Request) (float32, error) {
	if req.Thing == nil {
		return 0, fmt.Errorf("StatWorker_MinimumHandlingSkill needs a ThingDef: %s is a terrain", req.Subject.Def)
	}
	r, err := race(req)
	if err != nil {
		return 0, err
	}
	if humanlike(r) {
		return 0, nil
	}
	e := req.Evaluator
	subject := Subject{Def: req.Subject.Def}
	if req.Subject.Context != nil {
		subject = req.Subject
	}
	wild, err := e.request("Wildness", subject)
	if err != nil {
		return 0, err
	}
	x, err := e.value(wild)
	if err != nil {
		return 0, err
	}
	game, err := e.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	c := game.GetStatWorker_MinimumHandlingSkill()
	if c == nil {
		return 0, fmt.Errorf("catalog game constants lack StatWorker_MinimumHandlingSkill")
	}
	skill, err := EvaluateCurve(c.GetHandlingSkillFromWildness(), x)
	if err != nil {
		return 0, fmt.Errorf("handling skill from wildness curve: %w", err)
	}
	return clamp32(skill, 0, 20), nil
}

// --- StatWorker_ForagedNutritionPerDay ---

type workerForagedNutritionPerDay struct{}

func (workerForagedNutritionPerDay) Class() string { return "StatWorker_ForagedNutritionPerDay" }

// BaseValue is the base worker's, or under Odyssey a pawn that has learned
// Forage eats ForgeBodySizeFactor of its body size.
func (workerForagedNutritionPerDay) BaseValue(req *Request) (float32, error) {
	result := baseValue(req)
	pawn := req.pawn()
	if pawn == nil {
		return result, nil
	}
	odyssey, err := req.Evaluator.modActive(modOdyssey)
	if err != nil || !odyssey {
		return result, err
	}
	training, err := need(pawn.PawnStat.Training, "the pawn's training tracker")
	if err != nil || !training.Present || !training.ForageLearned {
		return result, err
	}
	size, err := pawnBodySize(req, pawn, req.Thing)
	if err != nil {
		return 0, err
	}
	game, err := req.Evaluator.catalog.GameConstants()
	if err != nil {
		return 0, err
	}
	c := game.GetStatWorker_ForagedNutritionPerDay()
	if c == nil {
		return 0, fmt.Errorf("catalog game constants lack StatWorker_ForagedNutritionPerDay")
	}
	return float32(size * c.GetForgeBodySizeFactor()), nil
}

// Show hides an animal with a training tracker unless Odyssey is active and it
// has learned Forage; anything else is the base's.
func (workerForagedNutritionPerDay) Show(req *Request, base func() (bool, error)) (bool, error) {
	if pawn := req.pawn(); pawn != nil {
		animal, err := need(pawn.PawnStat.IsAnimal, "Pawn.IsAnimal")
		if err != nil {
			return false, err
		}
		if animal {
			training, err := need(pawn.PawnStat.Training, "the pawn's training tracker")
			if err != nil {
				return false, err
			}
			if training.Present {
				odyssey, err := req.Evaluator.modActive(modOdyssey)
				if err != nil || !odyssey {
					return false, err
				}
				return training.ForageLearned, nil
			}
		}
	}
	return base()
}

// --- StatWorker_MeatAmount ---

type workerMeatAmount struct{}

func (workerMeatAmount) Class() string { return "StatWorker_MeatAmount" }

// Show hides the stat for a pawn whose race has meat.
func (workerMeatAmount) Show(req *Request, base func() (bool, error)) (bool, error) {
	if req.pawn() != nil {
		if r := req.Thing.GetRace(); r != nil && r.GetHasMeat() {
			return false, nil
		}
	}
	return base()
}

// --- display-only workers ---

type workerLeatherAmount struct{}

func (workerLeatherAmount) Class() string { return "StatWorker_LeatherAmount" }

type workerCaravanRidingSpeedFactor struct{}

func (workerCaravanRidingSpeedFactor) Class() string { return "StatWorker_CaravanRidingSpeedFactor" }
