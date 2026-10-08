package buildingruntime

import (
	"context"
	"reflect"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"google.golang.org/protobuf/proto"
)

// TestClockSchedulerSectionWakeRunsDeclaringPlanners: a fact change in one
// section (buildings, published under the colony family) wakes only the
// planners declaring it (#625), and the step reads the colony anew. A
// pawns wake selects the pawn readers and no building planner.
func TestClockSchedulerSectionWakeRunsDeclaringPlanners(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerSleeping(t, s, f)
	ctx := context.Background()
	first, err := s.Step(ctx)
	if err != nil || first.Rounds == nil || first.Sleeping == nil {
		t.Fatal(first, err)
	}
	reads := n.reads
	s.lastFull = s.clock.Now()
	tick := f.status.Context.GetTick() + 60000
	f.status.Context.Tick = proto.Int64(tick)
	n.reply.GetObserved().Context.Tick = proto.Int64(tick)
	n.cells.Context.Tick = proto.Int64(tick)
	s.lastTick, s.lastTickKnown = tick, true
	// The idle-draft restorer would be due on its own cadence by then;
	// hold it back so the wake alone decides the wave.
	s.queue.due["undraft"] = tick + 1
	reason := StepReason{Cause: StepWake, Families: []bridge.FactFamily{bridge.FactColony}, Sections: []facts.Section{facts.Buildings}}
	step, err := s.StepWithReason(ctx, reason)
	if err != nil || step.Reason.Cause != StepLive || !reflect.DeepEqual(step.Planners, []string{"sleeping"}) {
		t.Fatal(step, err)
	}
	if n.reads <= reads {
		t.Fatal(n.reads - reads)
	}
	// A pawns wake selects the pawn readers (the idle-draft restorer is the
	// configured one) and no building planner.
	pawns, err := s.StepWithReason(ctx, StepReason{Cause: StepWake, Families: []bridge.FactFamily{bridge.FactPawns}})
	if err != nil || !reflect.DeepEqual(pawns.Planners, []string{"undraft"}) {
		t.Fatal(pawns, err)
	}
}

// TestClockSchedulerWaitingPlannerSkipsUntilOutcomeOrDeadline: a planner
// that found the work of its kinds still open (the sleeping planner over
// its own pending blueprint, existing_work) is not evaluated again until
// that attempt's outcome row or its deadline (#625); a step that skips it
// reports it as waiting, and a full step clears every wait.
func TestClockSchedulerWaitingPlannerSkipsUntilOutcomeOrDeadline(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerSleeping(t, s, f)
	ctx := context.Background()
	first, err := s.Step(ctx)
	if err != nil || first.Sleeping == nil || first.Sleeping.Verdict != BuildingReasonAdmitted {
		t.Fatal(first, err)
	}
	s.lastFull = s.clock.Now()
	tick := f.status.Context.GetTick()
	buildingWake := StepReason{Cause: StepWake, Families: []bridge.FactFamily{bridge.FactColony}, Sections: []facts.Section{facts.Buildings}}
	// The planner runs once more, finds its blueprint open and waits on it.
	step, err := s.StepWithReason(ctx, buildingWake)
	if err != nil || !reflect.DeepEqual(step.Planners, []string{"sleeping"}) || step.Sleeping == nil || step.Sleeping.Verdict != BuildingReasonExistingWork || step.Waiting != nil {
		t.Fatal(step, err)
	}
	wait, ok := s.queue.waitingOn("sleeping")
	if !ok || len(wait.On) == 0 || wait.Deadline != tick+int64(reviewEveryUrgent) {
		t.Fatal(wait, ok)
	}
	plans, err := s.player.journal.LoadPlans(ctx)
	if err != nil || !reflect.DeepEqual(openWorkOfKinds(plans, []domain.ActionKind{domain.BuildingAction}), wait.On) {
		t.Fatal(plans, err, wait.On)
	}
	reads := n.reads
	step, err = s.StepWithReason(ctx, buildingWake)
	if err != nil || len(step.Planners) != 0 || step.Sleeping != nil || !reflect.DeepEqual(step.Waiting, []string{"sleeping"}) || n.reads != reads {
		t.Fatal(step, err, n.reads-reads)
	}
	ran := func(step ClockSchedulerResult, name string) bool {
		for _, planner := range step.Planners {
			if planner == name {
				return true
			}
		}
		return false
	}
	// Its outcome row releases the wait: the wake runs it (and, the
	// attempt's kind never having been armed, everything else).
	release := StepReason{Cause: StepWake, Events: []WakeOutcome{{Action: wait.On[0], Attempt: 1, Terminal: true}}}
	step, err = s.StepWithReason(ctx, release)
	if err != nil || step.Waiting != nil || !ran(step, "sleeping") {
		t.Fatal(step, err)
	}
	// Past its deadline the wait lapses even without an outcome.
	if _, ok = s.queue.waitingOn("sleeping"); !ok {
		t.Fatal("re-run over open work did not wait again")
	}
	step, err = s.StepWithReason(ctx, buildingWake)
	if err != nil || !reflect.DeepEqual(step.Waiting, []string{"sleeping"}) {
		t.Fatal(step, err)
	}
	f.status.Context.Tick = proto.Int64(tick + int64(reviewEveryUrgent))
	step, err = s.StepWithReason(ctx, buildingWake)
	if err != nil || step.Waiting != nil || !ran(step, "sleeping") {
		t.Fatal(step, err)
	}
	// A full step runs everything and clears the waits it had.
	if _, ok = s.queue.waitingOn("sleeping"); !ok {
		t.Fatal("re-run over open work did not wait again")
	}
	s.lastFull = time.Time{}
	step, err = s.StepWithReason(ctx, StepReason{Cause: StepTimer})
	if err != nil || step.Reason.Cause != StepLive || step.Waiting != nil || step.Sleeping == nil {
		t.Fatal(step, err)
	}
}

