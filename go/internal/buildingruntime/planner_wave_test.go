package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"reflect"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// Every catalog entry carries a class, and the class follows the rule the
// admission cycle relies on: the preempt and critical priority
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
	want := []string{"undraft", "supplies", "hospital", "sleepingUpkeep", "defense", "medical", "surgery", "tend", "rescue", "fireSafety", "recovery", "populationCustody", "dialog"}
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
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			<-ctx.Done()
			released <- ctx.Err()
			out.Lighting = &RoundsBuildingResult{Verdict: refuse(RefusalSharedAdmission, "already_reserved", "")}
			return Verdict{}, ctx.Err()
		}}
}

// quickPlanner is a catalog entry that returns at once with a result.
func quickPlanner(name string, class plannerClass) plannerEntry {
	return plannerEntry{name: name, class: class, priority: plannerCritical,
		configured: func(*ClockSchedulerConfig) bool { return true },
		run: func(s *ClockScheduler, ctx, epoch context.Context, out *ClockSchedulerResult, arbiter *stepArbiter) (Verdict, error) {
			out.Sleeping = &RoundsBuildingResult{Verdict: BuildingReasonNoDeficit}
			return BuildingReasonNoDeficit, nil
		}}
}

// An optional planner blocked on a read that is never answered does not
// hold admission: the step admits its window once the critical wave
// has returned and the grace has passed, lists the planner under
// missed_cutoff, discards its result and cancels it, and does not report
// the cancellation as a failure. It runs again next step.
func TestClockSchedulerAdmitsPastBlockedOptionalPlanner(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	if got.Sleeping == nil || got.Sleeping.Verdict != BuildingReasonNoDeficit || got.Lighting != nil {
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

// An optional planner slower than the grace is not starved: after it
// missed one step's cutoff the next step joins it for the wall budget, so
// its result is merged instead of being cancelled every step while the
// clock is refused no_work on the work only it would propose.
func TestClockSchedulerJoinsStarvedOptionalPlanner(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, _ := schedulerFixture(t)
	s.config.Budget.OptionalGrace = 20 * time.Millisecond
	s.config.Budget.Wall = 5 * time.Second
	if got := s.optionalWaveGrace(5*time.Millisecond, []string{"defenseLayout"}); got != 20*time.Millisecond {
		t.Fatalf("fresh planner grace %v, want the floor", got)
	}
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical), blockedPlanner("defenseLayout", classOptional, make(chan error, 1))}
	got, err := s.Step(context.Background())
	if err != nil || !reflect.DeepEqual(got.MissedCutoff, []string{"defenseLayout"}) {
		t.Fatal(got.MissedCutoff, err)
	}
	if grace := s.optionalWaveGrace(5*time.Millisecond, []string{"defenseLayout"}); grace != 5*time.Second {
		t.Fatalf("starved planner grace %v, want the wall budget", grace)
	}
	if grace := s.optionalWaveGrace(5*time.Millisecond, []string{"lighting"}); grace != 20*time.Millisecond {
		t.Fatalf("other planner grace %v, want the floor", grace)
	}
	// A wave that does not run it keeps the mark; its own return clears it.
	s.markStarved([]string{"rooms"}, nil)
	if grace := s.optionalWaveGrace(5*time.Millisecond, []string{"defenseLayout"}); grace != 5*time.Second {
		t.Fatalf("starved planner grace %v after a wave without it, want the wall budget", grace)
	}
	s.markStarved([]string{"defenseLayout"}, nil)
	if grace := s.optionalWaveGrace(5*time.Millisecond, []string{"defenseLayout"}); grace != 20*time.Millisecond {
		t.Fatalf("returned planner grace %v, want the floor", grace)
	}
}

