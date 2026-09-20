package buildingruntime

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// failingBuildingSource refuses every native read, standing in for a planner
// whose bridge tool is missing or refused (#62).
type failingBuildingSource struct{ err error }

func (f failingBuildingSource) Identity(context.Context) (*l.IdentityReply, bridge.Result, error) {
	return nil, bridge.Result{}, f.err
}
func (f failingBuildingSource) ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error) {
	return nil, bridge.Result{}, f.err
}
func (f failingBuildingSource) PreviewBuilding(context.Context, domain.Action, domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	return bridge.BuildingPreview{}, bridge.Result{}, f.err
}

// A planner whose native read fails is reported in PlannerFailures, but the
// step still evaluates the clock window and starts it on what the other
// planners committed (#62).
func TestClockSchedulerIsolatesFailingPlanner(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	schedulerRoutine(t, s, f)
	refused := errors.New("bridge read refused: games_tool_detail")
	planner, err := NewRoutineSleepingPlanner(s.config.Routine, failingBuildingSource{refused})
	if err != nil {
		t.Fatal(err)
	}
	config := s.config
	config.Sleeping = planner
	replacement, err := NewClockScheduler(s.player, s.session, f, config, s.clock)
	if err != nil {
		t.Fatal(err)
	}
	*s = *replacement
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if got.Sleeping != nil || len(got.PlannerFailures) != 1 || !errors.Is(got.PlannerFailures[0], refused) || !strings.HasPrefix(got.PlannerFailures[0].Error(), "sleeping: ") {
		t.Fatal(got.Sleeping, got.PlannerFailures)
	}
	second, err := s.StepWithReason(context.Background(), StepReason{Cause: StepTimer})
	if err != nil || !second.Running || len(second.PlannerFailures) != 0 {
		t.Fatal(second, err)
	}
	// A live review under the running window isolates the failure the same
	// way and leaves the window running (#243).
	live, err := s.Step(context.Background())
	if err != nil || !live.Running || live.Reason.Cause != StepLive || len(live.PlannerFailures) != 1 || !errors.Is(live.PlannerFailures[0], refused) {
		t.Fatal(live, err)
	}
}

func TestPlannerGroupIsolatesFailuresUntilContextEnds(t *testing.T) {
	t.Parallel()
	g := newPlannerGroup(context.Background(), 2)
	boom := errors.New("boom")
	g.Go("boom", classOptional, plannerFoothold, func() error { return boom })
	g.Go("fine", classOptional, plannerFoothold, func() error { return nil })
	if err := g.Wait(); err != nil {
		t.Fatal(err)
	}
	if failures := g.Failures(); len(failures) != 1 || failures[0] != boom {
		t.Fatal(failures)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancelled := newPlannerGroup(ctx, 1)
	cancelled.Go("first", classCritical, plannerPreempt, func() error { cancel(); return boom })
	ran := false
	cancelled.Go("second", classOptional, plannerComfort, func() error { ran = true; return nil })
	if err := cancelled.Wait(); !errors.Is(err, context.Canceled) || !errors.Is(err, boom) {
		t.Fatal(err)
	}
	if ran {
		t.Fatal("planner queued behind a finished step context still ran")
	}
}

// Planners run lowest priority class first, queue order breaking ties, so a
// tight step budget reaches naming/combat and critical medicine before
// comfort and expansion (#76).
func TestPlannerGroupAdmitsByPriorityThenQueueOrder(t *testing.T) {
	t.Parallel()
	g := newPlannerGroup(context.Background(), 1)
	var order []string
	queue := func(name string, priority int) {
		g.Go(name, classOptional, priority, func() error { order = append(order, name); return nil })
	}
	queue("comfort", plannerComfort)
	queue("work", plannerFoothold)
	queue("naming", plannerPreempt)
	queue("fields", plannerFoothold)
	queue("gear", plannerMaintenance)
	queue("rescue", plannerCritical)
	if err := g.Wait(); err != nil {
		t.Fatal(err)
	}
	if strings.Join(order, " ") != "naming rescue work fields gear comfort" {
		t.Fatal(order)
	}
}

// A slot is taken before the next planner starts, so a lower priority
// planner waits for a running higher priority one rather than racing it for
// the bridge gate.
func TestPlannerGroupHoldsLowPriorityUntilASlotFrees(t *testing.T) {
	t.Parallel()
	g := newPlannerGroup(context.Background(), 2)
	gate := make(chan struct{})
	started := make(chan string, 3)
	hold := func(name string, priority int) {
		g.Go(name, classOptional, priority, func() error {
			started <- name
			<-gate
			return nil
		})
	}
	hold("comfort", plannerComfort)
	hold("naming", plannerPreempt)
	hold("rescue", plannerCritical)
	done := make(chan error, 1)
	go func() { done <- g.Wait() }()
	first, second := <-started, <-started
	if !(first == "naming" && second == "rescue" || first == "rescue" && second == "naming") {
		t.Fatal(first, second)
	}
	select {
	case name := <-started:
		t.Fatal("third planner started before a slot freed:", name)
	case <-time.After(50 * time.Millisecond):
	}
	close(gate)
	if name := <-started; name != "comfort" {
		t.Fatal(name)
	}
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

// A planner that fails on a native refusal leaves no work and no wait; with
// nothing else to do the clock would park on no_work while the same read is
// refused every step. The step lends one stock-sized window instead, and a
// failure that is not a native refusal still does not (#219).
func TestClockSchedulerLendsWindowToPlannerRefusedNatively(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name  string
		err   error
		lends bool
	}{
		{"native refusal", &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("bench is not usable for bills")}}, true},
		{"other failure", errors.New("boom"), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			s, f := schedulerFixture(t)
			schedulerRoutine(t, s, f)
			planner, err := NewRoutineSleepingPlanner(s.config.Routine, failingBuildingSource{tc.err})
			if err != nil {
				t.Fatal(err)
			}
			config := s.config
			config.Sleeping = planner
			config.RoutineMethods = true
			s.session.routineMethods = true
			replacement, err := NewClockScheduler(s.player, s.session, f, config, s.clock)
			if err != nil {
				t.Fatal(err)
			}
			*s = *replacement
			root := s.session.State().Snapshot
			p, err := s.player.journal.LoadPlan(context.Background(), root.Plan)
			if err != nil {
				t.Fatal(err)
			}
			for _, action := range p.Spec.Actions() {
				if _, err = s.player.journal.Cancel(context.Background(), p.Spec.ID(), action.ID()); err != nil {
					t.Fatal(err)
				}
			}
			got, err := s.Step(context.Background())
			if len(got.PlannerFailures) != 1 || !errors.Is(got.PlannerFailures[0], tc.err) {
				t.Fatal(got.PlannerFailures)
			}
			if !tc.lends {
				if !errors.Is(err, executor.ErrHeld) || got.Attempt != nil || f.writes != 0 || got.Decision.Admitted {
					t.Fatal(got, err, f.writes)
				}
				return
			}
			if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
				t.Fatal(got, err, f.writes)
			}
			if ticks := got.Attempt.Intent.Command.Start.MaxTicks; ticks == 0 || ticks != min(got.Window.Ticks, stockWaitTicks) {
				t.Fatalf("refused planner lent %d ticks, want %d", ticks, min(got.Window.Ticks, stockWaitTicks))
			}
		})
	}
}
