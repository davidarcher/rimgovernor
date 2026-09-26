package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// YieldDevelopment lets a planner whose selected goal has no method this
// review (retries exhausted, every fallback refused) hand its unused slot
// on within the same review, without inflating waiting age. The yielding
// row reads method_unavailable and is idle, so the next review ranks it
// behind the goals it yielded to. Its labor is released, and the capacity-
// and labor-deferred rows are rechecked in rank order against the same
// holds and fit the ranking used, so a stage-held, risky or otherwise
// refused row never takes the slot; each that fits while a slot is free
// is Granted, and the next review does not judge it idle for a slot its
// planner may never have run under. Regrants are bounded by
// MaxDevelopmentYields per review; past it the yield grants nothing and
// records DevelopmentYieldBound, and the next review ranks again. A goal
// that is not selected yields nothing.
func YieldDevelopment(state DevelopmentState, goal GoalID) DevelopmentState {
	state.Rows = append([]DevelopmentRow(nil), state.Rows...)
	state.Committed = append([]GoalID(nil), state.Committed...)
	state.Holds = append([]DevelopmentHold(nil), state.Holds...)
	yielder := -1
	for i := range state.Rows {
		if state.Rows[i].Goal == goal && state.Rows[i].Selected {
			yielder = i
		}
	}
	if yielder < 0 {
		return state
	}
	row := &state.Rows[yielder]
	row.Selected, row.Granted, row.Reason, row.Idle = false, false, DevelopmentMethodUnavailable, true
	defer summarizeDevelopment(&state)
	if state.Yields >= MaxDevelopmentYields {
		state.Continuation = DevelopmentYieldBound
		return state
	}
	state.Yields++
	var chosen []LaborProfile
	for _, r := range state.Rows {
		if r.Selected {
			chosen = append(chosen, r.Labor)
		}
	}
	free := max(0, state.Capacity-slotHolds(state.Holds)-len(chosen))
	if _, _, paused := holdsFit(state, nil); paused {
		free = 0
	}
	for j := range state.Rows {
		r := &state.Rows[j]
		if free == 0 {
			break
		}
		if r.Reason != DevelopmentCapacity && r.Reason != DevelopmentLabor || state.StageHold && StageDevelopmentGoal(r.Goal) {
			continue
		}
		profile := r.Labor
		fits, _, _ := holdsFit(state, append(chosen[:len(chosen):len(chosen)], profile))
		if !fits[len(fits)-1] {
			continue
		}
		chosen = append(chosen, profile)
		r.Selected, r.Granted, r.Reason, r.Bottleneck = true, true, "", ""
		free--
	}
	return state
}

// summarizeDevelopment records the diagnostics the rows imply: the first
// limiting reason and, in automatic mode with a known census, the workers
// no hold or selection took.
func summarizeDevelopment(s *DevelopmentState) {
	s.Limiting = ""
	for _, row := range s.Rows {
		switch row.Reason {
		case DevelopmentCapacity, DevelopmentLabor, DevelopmentStage, DevelopmentOvercommitted, DevelopmentWorkersUnknown, DevelopmentNoWorkers:
			if s.Limiting == "" {
				s.Limiting = row.Reason
			}
		}
	}
	s.Unused = domain.Unknown[int]()
	census, known := s.Census.Value()
	if !s.Auto || !known {
		return
	}
	var demands []LaborProfile
	for _, h := range s.Holds {
		demands = append(demands, h.Labor)
	}
	for _, row := range s.Rows {
		if row.Selected {
			demands = append(demands, row.Labor)
		}
	}
	fits, _ := developmentFit(true, s.Census, s.Labor, demands)
	used := 0
	for i, d := range demands {
		if fits[i] && len(d) > 0 {
			used++
		}
	}
	s.Unused = domain.Known(max(0, len(census)-used))
}
