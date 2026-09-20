package buildingruntime

import (
	"context"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
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
	// StepLive is a step that planned while its own window was running
	// (#243): the planners read the bundle's tick-consistent snapshot and
	// commit plans the Worker dispatches live; nothing is admitted. A timer
	// step is promoted to it when FullStepEvery has passed, a wake or a
	// full step at once; the selection is the underlying cause's.
	StepLive StepCause = "live"
)

// DefaultFullStepEvery bounds how long the due queue may drive the
// planners before a timer step is promoted to a full one: the coarse
// reconciliation that re-runs every planner in case an invalidation was
// missed (#625). Between full steps a timer step runs only the planners
// due at the game tick (plannerEntry.reviewEvery) or dirtied by a wake.
const DefaultFullStepEvery = 2 * time.Minute

// DefaultLiveWaveEvery spaces the timer-driven planner waves under a
// running window (#243) in wall time whatever the game speed: the due
// queue is keyed by game tick, so at Ultrafast every planner falls due
// within seconds and the waves would otherwise run back to back, each
// holding the player gate the Worker dispatches under. A wake still plans
// live at once (livePlanningPaced bounds the outrun).
const DefaultLiveWaveEvery = 30 * time.Second

// DefaultLivePlanningTicks is the most ticks a running window may cover in
// the wall time of one live planner wave before the wave waits for the
// stop instead (#598): a tenth of a game day, 24 PlanningTickTolerances.
// Capped Ultrafast (900 ticks/s) over a 5 s step covers 4.5k and plans
// live; an uncapped game at 1000 ticks/s over a 10 s step covers 10k and
// waits.
const DefaultLivePlanningTicks domain.Tick = 6000

// DefaultPlayerQuiet is how long after the player's last speed-key press (a
// Manual authority change) a step waits before it re-takes a clock the
// player runs by hand under a stopped epoch (#601): long enough not to
// fight a player still pressing keys, short enough that the next window
// and the test-acceleration boost return within a few seconds.
const DefaultPlayerQuiet = 3 * time.Second

// LivePlanningSkippedPace is ClockSchedulerResult.LivePlanning when the
// live wave waited for the stop because the game outran it (#598).
const LivePlanningSkippedPace = "skipped_pace"

// StepReason is the evidence one step acts on. The worker fills Cause and
// the wake fields; the scheduler fills TickAdvanced from its status read and
// reports the reason it actually applied on ClockSchedulerResult.Reason.
type StepReason struct {
	Cause StepCause
	// Events are the latched outcomes a wake carried.
	Events []WakeOutcome
	// Families are the fact families a wake's ObservationInvalidated
	// events named; Sections the store sections the same events narrowed
	// to (clockPageSections, #625). A reason carrying families alone is
	// taken to dirty every section of theirs.
	Families []bridge.FactFamily
	Sections []facts.Section
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
	for _, section := range r.Sections {
		b.WriteString(" ")
		b.WriteString(string(section))
	}
	return b.String()
}

// plannerSelection decides which catalog planners a step for reason runs
// at tick, over a copy of the due queue q (#625): a full step runs every
// planner; a settled step (a window just ran) marks every planner and
// runs those not waiting on a dependency; a timer step runs the planners
// dirty or due at tick; a wake or live step folds its evidence first (the
// planners of the latched outcomes' kinds, the consumers of the dirty
// sections, everything on an authority change or an outcome whose kind
// kindOf does not remember, everything on a wake carrying nothing
// selectable). The result's pick is nil when every planner runs.
func plannerSelection(reason StepReason, kindOf func(domain.ActionID) (domain.ActionKind, bool), q *plannerQueue, tick int64) plannerSelectionResult {
	q = q.clone()
	switch reason.Cause {
	case StepFull:
		return q.selection(tick, true, false, nil)
	case StepSettled:
		return q.selection(tick, false, true, nil)
	case StepTimer:
		return q.selection(tick, false, false, nil)
	case StepWake, StepLive:
		q.wake(reason, kindOf)
		return q.selection(tick, false, false, nil)
	}
	return q.selection(tick, true, false, nil)
}

// Step runs one full scheduling decision; see StepWithReason.
func (s *ClockScheduler) Step(ctx context.Context) (ClockSchedulerResult, error) {
	return s.StepWithReason(ctx, StepReason{Cause: StepFull})
}
