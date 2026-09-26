package startuplabor

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LimitObservation is one comparison-mode run: the same fixture, variant
// and seed played under one explicit --routine-project-limit. Every field
// is an observation of that run, not a bound anything is held to.
type LimitObservation struct {
	Limit    int
	Variant  string
	Seed     string
	Revision string
	// FirstEnclosure is the tick the first completed enclosure was
	// observed; unknown when none completed inside the window.
	FirstEnclosure domain.Fact[domain.Tick]
	// ShelterRecovery is the tick shelter capacity covered every
	// colonist; unknown when it never did.
	ShelterRecovery domain.Fact[domain.Tick]
	// Blocked is review time per diagnosis class (BlockedTicks).
	Blocked map[Class]domain.Tick
	// Idle is the run's idle accounting.
	Idle Account
	// WindowTicks is the observed window the run covered.
	WindowTicks domain.Tick
}

// BlockedTicks sums, per class, the review interval each diagnosis stood
// for: a diagnosis speaks for the ticks until the next review of the same
// goal, bounded the same way idle samples are. Diagnoses may arrive in any
// order; the last diagnosis of a goal credits nothing, as nothing observed
// its end.
func BlockedTicks(diagnoses []Diagnosis, window, maxGap domain.Tick) map[Class]domain.Tick {
	if maxGap <= 0 {
		maxGap = DefaultMaxGap
	}
	byGoal := map[domain.GoalID][]Diagnosis{}
	var goals []domain.GoalID
	for _, d := range diagnoses {
		if _, seen := byGoal[d.Goal]; !seen {
			goals = append(goals, d.Goal)
		}
		byGoal[d.Goal] = append(byGoal[d.Goal], d)
	}
	sort.Slice(goals, func(i, j int) bool { return goals[i] < goals[j] })
	out := map[Class]domain.Tick{}
	for _, goal := range goals {
		run := byGoal[goal]
		for i := 0; i+1 < len(run); i++ {
			span := run[i+1].ReviewTick - run[i].ReviewTick
			if span <= 0 {
				// A rewind or a repeated review tick credits nothing.
				continue
			}
			if span > maxGap {
				span = maxGap
			}
			if window > 0 && span > window {
				span = window
			}
			out[run[i].Class] += span
		}
	}
	return out
}

// CompareLimits renders the comparison artifact. It is labelled as
// observations on the named revision: the rows diagnose the cap, they do
// not gate it and they are not a root cause.
func CompareLimits(obs []LimitObservation) map[string]any {
	sorted := append([]LimitObservation(nil), obs...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Limit < sorted[j].Limit })
	rows := make([]map[string]any, 0, len(sorted))
	for _, o := range sorted {
		row := map[string]any{
			"limit": o.Limit, "variant": o.Variant, "seed": o.Seed, "revision": o.Revision,
			"window_ticks": o.WindowTicks, "idle": o.Idle.Row(),
		}
		if v, known := o.FirstEnclosure.Value(); known {
			row["first_enclosure_tick"] = v
		} else {
			row["first_enclosure_tick"] = nil
		}
		if v, known := o.ShelterRecovery.Value(); known {
			row["shelter_recovery_tick"] = v
		} else {
			row["shelter_recovery_tick"] = nil
		}
		blocked := map[string]int64{}
		for c, t := range o.Blocked {
			blocked[string(c)] = int64(t)
		}
		row["blocked_ticks"] = blocked
		rows = append(rows, row)
	}
	return map[string]any{
		"kind":  "startup_labor_limit_comparison",
		"label": "observations on the tested revision; not a performance gate and not a proven root cause",
		"runs":  rows,
	}
}
