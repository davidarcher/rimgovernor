package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MoodThought is one grouped native thought row (the Mood tab's stacking)
// with its total mood offset; the census keeps only rows that pull the
// pawn's mood down.
type MoodThought struct {
	Def    string
	Offset float64
}

// MoodProvision is one upkeep goal whose facility would remove the pawn's
// observed environment thought pressure, with the summed offset of the
// thoughts it owns (most negative first in MoodState.Provision).
type MoodProvision struct {
	Goal   GoalID
	Offset float64
}

// moodProvisionOwners maps the removable environment thoughts to the goal
// whose facility removes them. Everything else (SleptInBarracks, ugly
// apparel, social memories) is left to native relief and the pawn's own
// recovery: no goal builds bedrooms yet (#286).
var moodProvisionOwners = map[string]GoalID{
	"AteWithoutTable": EnsureComfort,
	"NeedJoy":         EnsureComfort,
	"SleptOutside":    EnsureInitialShelter,
	"SleptOnGround":   EnsureInitialShelter,
	"EnvironmentDark": MaintainLighting,
	"EnvironmentCold": EnsureTemperatureSafety,
	"EnvironmentHot":  EnsureTemperatureSafety,
	"NeedBeauty":      MaintainCleanFacilities,
	"NeedRoomSize":    EnsureExpansion,
}

// MoodProvisionOwner names the upkeep goal whose facility removes the
// thought, if the catalog knows one.
func MoodProvisionOwner(def string) (GoalID, bool) {
	goal, ok := moodProvisionOwners[def]
	return goal, ok
}

// MoodProvisionGoal reports whether the catalog can name the goal as an owner.
func MoodProvisionGoal(goal GoalID) bool {
	for _, owner := range moodProvisionOwners {
		if owner == goal {
			return true
		}
	}
	return false
}

func validateMoodThoughts(f domain.Fact[[]MoodThought]) error {
	rows, known := f.Value()
	if !known {
		return nil
	}
	if len(rows) > 64 {
		return errors.New("mood thoughts exceed bound")
	}
	seen := map[string]bool{}
	for _, t := range rows {
		if !foodID(t.Def) || seen[t.Def] || math.IsNaN(t.Offset) || math.IsInf(t.Offset, 0) || t.Offset >= 0 {
			return errors.New("invalid mood thought")
		}
		seen[t.Def] = true
	}
	return nil
}

func validateMoodProvision(rows []MoodProvision) error {
	if len(rows) > 8 {
		return errors.New("mood provision exceeds bound")
	}
	seen := map[GoalID]bool{}
	for i, p := range rows {
		if p.Goal == "" || seen[p.Goal] || math.IsNaN(p.Offset) || math.IsInf(p.Offset, 0) || p.Offset >= 0 {
			return errors.New("invalid mood provision")
		}
		if i > 0 && rows[i-1].Offset > p.Offset {
			return errors.New("mood provision out of order")
		}
		seen[p.Goal] = true
	}
	return nil
}

// moodProvisioning reduces a pawn's observed negative thoughts to the owner
// goals of its removable environment thoughts. The result is empty unless
// those thoughts dominate: they carry at least half of the pawn's total
// negative thought offset. Unknown thoughts provision nothing.
func moodProvisioning(f domain.Fact[[]MoodThought]) []MoodProvision {
	rows, known := f.Value()
	if !known {
		return nil
	}
	total, owned := 0.0, 0.0
	byGoal := map[GoalID]float64{}
	for _, t := range rows {
		total += t.Offset
		if goal, ok := MoodProvisionOwner(t.Def); ok {
			owned += t.Offset
			byGoal[goal] += t.Offset
		}
	}
	if owned >= 0 || owned > total/2 {
		return nil
	}
	var result []MoodProvision
	for goal, offset := range byGoal {
		result = append(result, MoodProvision{goal, offset})
	}
	sort.Slice(result, func(i, j int) bool {
		if result[i].Offset != result[j].Offset {
			return result[i].Offset < result[j].Offset
		}
		return result[i].Goal < result[j].Goal
	})
	return result
}

// MoodProvisionDeficits reports, per owner goal, the fraction of reviewed
// pawns whose dominant thought pressure that goal's facility would remove.
// DetectRoutine raises the owner's development deficit to at least this.
func MoodProvisionDeficits(h MoodHistory) map[GoalID]float64 {
	if len(h.States) == 0 {
		return nil
	}
	counts := map[GoalID]int{}
	for _, s := range h.States {
		if !s.Active || s.Missing {
			continue
		}
		for _, p := range s.Provision {
			counts[p.Goal]++
		}
	}
	if len(counts) == 0 {
		return nil
	}
	result := make(map[GoalID]float64, len(counts))
	for goal, n := range counts {
		result[goal] = float64(n) / float64(len(h.States))
	}
	return result
}

// WithoutProvision is the same state with its provisioning dropped: the
// relief planner falls back to it when no owner goal is active to provision.
func (s MoodState) WithoutProvision() MoodState {
	s.Provision = nil
	return s
}
