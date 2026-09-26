package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
)

// A native call drops the operation's families from the worker's fact
// store itself: with no admitted window no clock events page ever arrives
// to drop them, and the next decision would replan from the rows before
// our own write (#694). A dispatch that never reached native keeps them.
func TestWorkerNativeCallDropsWrittenFamilies(t *testing.T) {
	t.Parallel()
	for _, called := range []bool{false, true} {
		w, f, db := workerFixture(t)
		w.config.Store = facts.NewStore()
		scope := facts.Scope{Load: "load", Generation: 1}
		sections := facts.FamilySections(bridge.FactPawns)
		for _, section := range sections {
			facts.Put(w.config.Store, scope, section, facts.Held[int]{AsOf: 100, Complete: true, Source: "x"})
		}
		v := workerPending(t, w, "written", false)
		f.mu.Lock()
		f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
		f.mu.Unlock()
		f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
			plan, err := db.LoadPlan(ctx, p)
			if err != nil {
				return executor.Result{}, err
			}
			return executor.Result{Progress: plan.Progress[0], NativeCalled: called}, nil
		}
		if err := w.step(context.Background(), time.Now()); err != nil {
			t.Fatal(err)
		}
		if f.runs.Load() == 0 {
			t.Fatal("nothing dispatched")
		}
		for _, section := range sections {
			if held := w.config.Store.Held(section, 100); held == called {
				t.Fatalf("native called %v: %s held %v", called, section, held)
			}
		}
	}
}
