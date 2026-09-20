package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestParseFaults(t *testing.T) {
	t.Parallel()
	got, err := ParseFaults(" planner:lighting=fail; planner:defense=hang ;renewal=drop")
	if err != nil {
		t.Fatal(err)
	}
	want := Faults{FailPlanners: map[string]bool{"lighting": true}, HangPlanners: map[string]bool{"defense": true}, DropRenewal: true}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", got)
	}
	if err := got.Validate(); err != nil {
		t.Fatal(err)
	}
	if empty, err := ParseFaults(""); err != nil || !empty.Empty() {
		t.Fatal(empty, err)
	}
	for _, bad := range []string{"lighting", "planner:=fail", "planner:lighting=slow", "renewal=keep"} {
		if _, err := ParseFaults(bad); err == nil {
			t.Fatalf("%q parsed", bad)
		}
	}
	if err := (Faults{FailPlanners: map[string]bool{"nosuch": true}}).Validate(); err == nil || !strings.Contains(err.Error(), "nosuch") {
		t.Fatal(err)
	}
}

// A planner faulted to fail is an isolated failure (#62): the step still
// admits its window and names the fault.
func TestClockSchedulerFailFaultIsIsolated(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.config.Faults = Faults{FailPlanners: map[string]bool{"lighting": true}}
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical), quickPlanner("lighting", classOptional)}
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if len(got.PlannerFailures) != 1 || !errors.Is(got.PlannerFailures[0], errFaultInjected) || !strings.Contains(got.PlannerFailures[0].Error(), "lighting") {
		t.Fatalf("failures %v", got.PlannerFailures)
	}
}

// A critical planner faulted to hang holds admission past the wall budget
// (#623): the step admits nothing and names it under held_by.
func TestClockSchedulerHangFaultOnCriticalHolds(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.config.Budget.Wall = 50 * time.Millisecond
	s.config.Faults = Faults{HangPlanners: map[string]bool{"tend": true}}
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical)}
	got, err := s.Step(context.Background())
	if !errors.Is(err, executor.ErrHeld) || got.Attempt != nil || f.writes != 0 || got.Decision.Admitted {
		t.Fatal(got, err, f.writes)
	}
	if !reflect.DeepEqual(got.HeldBy, []string{"tend"}) {
		t.Fatalf("held %v", got.HeldBy)
	}
}
