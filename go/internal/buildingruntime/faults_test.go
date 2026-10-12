package buildingruntime

import (
	"context"
	"errors"
	"reflect"
	"strings"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestParseFaults(t *testing.T) {
	t.Parallel()
	got, err := ParseFaults(" planner:lighting=fail; planner:defense=hang ;renewal=drop;foodgap=zero; stock=Steel:30000")
	if err != nil {
		t.Fatal(err)
	}
	want := Faults{FailPlanners: map[string]bool{"lighting": true}, HangPlanners: map[string]bool{"defense": true}, DropRenewal: true, FoodGapZero: true, StockDemand: map[policy.Resource]int64{"Steel": 30000}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("%+v", got)
	}
	if err := got.Validate(); err != nil {
		t.Fatal(err)
	}
	if empty, err := ParseFaults(""); err != nil || !empty.Empty() {
		t.Fatal(empty, err)
	}
	for _, bad := range []string{"lighting", "planner:=fail", "planner:lighting=slow", "renewal=keep", "foodgap=small", "stock=Steel", "stock=Steel:0", "stock=:5", "stock=Steel:x"} {
		if _, err := ParseFaults(bad); err == nil {
			t.Fatalf("%q parsed", bad)
		}
	}
	if err := (Faults{FailPlanners: map[string]bool{"nosuch": true}}).Validate(); err == nil || !strings.Contains(err.Error(), "nosuch") {
		t.Fatal(err)
	}
}

// A planner faulted to fail is an isolated failure: the step still
// admits its window and names the fault.
func TestClockSchedulerFailFaultIsIsolated(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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

// A critical planner faulted to hang holds admission past the wall budget:
// the step admits nothing and names it under held_by.
func TestClockSchedulerHangFaultOnCriticalHolds(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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

// A dropped renewal (authority loss) writes nothing native and leaves the
// owned epoch to lapse: native stops it lease_expired and revokes, so the
// scheduler cannot keep a window running on its own.
func TestClockRenewalDropFaultLetsLeaseLapse(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, n, w, _ := renewalFixture(t)
	s.config.Faults = Faults{DropRenewal: true}
	writes := n.writes
	r, err := s.RenewEpoch(context.Background())
	if err != nil || r.Renewed || r.Attempt != nil || w.renews != 0 || n.writes != writes {
		t.Fatal(r, err, w.renews, n.writes)
	}
}

// The stock fault raises the station chain's targets to its count and leaves
// the review's own demand, which the resource planners dispatch on, untouched.
func TestStationNeedsRaisedByTheStockFault(t *testing.T) {
	t.Parallel()
	snapshot := domain.GenerationSnapshot{Colony: "c"}
	r := &Rounder{}
	r.demand.set(snapshot, policy.DerivedDemand{Needs: map[policy.Resource]int64{"Steel": 10, "WoodLog": 500}})
	if got := r.stationNeeds(snapshot); got["Steel"] != 10 || got["WoodLog"] != 500 {
		t.Fatalf("no fault: %v", got)
	}
	r.stockDemand = map[policy.Resource]int64{"Steel": 30000, "Silver": 7}
	got := r.stationNeeds(snapshot)
	if got["Steel"] != 30000 || got["Silver"] != 7 || got["WoodLog"] != 500 {
		t.Fatalf("raised: %v", got)
	}
	if need := r.resourceTargets(snapshot); need["Steel"] != 10 || len(need) != 2 {
		t.Fatalf("the review's demand changed: %v", need)
	}
}