// A critical planner blocked the same way holds admission: past the
// wall budget the step admits no window, reports the planner under
// held_by, and the next step evaluates again.
func TestClockSchedulerHoldsOnBlockedCriticalPlanner(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
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
	// The 50 ms wall only has to expire on the blocked step; under a loaded
	// suite the quick planner can miss it too and hold again.
	s.config.Budget.Wall = time.Minute
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical)}
	again, err := s.Step(context.Background())
	if err != nil || again.Attempt == nil || again.Attempt.Phase != store.ClockApplied || f.writes != 1 || len(again.HeldBy) != 0 {
		t.Fatal(again, err, f.writes)
	}
}

// A proposal that reached its step's arbiter after the cutoff is carried to
// the next step's coordinator and revalidated there: against a
// newer snapshot it is refused with the stale dependency named and its
// commit never runs; against the same one it commits.
func TestClockSchedulerRefusesLateProposalAgainstNewerSnapshot(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, f := schedulerFixture(t)
	s.catalog = nil
	current := s.session.State().Snapshot
	committed := 0
	late := func(id string, snapshot domain.GenerationSnapshot, tick domain.Tick) *Proposal {
		p := &Proposal{ID: id, Planner: "lighting", Concern: domain.ConcernID(id), Priority: plannerMaintenance, Snapshot: snapshot, ValidTick: tick}
		p.commit = func(context.Context) (domain.PlanID, Verdict, error) {
			committed++
			return domain.PlanID("plan-" + id), BuildingReasonAdmitted, nil
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
	arbiter.propose("lighting", PlanResult{Kind: PlanProposed, Proposal: late("valid", current, tick)}, nil)
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied {
		t.Fatal(got, err)
	}
	if len(got.Proposals) != 2 || committed != 1 {
		t.Fatalf("proposals %+v committed %d", got.Proposals, committed)
	}
	byID := map[string]ProposalOutcome{}
	for _, outcome := range got.Proposals {
		byID[outcome.Proposal] = outcome
	}
	if o := byID["generation"]; o.Admitted || o.Verdict != BuildingReasonExpired || o.Stale != fmt.Sprintf("native generation %d, step is %d", stale.Native, current.Native) {
		t.Fatalf("stale generation: %+v", o)
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

// The shelter's planner is the one startup planner: optional by
// class, so a healthy colony's shell review never holds admission, and
// promoted into the critical cycle while the stage waits for the shelter.
func TestPlannerCatalogStartupPlanners(t *testing.T) {
	t.Parallel()
	var startup []string
	for _, entry := range plannerCatalog {
		if !entry.startup {
			continue
		}
		if entry.class != classOptional {
			t.Fatalf("%s is %q: a startup planner is optional until the hold promotes it", entry.name, entry.class)
		}
		startup = append(startup, entry.name)
	}
	if want := []string{"sleeping"}; !reflect.DeepEqual(startup, want) {
		t.Fatalf("startup planners %v, want %v", startup, want)
	}
}

// While the colony stage holds development because the shelter is unmet,
// the shelter's own planner runs in the critical cycle: siting a
// starter shell walks the bunk rungs and previews a ring, which outlasts
// the optional grace, and a step that drops that work admits nothing for
// the goal at all. A step whose shelter planner has not returned holds
// admission and names it, instead of recording it as having missed the
// cutoff and discarding its result.
func TestClockSchedulerPromotesTheShelterPlannerUnderTheFootholdHold(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, f := schedulerFixture(t)
	schedulerRounds(t, s, f)
	// A hang guard: the quick critical planner must return within it under
	// package load; the blocked shelter planner runs to it every time.
	s.config.Budget.Wall = 2 * time.Second
	s.config.Budget.OptionalGrace = 20 * time.Millisecond
	released := make(chan error, 1)
	shelter := blockedPlanner("sleeping", classOptional, released)
	shelter.startup = true
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical), shelter}
	got, err := s.Step(context.Background())
	if !errors.Is(err, executor.ErrHeld) || got.Attempt != nil || f.writes != 0 {
		t.Fatal(got, err, f.writes)
	}
	if got.Rounds == nil || got.Rounds.Review.Stage == nil || !got.Rounds.Review.Stage.NeedsShelter() {
		t.Fatalf("the fixture must review under the Foothold hold: %+v", got.Rounds)
	}
	if !reflect.DeepEqual(got.HeldBy, []string{"sleeping"}) || len(got.MissedCutoff) != 0 {
		t.Fatalf("held %v missed %v", got.HeldBy, got.MissedCutoff)
	}
	select {
	case cause := <-released:
		if !errors.Is(cause, context.Canceled) {
			t.Fatal(cause)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the promoted planner was never released")
	}
}

// Without the hold the same planner is optional: the shell review of a
// colony that already has its shelter is cut off at the grace and the step
// admits its window.
func TestClockSchedulerCutsOffTheShelterPlannerWithoutTheHold(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	s.config.Budget.OptionalGrace = 20 * time.Millisecond
	released := make(chan error, 1)
	shelter := blockedPlanner("sleeping", classOptional, released)
	shelter.startup = true
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical), shelter}
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if !reflect.DeepEqual(got.MissedCutoff, []string{"sleeping"}) || len(got.HeldBy) != 0 {
		t.Fatalf("missed %v held %v", got.MissedCutoff, got.HeldBy)
	}
	select {
	case cause := <-released:
		if !errors.Is(cause, context.Canceled) {
			t.Fatal(cause)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the optional planner was never released")
	}
}

// The wall budget bounds the planners, not the rounds that precedes
// them: a review slower than the whole budget (a cold review and layout on a
// slow runner) leaves the critical planner its full wall instead of holding
// the step with nothing evaluated.
func TestClockSchedulerWallExcludesTheRounds(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerRounds(t, s, f)
	s.config.Budget.Wall = 400 * time.Millisecond
	slow := &slowWindow{windowedScheduler: windowedScheduler{f, n}, delay: 600 * time.Millisecond}
	replacement, err := NewClockScheduler(s.player, s.session, slow, s.config, s.clock)
	if err != nil {
		t.Fatal(err)
	}
	*s = *replacement
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical)}
	got, err := s.Step(context.Background())
	if len(got.HeldBy) != 0 || errors.Is(err, executor.ErrHeld) {
		t.Fatalf("held %v err %v: the review's time counted against the planner wall", got.HeldBy, err)
	}
	if slow.reads.Load() == 0 {
		t.Fatal("the slow review never ran, so the test proved nothing")
	}
}

