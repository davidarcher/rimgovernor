package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func schedulerSleeping(t *testing.T, s *ClockScheduler, f *schedulerNative) *sleepingNative {
	t.Helper()
	n := schedulerRoutine(t, s, f)
	_, _, _, _, template := sleepingFixture(t)
	planning := proto.Clone(template.reply.GetObserved().Planning.GetObserved()).(*o.PlanningFacts)
	planning.Cells.Context = proto.Clone(f.status.Context).(*c.ObservationContext)
	n.reply.GetObserved().Planning = &o.PlanningSection{Outcome: &o.PlanningSection_Observed{Observed: planning}}
	n.reply.GetObserved().Center = proto.Clone(template.reply.GetObserved().Center).(*c.Cell)
	source := &sleepingNative{routineNative: n}
	planner, err := NewRoutineSleepingPlanner(s.config.Routine, source)
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
	return source
}

// The scheduler compiles the sleeping method at the review and never
// executes it; a timer step under the window it started reads nothing until
// the full step is due (#243).
func TestSchedulerCompilesSleepingAtTheReview(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerSleeping(t, s, f)
	result, err := s.Step(context.Background())
	if err != nil || result.Sleeping == nil || result.Sleeping.Reason != BuildingMethodAdmitted || f.writes != 1 {
		t.Fatal(result, err, f.writes)
	}
	plan, err := s.player.journal.LoadPlan(context.Background(), result.Sleeping.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 2 {
		t.Fatal(plan, err)
	}
	for _, p := range plan.Progress {
		if p.View().Stage != domain.Pending || p.View().Attempt != 0 {
			t.Fatal("scheduler executed method", p)
		}
	}
	reads, previews := n.reads, n.previews
	next, err := s.StepWithReason(context.Background(), StepReason{Cause: StepTimer})
	if err != nil || !next.Running || next.Sleeping != nil || n.reads != reads || n.previews != previews {
		t.Fatal(next, err)
	}
}

// A failed sleeping preview commits nothing for that planner, but no longer
// blocks the clock: the failure is isolated and the window still starts (#62).
func TestSchedulerFailedSleepingPreviewIsIsolated(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerSleeping(t, s, f)
	n.onPreview = func(_ context.Context, p *bridge.BuildingPreview) { p.Preview.Tick-- }
	result, err := s.Step(context.Background())
	if err != nil || result.Routine == nil || result.Sleeping != nil || f.writes != 1 {
		t.Fatal(result, err, f.writes)
	}
	if len(result.PlannerFailures) != 1 || !strings.HasPrefix(result.PlannerFailures[0].Error(), "sleeping: ") {
		t.Fatal(result.PlannerFailures)
	}
}

func TestSchedulerRejectsSleepingWithoutMatchingReviewer(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	other, _, _, _, _ := sleepingFixture(t)
	config := s.config
	config.Sleeping = other
	if _, err := NewClockScheduler(s.player, s.session, f, config, s.clock); err == nil {
		t.Fatal("sleeping without reviewer accepted")
	}
	schedulerRoutine(t, s, f)
	config = s.config
	config.Sleeping = other
	if _, err := NewClockScheduler(s.player, s.session, f, config, s.clock); err == nil {
		t.Fatal("different sleeping reviewer accepted")
	}
}

func TestSchedulerCompilesCookingAtPausedBoundary(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerSleeping(t, s, f)
	v := n.reply.GetObserved()
	foodPlanFixture(v)
	issues := v.Issues[:0]
	for _, issue := range v.Issues {
		if issue.GetField() != "cooking" {
			issues = append(issues, issue)
		}
	}
	v.Issues = issues
	v.Planning.GetObserved().Definitions[0].Definition.DefName = proto.String("Campfire")
	config := s.config
	config.Sleeping = nil
	var err error
	config.Cooking, err = NewRoutineCookingPlanner(config.Routine, n)
	if err != nil {
		t.Fatal(err)
	}
	s, err = NewClockScheduler(s.player, s.session, f, config, s.clock)
	if err != nil {
		t.Fatal(err)
	}
	result, err := s.Step(context.Background())
	if err != nil || result.Cooking == nil || result.Cooking.Reason != BuildingMethodAdmitted || result.Sleeping != nil || f.writes != 1 {
		t.Fatal(result, err)
	}
	if config.Cooking.definition != "Campfire" {
		t.Fatal("wrong cooking method")
	}
	reads, previews := n.reads, n.previews
	next, err := s.StepWithReason(context.Background(), StepReason{Cause: StepTimer})
	if err != nil || !next.Running || next.Cooking != nil || reads != n.reads || previews != n.previews {
		t.Fatal(next, err)
	}
}
