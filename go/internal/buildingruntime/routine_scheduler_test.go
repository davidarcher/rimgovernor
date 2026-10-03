package buildingruntime

import (
	"context"
	"errors"
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	factsstore "github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func schedulerRoutine(t *testing.T, s *ClockScheduler, f *schedulerNative) *routineNative {
	t.Helper()
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	n := &routineNative{reply: &o.ColonyFactsReply{}}
	if err = protojson.Unmarshal(data, n.reply); err != nil {
		t.Fatal(err)
	}
	n.cells = fixtureCells(t)
	n.reply.GetObserved().Context = proto.Clone(f.status.Context).(*c.ObservationContext)
	n.cells.Context = proto.Clone(f.status.Context).(*c.ObservationContext)
	r, err := NewRoutineReviewer(s.player, n, s.clock, policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	config := s.config
	config.Routine = r
	replacement, err := NewClockScheduler(s.player, s.session, windowedScheduler{f, n}, config, s.clock)
	if err != nil {
		t.Fatal(err)
	}
	*s = *replacement
	return n
}

// windowedScheduler is a scheduler fake whose planning window is the
// routine fake's, as one native serves both.
type windowedScheduler struct {
	*schedulerNative
	window *routineNative
}

func (w windowedScheduler) ReadPlanningWindow(ctx context.Context, id *c.Identity, rect policy.Rectangle) (bridge.PlanningWindow, bridge.Result, error) {
	return w.window.ReadPlanningWindow(ctx, id, rect)
}

// TestClockSchedulerReviewsRoutineUnderARunningWindow: the routine review
// is no longer bound to the stop between windows (#243). A timer step under
// the window it started reviews nothing until the full step is due; a full
// step reviews live, reported with the "live" cause, and admits nothing
// (the window is already running: no write, no pause).
func TestClockSchedulerReviewsRoutineUnderARunningWindow(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerRoutine(t, s, f)
	first, err := s.Step(context.Background())
	if err != nil || first.Routine == nil || len(first.Routine.Goals) != 49 || f.writes != 1 || n.reads != 1 {
		t.Fatal(first, err, f.writes, n.reads)
	}
	second, err := s.StepWithReason(context.Background(), StepReason{Cause: StepTimer})
	if err != nil || !second.Running || second.Routine != nil || n.reads != 1 || second.Reason.Cause != StepTimer {
		t.Fatal(second, err)
	}
	live, err := s.Step(context.Background())
	if err != nil || !live.Running || live.Routine == nil || n.reads != 2 || live.Reason.Cause != StepLive || live.Attempt != nil || f.writes != 1 || f.pauses != 0 {
		t.Fatal(live, err, n.reads, f.writes, f.pauses)
	}
	if err = s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	cleanup, err := s.Step(context.Background())
	if err != nil || !cleanup.Cleaned || n.reads != 2 || f.pauses != 1 {
		t.Fatal(cleanup, err)
	}
}

func TestClockSchedulerFailedRoutineReadCannotStartWindow(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerRoutine(t, s, f)
	n.onRead = func(context.Context) { n.reply.GetObserved().ColonistCount = proto.Uint32(0) }
	got, err := s.Step(context.Background())
	if err == nil || got.Routine != nil || f.writes != 0 {
		t.Fatal(got, err, f.writes)
	}
	review, err := s.player.journal.LoadRoutineReview(context.Background())
	if err != nil || review.Revision != 0 {
		t.Fatal(review, err)
	}
}

func TestClockSchedulerRejectsDifferentRoutineOwner(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	r, _, _, _, _ := routineFixture(t)
	config := s.config
	config.Routine = r
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("different player accepted")
	}
}