// TestPlannerQueueRanRecordsCadenceAndWaits: a wave records each planner's
// next review tick; one reporting existing work of its kinds waits on that
// work until its outcome; a full wave clears the waits.
func TestPlannerQueueRanRecordsCadenceAndWaits(t *testing.T) {
	t.Parallel()
	q := newPlannerQueue()
	reasons := map[string]Verdict{"repair": BuildingReasonExistingWork, "tend": BuildingReasonAdmitted}
	reasonOf := func(name string) (Verdict, bool) { reason, ok := reasons[name]; return reason, ok }
	sel := plannerSelectionResult{planners: true, pick: func(plannerEntry) bool { return true }}
	q.ran(sel, []string{"repair", "tend"}, reasonOf, 1000, func(kinds []domain.ActionKind) []domain.ActionID {
		if !reflect.DeepEqual(kinds, []domain.ActionKind{domain.RepairAction}) {
			t.Fatal(kinds)
		}
		return []domain.ActionID{"repair-1"}
	})
	if q.due["repair"] != 1000+int64(reviewEveryRounds) || q.due["tend"] != 1000+int64(reviewEveryUrgent) {
		t.Fatal(q.due)
	}
	if wait, ok := q.waitingOn("repair"); !ok || !reflect.DeepEqual(wait.On, []domain.ActionID{"repair-1"}) || wait.Deadline != q.due["repair"] {
		t.Fatal(wait, ok)
	}
	if _, ok := q.waitingOn("tend"); ok {
		t.Fatal("admitted planner waits")
	}
	// Dirty and waiting: skipped and reported.
	q.mark("repair")
	got := q.selection(0, false, false, nil)
	if got.planners || !reflect.DeepEqual(got.waiting, []string{"repair"}) {
		t.Fatal(got)
	}
	// The outcome row releases it.
	q.wake(StepReason{Cause: StepWake, Events: []WakeOutcome{{Action: "repair-1", Attempt: 1, Terminal: true}}}, func(domain.ActionID) (domain.ActionKind, bool) { return domain.RepairAction, true })
	got = q.selection(0, false, false, nil)
	if !got.planners || got.waiting != nil || !got.pick(plannerEntry{name: "repair"}) || got.pick(plannerEntry{name: "tend"}) {
		t.Fatal(got)
	}
	// An attempt no longer open in the journal releases the wait too.
	q.waits["repair"] = plannerWait{On: []domain.ActionID{"repair-2"}, Deadline: 1 << 40}
	if got = q.selection(0, false, false, func(domain.ActionID) bool { return false }); got.waiting != nil {
		t.Fatal(got)
	}
	q.waits["repair"] = plannerWait{On: []domain.ActionID{"repair-2"}, Deadline: 1 << 40}
	q.ran(plannerSelectionResult{planners: true}, nil, reasonOf, 0, nil)
	if len(q.waits) != 0 {
		t.Fatal(q.waits)
	}
}

