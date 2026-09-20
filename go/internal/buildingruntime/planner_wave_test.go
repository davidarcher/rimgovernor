package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Every catalog entry carries a class, and the class follows the rule the
// admission cycle relies on (#623): the preempt and critical priority
// classes and the emergency evidence (fire safety) are critical, every
// development review is optional.
func TestPlannerCatalogClasses(t *testing.T) {
	t.Parallel()
	var critical []string
	for _, entry := range plannerCatalog {
		want := classOptional
		if entry.priority <= plannerCritical || entry.name == "fireSafety" {
			want = classCritical
		}
		if entry.class != want {
			t.Fatalf("%s is %q, want %q", entry.name, entry.class, want)
		}
		if entry.class == classCritical {
			critical = append(critical, entry.name)
		}
	}
	want := []string{"idleDrafts", "supplies", "hospital", "sleepingUpkeep", "defense", "medical", "tend", "rescue", "fireSafety", "recovery", "populationCustody", "naming", "dialog"}
	if !reflect.DeepEqual(critical, want) {
		t.Fatalf("critical planners %v, want %v", critical, want)
	}
}

// blockedPlanner is a catalog entry whose run never returns on its own: it
// waits on the planner's context (a native read that is never answered)
// and reports how it was released.
func blockedPlanner(name string, class plannerClass, released chan<- error) plannerEntry {
	return plannerEntry{name: name, class: class, priority: plannerFoothold,
		configured: func(*ClockSchedulerConfig) bool { return true },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (RoutineBuildingReason, error) {
			<-ctx.Done()
			released <- ctx.Err()
			out.Lighting = &RoutineBuildingResult{Reason: BuildingMethodRefused}
			return "", ctx.Err()
		}}
}

// quickPlanner is a catalog entry that returns at once with a result.
func quickPlanner(name string, class plannerClass) plannerEntry {
	return plannerEntry{name: name, class: class, priority: plannerCritical,
		configured: func(*ClockSchedulerConfig) bool { return true },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (RoutineBuildingReason, error) {
			out.Sleeping = &RoutineBuildingResult{Reason: BuildingMethodNoDeficit}
			return BuildingMethodNoDeficit, nil
		}}
}

// An optional planner blocked on a read that is never answered does not
// hold admission (#623): the step admits its window once the critical wave
// has returned and the grace has passed, lists the planner under
// missed_cutoff, discards its result and cancels it, and does not report
// the cancellation as a failure. It runs again next step.
func TestClockSchedulerAdmitsPastBlockedOptionalPlanner(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.config.Budget.OptionalGrace = 20 * time.Millisecond
	released := make(chan error, 2)
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical), blockedPlanner("lighting", classOptional, released)}
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if !reflect.DeepEqual(got.MissedCutoff, []string{"lighting"}) || len(got.HeldBy) != 0 || len(got.PlannerFailures) != 0 {
		t.Fatalf("missed %v held %v failures %v", got.MissedCutoff, got.HeldBy, got.PlannerFailures)
	}
	if got.Sleeping == nil || got.Sleeping.Reason != BuildingMethodNoDeficit || got.Lighting != nil {
		t.Fatalf("critical result must be merged and the missed planner's discarded: %+v %+v", got.Sleeping, got.Lighting)
	}
	if !reflect.DeepEqual(got.Planners, []string{"tend", "lighting"}) {
		t.Fatal(got.Planners)
	}
	// The planner was released by the cutoff's cancellation, not by the
	// step ending: it had returned before the window was admitted.
	select {
	case cause := <-released:
		if !errors.Is(cause, context.Canceled) {
			t.Fatal(cause)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("blocked optional planner never released")
	}
	if second, err := s.Step(context.Background()); err != nil || !second.Running {
		t.Fatal(second, err)
	}
}

