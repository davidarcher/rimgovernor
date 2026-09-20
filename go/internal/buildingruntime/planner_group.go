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

// plannerClass says whether a clock admission waits on the planner (#623).
// The admission cycle joins the critical class only: authority, emergency
// evidence and the planners whose verdict the window decision reads. The
// optional class is the development reviews; they run on the same snapshot
// and an evaluation still running at the cutoff is recorded missed, never
// waited for.
type plannerClass string

const (
	classCritical plannerClass = "critical"
	classOptional plannerClass = "optional"
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
// Go only queues; Start runs the queue critical class first, then priority
// order (stable, so queue order breaks ties) with at most width planners at
// once, taking a slot before the next planner starts. Native reads execute
// one at a time on the game's main thread, so the wave overlaps round trips,
// and the order slots are taken decides which goals' reads go first when the
// step budget is tight (#76). WaitCritical returns once every critical
// planner has returned; WaitUntil joins the rest up to a cutoff.
type plannerGroup struct {
	ctx      context.Context
	width    int
	queued   []queuedPlanner
	mu       sync.Mutex
	failures []error
	// names is every planner queued, in queue order; pending those not
	// yet returned (or skipped); criticalLeft counts the critical ones
	// among them and critical is closed when it reaches zero.
	names        []string
	pending      map[string]bool
	criticalLeft int
	critical     chan struct{}
	all          chan struct{}
	started      bool
}

type queuedPlanner struct {
	name     string
	class    plannerClass
	priority int
	run      func() error
}

func newPlannerGroup(ctx context.Context, width int) *plannerGroup {
	return &plannerGroup{ctx: ctx, width: max(1, width), pending: map[string]bool{}, critical: make(chan struct{}), all: make(chan struct{})}
}

// Go queues fn as the named planner of class at priority. Each fn wraps its
// planner's error with the planner's name, so a recorded failure names its
// source.
func (g *plannerGroup) Go(name string, class plannerClass, priority int, fn func() error) {
	g.queued = append(g.queued, queuedPlanner{name, class, priority, fn})
	g.names = append(g.names, name)
	g.pending[name] = true
	if class == classCritical {
		g.criticalLeft++
	}
}

// Start runs the queue in the background: critical planners first, then by
// priority, each taking a slot before the next starts. A planner queued
// behind a finished step context is skipped, not run.
func (g *plannerGroup) Start() {
	if g.started {
		return
	}
	g.started = true
	ordered := append([]queuedPlanner(nil), g.queued...)
	g.queued = nil
	sort.SliceStable(ordered, func(i, j int) bool {
		if (ordered[i].class == classCritical) != (ordered[j].class == classCritical) {
			return ordered[i].class == classCritical
		}
		return ordered[i].priority < ordered[j].priority
	})
	if g.criticalLeft == 0 {
		close(g.critical)
	}
	if len(ordered) == 0 {
		close(g.all)
		return
	}
	go func() {
		slots := make(chan struct{}, g.width)
		var wg sync.WaitGroup
		for i, planner := range ordered {
			select {
			case slots <- struct{}{}:
			case <-g.ctx.Done():
			}
			if g.ctx.Err() != nil {
				for _, skipped := range ordered[i:] {
					g.finish(skipped, nil)
				}
				break
			}
			wg.Add(1)
			go func() {
				defer wg.Done()
				defer func() { <-slots }()
				g.finish(planner, planner.run())
			}()
		}
		wg.Wait()
		close(g.all)
	}()
}

// finish records a planner's return (or skip) and releases the waiters it
// was the last of.
func (g *plannerGroup) finish(planner queuedPlanner, err error) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if err != nil {
		g.failures = append(g.failures, err)
	}
	delete(g.pending, planner.name)
	if planner.class == classCritical {
		if g.criticalLeft--; g.criticalLeft == 0 {
			close(g.critical)
		}
	}
}

// WaitCritical starts the wave if needed and blocks until every critical
// planner has returned, the step context ends, or cutoff is done. It
// returns the step context's error when that is what cut the wave short,
// errCutoff when the cutoff came first, else nil.
func (g *plannerGroup) WaitCritical(cutoff <-chan struct{}) error {
	g.Start()
	select {
	case <-g.critical:
		return nil
	case <-g.ctx.Done():
		<-g.critical
		return g.contextError()
	case <-cutoff:
		return errCutoff
	}
}

// WaitUntil blocks until every planner has returned or cutoff is done, and
// returns the names of the planners still pending, in queue order (none
// when the wave completed).
func (g *plannerGroup) WaitUntil(cutoff <-chan struct{}) []string {
	g.Start()
	select {
	case <-g.all:
		return nil
	case <-cutoff:
		return g.Pending()
	}
}

// Wait runs every queued planner and blocks until each returns. It returns
// the step context's error when that is what cut the wave short; otherwise
// the isolated failures are left in Failures and Wait returns nil.
func (g *plannerGroup) Wait() error {
	g.Start()
	<-g.all
	return g.contextError()
}

func (g *plannerGroup) contextError() error {
	if err := g.ctx.Err(); err != nil {
		return errors.Join(append([]error{err}, g.Failures()...)...)
	}
	return nil
}

// errCutoff reports a wait ended by its cutoff rather than by the planners.
var errCutoff = errors.New("planner wave cutoff")

// Pending names the planners not yet returned, in queue order.
func (g *plannerGroup) Pending() []string {
	g.mu.Lock()
	defer g.mu.Unlock()
	var names []string
	for _, planner := range g.names {
		if g.pending[planner] {
			names = append(names, planner)
		}
	}
	return names
}

// Failures lists every isolated planner failure of this wave, in completion order.
func (g *plannerGroup) Failures() []error {
	g.mu.Lock()
	defer g.mu.Unlock()
	return append([]error(nil), g.failures...)
}
