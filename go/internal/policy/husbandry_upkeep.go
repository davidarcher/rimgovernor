package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainHerd names MaintainHerd-*'s train/slaughter deficit: any observed,
// non-release/slaughter-flagged animal with an available-but-untrained
// trainable, plus -- only once an operator has explicitly opted in via
// RoutinePolicy.AllowSlaughter and declared a HerdPopulationMax for a race --
// any surplus animal of that race safe to slaughter. Disclosed narrowing:
// Python's per-race protected-id, breeding-reserve and feed-reservation
// richness (controller/rimgovernor/husbandry.py's assess_herd) has no Go
// equivalent yet; surplus eligibility relies entirely on native's own
// SafeToSlaughter fact (which already excludes bonded/master animals per
// RimWorld's own rules) rather than a re-derived protected-IDs set, and there
// is no separate breeding-reserve carve-out. Slaughter is irreversible, so
// AllowSlaughter defaults to false and HerdPopulationMax defaults to empty:
// an operator who never sets either gets exactly the training-only behavior
// this need always had. Native eligibility (canTrain, safeToSlaughter) is
// still re-validated by EvaluateHusbandry immediately before dispatch; this
// only decides which already-observed candidate to try.
const MaintainHerd GoalID = "MaintainHerd"

// HusbandryPlanReason names why RoutineHusbandryPlanner did or did not
// propose a training write.
type HusbandryPlanReason string

const (
	HusbandryNoDeficit HusbandryPlanReason = "no_untrained_trainable_or_surplus"
	HusbandryUnknown   HusbandryPlanReason = "husbandry_census_unknown"
)

// HusbandryChoice is the one animal/method pair chosen for a training or
// slaughter write, mirroring husbandry_method's single-write-per-cycle rule
// (a recursive training or slaughter write can change other requested
// fields, so only one is ever chosen). Method is domain.HusbandryTrain or
// domain.HusbandrySlaughter whenever Reason is empty (a choice was made);
// TrainableDef is only ever set for HusbandryTrain.
type HusbandryChoice struct {
	Reason       HusbandryPlanReason
	Animal       PawnID
	Method       domain.HusbandryMethod
	TrainableDef string
}

// AnimalHerdDeficit reports whether any observed animal still has an
// available, not-yet-learned trainable, or -- only when allowSlaughter is
// true and populationMax declares at least one tracked race -- any race has
// an observed surplus with a safe-to-slaughter candidate not yet designated.
// An unknown census, or any animal whose release/slaughter/training facts
// are incomplete, leaves the whole need unknown rather than silently
// treating it as recovered; see herdSlaughterCandidates for the surplus
// unknown-handling this mirrors.
func AnimalHerdDeficit(animals domain.Fact[[]UpkeepAnimal], allowSlaughter bool, populationMax map[Resource]int64) domain.Fact[bool] {
	rows, known := animals.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	deficit := false
	for _, a := range rows {
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return domain.Unknown[bool]()
		}
		if release || slaughter {
			continue
		}
		for _, t := range a.Training {
			avail, ak := t.Available.Value()
			if !ak {
				return domain.Unknown[bool]()
			}
			if !avail {
				continue
			}
			learned, lk := t.Learned.Value()
			if !lk {
				return domain.Unknown[bool]()
			}
			if !learned {
				deficit = true
			}
		}
	}
	if allowSlaughter && len(populationMax) > 0 {
		candidates, unknown := herdSlaughterCandidates(rows, populationMax)
		if unknown {
			return domain.Unknown[bool]()
		}
		if len(candidates) > 0 {
			deficit = true
		}
	}
	return domain.Known(deficit)
}

// herdSlaughterCandidates ports husbandry.py's assess_herd surplus math
// (surplus = len(animals) - target.maximum - pending) per tracked race,
// narrowed to a single global opt-in with no protected-id/breeding-reserve
// carve-out: eligibility is taken entirely from native's own SafeToSlaughter
// fact. Only races present in populationMax are ever counted or dispatched;
// an untracked race never blocks or contributes to a candidate list. Any
// tracked-race animal with an unknown release, slaughter or
// safe-to-slaughter fact makes the whole result unknown, since an unknown
// fact is never evidence of a safe surplus. Returned candidates are
// deterministically ordered (lowest animal ID first) and already capped to
// each race's own surplus count.
func herdSlaughterCandidates(rows []UpkeepAnimal, populationMax map[Resource]int64) ([]UpkeepAnimal, bool) {
	counts := map[Resource]int64{}
	pending := map[Resource]int64{}
	eligible := map[Resource][]UpkeepAnimal{}
	for _, a := range rows {
		if _, tracked := populationMax[a.Definition]; !tracked {
			continue
		}
		release, rk := a.Release.Value()
		slaughter, sk := a.Slaughter.Value()
		if !rk || !sk {
			return nil, true
		}
		if release {
			continue
		}
		counts[a.Definition]++
		if slaughter {
			pending[a.Definition]++
			continue
		}
		safe, sak := a.SafeToSlaughter.Value()
		if !sak {
			return nil, true
		}
		if safe {
			eligible[a.Definition] = append(eligible[a.Definition], a)
		}
	}
	var candidates []UpkeepAnimal
	for race, limit := range populationMax {
		surplus := counts[race] - limit - pending[race]
		if surplus <= 0 {
			continue
		}
		rows := append([]UpkeepAnimal{}, eligible[race]...)
		sort.Slice(rows, func(i, j int) bool { return rows[i].ID < rows[j].ID })
		if int64(len(rows)) > surplus {
			rows = rows[:surplus]
		}
		candidates = append(candidates, rows...)
	}
	sort.Slice(candidates, func(i, j int) bool { return candidates[i].ID < candidates[j].ID })
	return candidates, false
}

// SelectHusbandryMethod picks the lowest animal-ID, lowest-def-name available
// untrained trainable to dispatch next, trying training first the same way
// it always has. Only once no training candidate exists, and only when
// allowSlaughter is true and populationMax declares a tracked race, does it
// fall back to the lowest-animal-ID surplus slaughter candidate from
// herdSlaughterCandidates -- training is always preferred so an operator who
// opts into slaughter does not lose the pre-existing training behavior.
func SelectHusbandryMethod(animals domain.Fact[[]UpkeepAnimal], allowSlaughter bool, populationMax map[Resource]int64) HusbandryChoice {
	rows, known := animals.Value()
	if !known {
		return HusbandryChoice{Reason: HusbandryUnknown}
	}
	sorted := append([]UpkeepAnimal{}, rows...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].ID < sorted[j].ID })
	for _, a := range sorted {
		if release, rk := a.Release.Value(); rk && release {
			continue
		}
		if slaughter, sk := a.Slaughter.Value(); sk && slaughter {
			continue
		}
		trainables := append([]HusbandryTrainable{}, a.Training...)
		sort.Slice(trainables, func(i, j int) bool { return trainables[i].Def < trainables[j].Def })
		for _, t := range trainables {
			avail, ak := t.Available.Value()
			learned, lk := t.Learned.Value()
			if ak && avail && lk && !learned {
				return HusbandryChoice{Animal: a.ID, Method: domain.HusbandryTrain, TrainableDef: t.Def}
			}
		}
	}
	if allowSlaughter && len(populationMax) > 0 {
		candidates, unknown := herdSlaughterCandidates(rows, populationMax)
		if unknown {
			return HusbandryChoice{Reason: HusbandryUnknown}
		}
		if len(candidates) > 0 {
			return HusbandryChoice{Animal: candidates[0].ID, Method: domain.HusbandrySlaughter}
		}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
