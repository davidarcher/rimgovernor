package buildingruntime

import (
	"context"
	"errors"
	"sort"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// plannerWidth bounds how many planners a step runs at once. It matches the
// bridge's in-flight call cap so admission order, not gate queueing, decides
// whose native reads go first.
const plannerWidth = bridge.MaxConcurrentCalls

// Planner priorities, the priority class the planner's goal usually carries
// in policy.DetectRoutine. A tight step budget is spent lowest-first.
const (
	plannerPreempt     = 0 // naming, active combat
	plannerCritical    = 1 // critical medicine, disaster recovery
	plannerFoothold    = 2 // work, food, shelter, warmth, cooking, power, storage
	plannerMaintenance = 3 // maintenance and development
	plannerComfort     = 4 // comfort and expansion
)

// plannerGroup runs one clock step's routine planners as a concurrent wave,
// like errgroup.Group, but a planner's failure is isolated: it is recorded in
// Failures instead of cancelling its peers or failing Wait. One broken planner
// (a refused native read, a missing tool) must not stop the whole clock step
// from reaching EvaluateClockWindow, or the clock never starts while that
// family is composed (#62). The step's own context is the only thing that
// aborts the wave: once it is done, every planner fails for the same reason
// and Wait returns that error.
//
// Go only queues; Wait runs the queue in priority order (stable, so queue
// order breaks ties) with at most width planners at once, taking a slot
// before the next planner starts. Native reads execute one at a time on the
// game's main thread, so the wave overlaps round trips, and the order slots
// are taken decides which goals' reads go first when the step budget is
// tight (#76).
type plannerGroup struct {
	ctx      context.Context
	width    int
	queued   []queuedPlanner
	mu       sync.Mutex
	failures []error
}

type queuedPlanner struct {
	priority int
	run      func() error
}

func newPlannerGroup(ctx context.Context, width int) *plannerGroup {
	return &plannerGroup{ctx: ctx, width: max(1, width)}
}

// Go queues fn at priority. Each fn wraps its planner's error with the
// planner's name, so a recorded failure names its source.
func (g *plannerGroup) Go(priority int, fn func() error) {
	g.queued = append(g.queued, queuedPlanner{priority, fn})
}

// Wait runs every queued planner and blocks until each returns. It returns
// the step context's error when that is what cut the wave short; otherwise
// the isolated failures are left in Failures and Wait returns nil.
func (g *plannerGroup) Wait() error {
	ordered := append([]queuedPlanner(nil), g.queued...)
	g.queued = nil
	sort.SliceStable(ordered, func(i, j int) bool { return ordered[i].priority < ordered[j].priority })
	slots := make(chan struct{}, g.width)
	var wg sync.WaitGroup
	for _, planner := range ordered {
		select {
		case slots <- struct{}{}:
		case <-g.ctx.Done():
		}
		if g.ctx.Err() != nil {
			break
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer func() { <-slots }()
			if err := planner.run(); err != nil {
				g.mu.Lock()
				g.failures = append(g.failures, err)
				g.mu.Unlock()
			}
		}()
	}
	wg.Wait()
	if err := g.ctx.Err(); err != nil {
		return errors.Join(append([]error{err}, g.Failures()...)...)
	}
	return nil
}

// Failures lists every isolated planner failure of this wave, in completion order.
func (g *plannerGroup) Failures() []error {
	g.mu.Lock()
	defer g.mu.Unlock()
	return append([]error(nil), g.failures...)
}
