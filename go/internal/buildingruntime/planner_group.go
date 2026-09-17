package buildingruntime

import (
	"context"
	"errors"
	"sync"
)

// plannerGroup runs one clock step's routine planners as a concurrent wave,
// like errgroup.Group, but a planner's failure is isolated: it is recorded in
// Failures instead of cancelling its peers or failing Wait. One broken planner
// (a refused native read, a missing tool) must not stop the whole clock step
// from reaching EvaluateClockWindow, or the clock never starts while that
// family is composed (#62). The step's own context is the only thing that
// aborts the wave: once it is done, every planner fails for the same reason
// and Wait returns that error.
type plannerGroup struct {
	ctx      context.Context
	wg       sync.WaitGroup
	mu       sync.Mutex
	failures []error
}

func newPlannerGroup(ctx context.Context) *plannerGroup { return &plannerGroup{ctx: ctx} }

// Go queues fn. Each fn wraps its planner's error with the planner's name, so
// a recorded failure names its source.
func (g *plannerGroup) Go(fn func() error) {
	g.wg.Add(1)
	go func() {
		defer g.wg.Done()
		if err := fn(); err != nil {
			g.mu.Lock()
			g.failures = append(g.failures, err)
			g.mu.Unlock()
		}
	}()
}

// Wait blocks until every queued planner returns. It returns the step
// context's error when that is what cut the wave short; otherwise the isolated
// failures are left in Failures and Wait returns nil.
func (g *plannerGroup) Wait() error {
	g.wg.Wait()
	if err := g.ctx.Err(); err != nil {
		return errors.Join(append([]error{err}, g.failures...)...)
	}
	return nil
}

// Failures lists every isolated planner failure of this wave, in completion order.
func (g *plannerGroup) Failures() []error {
	g.mu.Lock()
	defer g.mu.Unlock()
	return append([]error(nil), g.failures...)
}