// A critical planner blocked the same way holds admission (#623): past the
// wall budget the step admits no window, reports the planner under
// held_by, and the next step evaluates again.
func TestClockSchedulerHoldsOnBlockedCriticalPlanner(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.config.Budget.Wall = 50 * time.Millisecond
	released := make(chan error, 1)
	s.catalog = []plannerEntry{blockedPlanner("tend", classCritical, released)}
	got, err := s.Step(context.Background())
	if !errors.Is(err, executor.ErrHeld) || got.Attempt != nil || f.writes != 0 || got.Decision.Admitted {
		t.Fatal(got, err, f.writes)
	}
	if !reflect.DeepEqual(got.HeldBy, []string{"tend"}) || len(got.MissedCutoff) != 0 || len(got.PlannerFailures) != 0 {
		t.Fatalf("held %v missed %v failures %v", got.HeldBy, got.MissedCutoff, got.PlannerFailures)
	}
	// The planner ran under the step context and was released only by the
	// step ending, after the hold was reported.
	select {
	case cause := <-released:
		if !errors.Is(cause, context.Canceled) {
			t.Fatal(cause)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("blocked critical planner never released")
	}
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical)}
	again, err := s.Step(context.Background())
	if err != nil || again.Attempt == nil || again.Attempt.Phase != store.ClockApplied || f.writes != 1 || len(again.HeldBy) != 0 {
		t.Fatal(again, err, f.writes)
	}
}

// A proposal that reached its step's arbiter after the cutoff is carried to
// the next step's coordinator and revalidated there (#623): against a
// newer snapshot it is refused with the stale dependency named and its
// commit never runs; against the same one it commits.
func TestClockSchedulerRefusesLateProposalAgainstNewerSnapshot(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.catalog = nil
	current := s.session.State().Snapshot
	committed := 0
	late := func(id string, snapshot domain.GenerationSnapshot, tick domain.Tick) *Proposal {
		p := &Proposal{ID: id, Planner: "lighting", Goal: domain.GoalID(id), Priority: plannerMaintenance, Snapshot: snapshot, ValidTick: tick}
		p.commit = func(context.Context) (domain.PlanID, RoutineBuildingReason, error) {
			committed++
			return domain.PlanID("plan-" + id), BuildingMethodAdmitted, nil
		}
		return p
	}
	stale := current
	stale.Native++
	tick := domain.Tick(f.status.Context.GetTick())
	arbiter := newStepArbiter()
	arbiter.late = s.late
	arbiter.close()
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("generation", stale, tick)}, nil)
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("tick", current, tick-domain.Tick(bridge.PlanningTickTolerance())-1)}, nil)
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("valid", current, tick)}, nil)
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied {
		t.Fatal(got, err)
	}
	if len(got.Proposals) != 3 || committed != 1 {
		t.Fatalf("proposals %+v committed %d", got.Proposals, committed)
	}
	byID := map[string]ProposalOutcome{}
	for _, outcome := range got.Proposals {
		byID[outcome.Proposal] = outcome
	}
	if o := byID["generation"]; o.Admitted || o.Reason != BuildingMethodExpired || o.Stale != fmt.Sprintf("native generation %d, step is %d", stale.Native, current.Native) {
		t.Fatalf("stale generation: %+v", o)
	}
	if o := byID["tick"]; o.Admitted || o.Reason != BuildingMethodExpired || o.Stale == "" {
		t.Fatalf("stale tick: %+v", o)
	}
	if o := byID["valid"]; !o.Admitted || o.Plan != "plan-valid" || o.Stale != "" {
		t.Fatalf("valid proposal must commit: %+v", o)
	}
	// Carried once: the next step's coordinator sees nothing of them.
	next, err := s.Step(context.Background())
	if err != nil || len(next.Proposals) != 0 || committed != 1 {
		t.Fatal(next, err, committed)
	}
}

// A result proposed after the arbiter closed without a carry is dropped,
// and a non-proposal result is never carried.
func TestStepArbiterDropsLateResultsWithoutCarry(t *testing.T) {
	t.Parallel()
	a := newStepArbiter()
	a.close()
	settled := false
	a.propose("haul", PlanResult{Kind: PlanProposed, Proposal: &Proposal{ID: "x"}}, func(ProposalOutcome) { settled = true })
	a.propose("haul", PlanResult{Kind: PlanWaiting}, func(ProposalOutcome) { settled = true })
	if outcomes, failures := a.coordinate(context.Background(), stepBudget{}, proposalScope{}); len(outcomes) != 0 || len(failures) != 0 || settled {
		t.Fatal(outcomes, failures, settled)
	}
	carry := &lateProposals{}
	b := newStepArbiter()
	b.late = carry
	b.close()
	b.propose("haul", PlanResult{Kind: PlanWaiting}, nil)
	b.propose("haul", PlanResult{Kind: PlanProposed, Proposal: &Proposal{ID: "x"}}, nil)
	if got := carry.drain(); len(got) != 1 || got[0].result.Proposal.ID != "x" || got[0].settle != nil {
		t.Fatal(got)
	}
}