// TestClockSchedulerFilesReviewSectionsInTheStore: a reviewing step files
// the census it decoded in the state store (#354), every section stamped
// with the bundle's tick, and the review row records the same as-of map
// with a zero spread: nothing drifts while everything comes from one
// bundle. The admission's emergency census is filed from the bundle read
// itself.
func TestClockSchedulerFilesReviewSectionsInTheStore(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	schedulerRoutine(t, s, f)
	if s.facts.store.Len() != 0 {
		t.Fatal("store filled before any step")
	}
	first, err := s.Step(context.Background())
	if err != nil || first.Routine == nil {
		t.Fatal(first, err)
	}
	tick := int64(first.Routine.Review.Tick)
	status := s.facts.store.Status()
	held := map[factsstore.Section]factsstore.Status{}
	for _, row := range status {
		held[row.Section] = row
	}
	for _, section := range []factsstore.Section{factsstore.Colony, factsstore.PlanningCells, factsstore.Population, factsstore.Emergency} {
		row, ok := held[section]
		if !ok || row.AsOf != tick || row.Family != section.Family() {
			t.Fatalf("%s = %+v ok=%v (tick %d)", section, row, ok, tick)
		}
	}
	if held[factsstore.Emergency].Source != "rimgovernor/observations_read_status" {
		t.Fatalf("emergency filed from %q, not the admission bundle", held[factsstore.Emergency].Source)
	}
	if _, ok := held[factsstore.Pawns]; ok {
		t.Fatal("pawns filed under an unknown colonist census")
	}
	if scope := s.facts.store.Scope(); scope.Load != f.status.Context.GetIdentity().GetLoadToken() || scope.Generation != f.status.Context.GetNativeGeneration() {
		t.Fatalf("scope = %+v", scope)
	}
}

// TestClockSchedulerDisabledReviewFailsTheStep: authority that lapses
// between the step's state read and the routine review (a poll hold, a
// resume in flight) leaves a disabled review that ranks nothing. The step
// must not evaluate a window on it -- that refuses no_work at every step
// until something else re-reviews (#331) -- and the first step with
// authority back reviews again.
func TestClockSchedulerDisabledReviewFailsTheStep(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	n := schedulerRoutine(t, s, f)
	ctx := context.Background()
	first, err := s.Step(ctx)
	if err != nil || first.Routine == nil || !first.Routine.Review.Enabled || first.Attempt == nil {
		t.Fatal(first, err)
	}
	writes := f.writes
	// Authority lapses after the scheduler's own state check: the reviewer
	// records the disabled review and the planner wave must not go on.
	if err = s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	call, epoch, done, err := s.player.enter(ctx, "test", false)
	if err != nil {
		t.Fatal(err)
	}
	var out ClockSchedulerResult
	planners, err := s.runPlanners(call, epoch, &out, plannerSelectionResult{planners: true}, f.status)
	done()
	if !errors.Is(err, executor.ErrAuthority) || planners != nil || out.Routine != nil || f.writes != writes {
		t.Fatal(planners, err, f.writes, writes)
	}
	review, err := s.player.journal.LoadRoutineReview(ctx)
	if err != nil || review.Enabled || review.Revision != first.Routine.Review.Revision+1 {
		t.Fatal(review, err)
	}
	if cleanup, err := s.Step(ctx); err != nil || !cleanup.Cleaned {
		t.Fatal(cleanup, err)
	}
	if err = s.session.Manual(ctx); err != nil {
		t.Fatal(err)
	}
	next := s.session.State().Snapshot
	next.Native++
	granted, err := s.session.Acquire(ctx, next)
	if err != nil {
		t.Fatal(err)
	}
	f.status.Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
	n.reply.GetObserved().Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
	n.cells.Context.NativeGeneration = proto.Uint64(uint64(granted.Native))
	again, err := s.StepWithReason(ctx, StepReason{Cause: StepFull})
	if err != nil && !errors.Is(err, executor.ErrHeld) || again.Routine == nil || !again.Routine.Review.Enabled || again.Routine.Review.Revision != review.Revision+1 {
		t.Fatal(again, err)
	}
}
