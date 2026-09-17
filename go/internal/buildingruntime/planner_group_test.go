package buildingruntime

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
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
	second, err := s.Step(context.Background())
	if err != nil || !second.Running || len(second.PlannerFailures) != 0 {
		t.Fatal(second, err)
	}
}

func TestPlannerGroupIsolatesFailuresUntilContextEnds(t *testing.T) {
	t.Parallel()
	g := newPlannerGroup(context.Background(), 2)
	boom := errors.New("boom")
	g.Go(plannerFoothold, func() error { return boom })
	g.Go(plannerFoothold, func() error { return nil })
	if err := g.Wait(); err != nil {
		t.Fatal(err)
	}
	if failures := g.Failures(); len(failures) != 1 || failures[0] != boom {
		t.Fatal(failures)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancelled := newPlannerGroup(ctx, 1)
	cancelled.Go(plannerPreempt, func() error { cancel(); return boom })
	ran := false
	cancelled.Go(plannerComfort, func() error { ran = true; return nil })
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
		g.Go(priority, func() error { order = append(order, name); return nil })
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
		g.Go(priority, func() error {
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