type slowWindow struct {
	windowedScheduler
	delay time.Duration
	reads atomic.Int32
}

func (w *slowWindow) ReadPlanningWindow(ctx context.Context, id *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	w.reads.Add(1)
	time.Sleep(w.delay)
	return w.windowedScheduler.ReadPlanningWindow(ctx, id, rect)
}

// A planner whose owner moved under it (a sibling on the same standard
// admitted in the wave) waits for the next round; it is not a failure.
func TestClockSchedulerStaleOwnerIsNotAPlannerFailure(t *testing.T) {
	t.Run("revision", func(t *testing.T) { testPlannerAdmissionWait(t, store.ErrStaleOwner) })
	t.Run("open method", func(t *testing.T) { testPlannerAdmissionWait(t, store.ErrOpenMethod) })
}

func testPlannerAdmissionWait(t *testing.T, cause error) {
	t.Helper()
	s, _ := schedulerFixture(t)
	stale := quickPlanner("fields", classOptional)
	stale.run = func(*ClockScheduler, context.Context, context.Context, *ClockSchedulerResult, *stepArbiter) (Verdict, error) {
		return Verdict{}, fmt.Errorf("method admission: %w", cause)
	}
	s.catalog = []plannerEntry{quickPlanner("tend", classCritical), stale}
	got, err := s.Step(context.Background())
	if err != nil || len(got.PlannerFailures) != 0 {
		t.Fatal(got.PlannerFailures, err)
	}
}
