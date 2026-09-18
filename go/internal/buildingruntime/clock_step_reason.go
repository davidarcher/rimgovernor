package buildingruntime

import (
	"context"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// StepCause is why the clock worker stepped the scheduler.
type StepCause string

const (
	// StepTimer is the worker's cadence firing with nothing captured. It
	// runs planners only when the game tick moved since the last step;
	// otherwise the step is the admission tail alone.
	StepTimer StepCause = "timer"
	// StepWake is committed poll evidence: the planners that dispatch the
	// latched outcomes' kinds and the planners that read an invalidated
	// family run; an authority change runs every planner.
	StepWake StepCause = "wake"
	// StepSettled follows a step that reconciled or cleaned an epoch: a
	// window just ran, so every planner re-plans.
	StepSettled StepCause = "settled"
	// StepFull runs every planner regardless of evidence; it is also the
	// FullStepEvery safety net a timer step is promoted to.
	StepFull StepCause = "full"
)

// DefaultFullStepEvery bounds how long timer steps may skip the planners
// before one is promoted to a full step.
const DefaultFullStepEvery = 30 * time.Second

// StepReason is the evidence one step acts on. The worker fills Cause and
// the wake fields; the scheduler fills TickAdvanced from its status read and
// reports the reason it actually applied on ClockSchedulerResult.Reason.
type StepReason struct {
	Cause StepCause
	// Events are the latched outcomes a wake carried.
	Events []WakeOutcome
	// Families are the fact families a wake's ObservationInvalidated
	// events named.
	Families []bridge.FactFamily
	// Authority is set when a wake carried an AuthorityChanged event.
	Authority bool
	// TickAdvanced reports whether the game tick moved since the previous
	// step's status read (true on the first step).
	TickAdvanced bool
	// Stopped is set when the wake's committed pages stopped the clock;
	// StopAt is the earliest such stop's native stamp (zero when unknown),
	// from which the step publishes its stop latency (issue #112).
	Stopped bool
	StopAt  time.Time
}

func (r StepReason) String() string {
	var b strings.Builder
	b.WriteString(string(r.Cause))
	if r.TickAdvanced {
		b.WriteString(" tick_advanced")
	}
	if r.Authority {
		b.WriteString(" authority")
	}
	if r.Stopped {
		b.WriteString(" stopped")
	}
	for _, event := range r.Events {
		b.WriteString(" ")
		b.WriteString(string(event.Action))
	}
	for _, family := range r.Families {
		b.WriteString(" ")
		b.WriteString(string(family))
	}
	return b.String()
}

// plannerSelection decides which catalog planners a step runs. It returns
// whether any planner (and the routine reviewer before it) runs at all, and
// the catalog filter when only a subset does (nil selects every planner).
// kindOf resolves a woken action to the kind the scheduler remembered
// arming; an unremembered action could be any kind, so it selects all.
func plannerSelection(reason StepReason, kindOf func(domain.ActionID) (domain.ActionKind, bool)) (planners bool, pick func(plannerEntry) bool) {
	switch reason.Cause {
	case StepFull, StepSettled:
		return true, nil
	case StepTimer:
		return reason.TickAdvanced, nil
	case StepWake:
	default:
		return true, nil
	}
	if reason.Authority {
		return true, nil
	}
	kinds := map[domain.ActionKind]bool{}
	for _, event := range reason.Events {
		kind, known := kindOf(event.Action)
		if !known {
			return true, nil
		}
		kinds[kind] = true
	}
	families := map[bridge.FactFamily]bool{}
	for _, family := range reason.Families {
		families[family] = true
	}
	if len(kinds) == 0 && len(families) == 0 {
		// A wake that carried nothing selectable (a watch stop without an
		// outcome) is a settled window: re-plan everything.
		return true, nil
	}
	return true, func(entry plannerEntry) bool {
		for _, kind := range entry.kinds {
			if kinds[kind] {
				return true
			}
		}
		for _, family := range entry.families {
			if families[family] {
				return true
			}
		}
		return false
	}
}

// Step runs one full scheduling decision; see StepWithReason.
func (s *ClockScheduler) Step(ctx context.Context) (ClockSchedulerResult, error) {
	return s.StepWithReason(ctx, StepReason{Cause: StepFull})
}