// A wait's deadline is a tick, so on a stopped clock it never passes: a
// step whose tick did not advance drops the waits and a dirty planner
// re-examines its open work, and a planner refused admission is marked
// again, its cadence tick being unreachable too (#692).
func TestSelectPlannersDropsWaitsOnStoppedClock(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	// The verdict may carry the admission decision's real reason (#1880):
	// recovery keys on the kind, not the whole verdict.
	for _, refused := range []Verdict{refuse(RefusalSharedAdmission, "candidates_left_unadmitted", ""), refuse(RefusalSharedAdmission, "already_reserved", "wood")} {
		t.Run(refused.String(), func(t *testing.T) { stoppedClockRecovers(t, refused) })
	}
}

func stoppedClockRecovers(t *testing.T, refused Verdict) {
	t.Helper()
	s, _ := schedulerFixture(t)
	s.queue.catalog = func() []plannerEntry {
		return []plannerEntry{quickPlanner("defense", classCritical), quickPlanner("defenseLayout", classOptional)}
	}
	s.queue.configured = nil
	ctx := context.Background()
	// defenseLayout was refused admission and is due only at a later tick.
	reasons := map[string]Verdict{"defenseLayout": refused}
	s.queue.ran(plannerSelectionResult{planners: true, pick: func(plannerEntry) bool { return true }}, []string{"defenseLayout"}, func(name string) (Verdict, bool) {
		reason, ok := reasons[name]
		return reason, ok
	}, 100, nil)
	s.queue.mark("defense")
	s.queue.waits["defense"] = plannerWait{On: []domain.ActionID{"attack-1"}, Deadline: 1 << 40}
	s.noWork = true
	got, err := s.selectPlanners(ctx, StepReason{Cause: StepTimer}, 100)
	if err != nil || !got.planners || got.waiting != nil || !got.pick(plannerEntry{name: "defense"}) || !got.pick(plannerEntry{name: "defenseLayout"}) {
		t.Fatal(got, err)
	}
	// While a window runs the refused planner waits for its cadence.
	s.noWork = false
	s.queue.dirty = map[string]uint64{}
	if got, err = s.selectPlanners(ctx, StepReason{Cause: StepTimer}, 100); err != nil || got.planners {
		t.Fatal(got, err)
	}
}

// TestInvalidationSections maps a wire invalidation to the store sections
// it dirties: whole families to every section, ids to the incremental
// sections, a rect to the cell section.
func TestInvalidationSections(t *testing.T) {
	t.Parallel()
	cases := []struct {
		inv  facts.Invalidation
		want []facts.Section
	}{
		{facts.Invalidation{Families: []bridge.FactFamily{bridge.FactResearch}}, []facts.Section{facts.Research}},
		{facts.Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, IDs: []string{"zone-1"}}, []facts.Section{facts.Zones, facts.Buildings, facts.Bills}},
		{facts.Invalidation{Families: []bridge.FactFamily{bridge.FactColony}, Rect: &facts.Rect{MinX: 1, MinZ: 1, MaxX: 2, MaxZ: 2}}, []facts.Section{facts.PlanningCells}},
	}
	for _, c := range cases {
		if got := c.inv.Sections(); !reflect.DeepEqual(got, c.want) {
			t.Errorf("%+v: %v, want %v", c.inv, got, c.want)
		}
	}
}
