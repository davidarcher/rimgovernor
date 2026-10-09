package policy

import (
	"errors"
	"math"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// MoodThought is one grouped native thought row (the Mood tab's stacking)
// with its total mood offset; the census keeps negative and positive rows
// (a memory like KnowBuriedInSarcophagus is positive).
type MoodThought struct {
	Def    string
	Offset float64
}

// MoodProvision is one upkeep goal whose facility would remove the pawn's
// observed environment thought pressure, with the summed offset of the
// thoughts it owns (most negative first in MoodState.Provision).
type MoodProvision struct {
	Concern ConcernID
	Offset  float64
}

// Better meals can offset high-expectation mood pressure; they do not remove
// the expectation itself. Only an observed deficit requests this lever.
func mealMoodProvision(p MoodPawn, rows []MoodProvision) []MoodProvision {
	high, hk := p.HighExpectations.Value()
	mood, mk := p.Mood.Value()
	target, tk := p.Target.Value()
	if !hk || !high || !mk || !tk || mood >= target {
		return rows
	}
	for _, row := range rows {
		if row.Concern == EnsureCooking {
			return rows
		}
	}
	rows = append(rows, MoodProvision{Concern: EnsureCooking, Offset: -math.Max(1, (target-mood)*100)})
	sort.Slice(rows, func(i, j int) bool {
		if rows[i].Offset != rows[j].Offset {
			return rows[i].Offset < rows[j].Offset
		}
		return rows[i].Concern < rows[j].Concern
	})
	return rows
}

func validateMoodThoughts(f domain.Fact[[]MoodThought]) error {
	rows, known := f.Value()
	if !known {
		return nil
	}
	seen := map[string]bool{}
	for _, t := range rows {
		if !foodID(t.Def) || seen[t.Def] || math.IsNaN(t.Offset) || math.IsInf(t.Offset, 0) {
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
	seen := map[ConcernID]bool{}
	for i, p := range rows {
		if p.Concern == "" || seen[p.Concern] || math.IsNaN(p.Offset) || math.IsInf(p.Offset, 0) || p.Offset >= 0 {
			return errors.New("invalid mood provision")
		}
		if i > 0 && rows[i-1].Offset > p.Offset {
			return errors.New("mood provision out of order")
		}
		seen[p.Concern] = true
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
	byGoal := map[ConcernID]float64{}
	for _, t := range rows {
		if t.Offset >= 0 {
			continue
		}
		total += t.Offset
		owners := thoughtOwners(t.Def)
		if len(owners) > 0 {
			owned += t.Offset
		}
		for _, goal := range owners {
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
		return result[i].Concern < result[j].Concern
	})
	return result
}

// MoodProvisionDeficits reports, per owner goal, the ledger-weighted share of
// reviewed pawns under the entry margin: a pawn at or below threshold plus
// moodEntryMargin weighs the fraction of its negative thought loss the owner
// can remove; a pawn above the margin weighs 0. A pawn whose loss, mood or
// threshold is unreadable is left out of the denominator, never counted as
// zero. DetectRounds raises the owner's development deficit to at least this.
func MoodProvisionDeficits(census []MoodPawn, ledger MoodLedger) map[ConcernID]float64 {
	byID := make(map[PawnID]MoodPawn, len(census))
	for _, p := range census {
		byID[p.ID] = p
	}
	read := 0
	weights := map[ConcernID]float64{}
	for _, lp := range ledger.Pawns {
		lost, lk := lp.Lost.Value()
		p := byID[lp.ID]
		mood, mk := p.Mood.Value()
		threshold, tk := p.Threshold.Value()
		if !lk || !mk || !tk {
			continue
		}
		read++
		if mood > threshold+moodEntryMargin {
			continue
		}
		total := 0.0
		byGoal := map[ConcernID]float64{}
		for _, t := range lost {
			total += t.Offset
			for _, goal := range thoughtOwners(t.Def) {
				byGoal[goal] += t.Offset
			}
		}
		if total >= 0 {
			continue
		}
		for goal, offset := range byGoal {
			weights[goal] += offset / total
		}
	}
	if read == 0 || len(weights) == 0 {
		return nil
	}
	for goal := range weights {
		weights[goal] /= float64(read)
	}
	return weights
}

// WithoutProvision is the same state with its provisioning dropped: the
// relief planner falls back to it when no owner goal is active to provision.
func (s MoodState) WithoutProvision() MoodState {
	s.Provision = nil
	return s
}
