package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MaintainHerd names MaintainHerd-*'s train/slaughter deficit: any observed,
// non-release/slaughter-flagged animal with an available-but-untrained
// trainable. Disclosed narrowing: Python's per-race population target,
// protected-id, breeding-reserve and feed-reservation richness
// (controller/rimgovernor/husbandry.py's assess_herd) has no
// player-configurable equivalent in Go yet, so this need only ever proposes
// training, never slaughter — an unauthorized destructive write with no
// player policy backing it would be unsafe to dispatch autonomously. Native
// eligibility (canTrain, safeToSlaughter) is still re-validated by
// EvaluateHusbandry immediately before dispatch; this only decides which
// already-observed candidate to try.
const MaintainHerd GoalID = "MaintainHerd"

// HusbandryPlanReason names why RoutineHusbandryPlanner did or did not
// propose a training write.
type HusbandryPlanReason string

const (
	HusbandryNoDeficit HusbandryPlanReason = "no_untrained_trainable"
	HusbandryUnknown   HusbandryPlanReason = "husbandry_census_unknown"
)

// HusbandryChoice is the one animal/trainable pair chosen for a training
// write, mirroring husbandry_method's single-write-per-cycle rule (a
// recursive training write can change other requested fields, so only one is
// ever chosen).
type HusbandryChoice struct {
	Reason       HusbandryPlanReason
	Animal       PawnID
	TrainableDef string
}

// AnimalHerdDeficit reports whether any observed animal still has an
// available, not-yet-learned trainable. An unknown census, or any animal
// whose release/slaughter/training facts are incomplete, leaves the whole
// need unknown rather than silently treating it as recovered.
func AnimalHerdDeficit(animals domain.Fact[[]UpkeepAnimal]) domain.Fact[bool] {
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
	return domain.Known(deficit)
}

// SelectHusbandryMethod picks the lowest animal-ID, lowest-def-name available
// untrained trainable to dispatch next.
func SelectHusbandryMethod(animals domain.Fact[[]UpkeepAnimal]) HusbandryChoice {
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
				return HusbandryChoice{Animal: a.ID, TrainableDef: t.Def}
			}
		}
	}
	return HusbandryChoice{Reason: HusbandryNoDeficit}
}
