package buildingruntime

import (
	"context"
	"errors"
	"strings"
	"testing"

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
	g := newPlannerGroup(context.Background())
	boom := errors.New("boom")
	g.Go(func() error { return boom })
	g.Go(func() error { return nil })
	if err := g.Wait(); err != nil {
		t.Fatal(err)
	}
	if failures := g.Failures(); len(failures) != 1 || failures[0] != boom {
		t.Fatal(failures)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancelled := newPlannerGroup(ctx)
	cancelled.Go(func() error { cancel(); return boom })
	if err := cancelled.Wait(); !errors.Is(err, context.Canceled) || !errors.Is(err, boom) {
		t.Fatal(err)
	}
}
