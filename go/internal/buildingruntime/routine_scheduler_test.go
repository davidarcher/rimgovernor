package buildingruntime

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
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
	n.reply.GetObserved().Context = proto.Clone(f.status.Context).(*c.ObservationContext)
	n.reply.GetObserved().Planning.GetObserved().Cells.Context = proto.Clone(f.status.Context).(*c.ObservationContext)
	r, err := NewRoutineReviewer(s.player, n, s.clock, policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	config := s.config
	config.Routine = r
	replacement, err := NewClockScheduler(s.player, s.session, f, config, s.clock)
	if err != nil {
		t.Fatal(err)
	}
	*s = *replacement
	return n
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
	if err != nil || first.Routine == nil || len(first.Routine.Goals) != 45 || f.writes != 1 || n.reads != 1 {
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

// TestClockSchedulerBundleRequestsFamiliesForAReview: the step's first
// bundle carries the census families exactly when the step is expected to
// review (issue #180): never without a reviewer, on a timer only when the
// full step is due, on evidence when the selection runs the reviewer (a
// subset selection included), under a running window as under a stopped
// one (#243).
func TestClockSchedulerBundleRequestsFamiliesForAReview(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	families := func(r *o.BundleRequest) bool {
		return r.GetColonyFacts() && r.GetPopulation() && r.GetResearch() && r.GetColonistPawns()
	}
	bare := func(r *o.BundleRequest) bool {
		return r.GetClockStatus() && r.GetEmergency() && r.ColonyFacts == nil && r.Population == nil && r.Research == nil && r.ColonistPawns == nil
	}
	if r := s.bundleRequest(StepReason{Cause: StepFull}); !bare(r) {
		t.Fatal("families without a reviewer", r)
	}
	schedulerRoutine(t, s, f)
	if r := s.bundleRequest(StepReason{Cause: StepFull}); !families(r) || !r.GetClockStatus() || !r.GetEmergency() {
		t.Fatal(r)
	}
	if r := s.bundleRequest(StepReason{Cause: StepTimer}); !families(r) {
		t.Fatal("timer with the full step due", r)
	}
	s.lastFull = s.clock.Now()
	if r := s.bundleRequest(StepReason{Cause: StepTimer}); !bare(r) {
		t.Fatal("timer between full steps", r)
	}
	if r := s.bundleRequest(StepReason{Cause: StepWake, Authority: true}); !families(r) {
		t.Fatal("authority wake", r)
	}
	if r := s.bundleRequest(StepReason{Cause: StepWake, Families: []bridge.FactFamily{bridge.FactPawns}}); !families(r) {
		t.Fatal("wake selecting a planner subset still reviews", r)
	}
	s.running.Store(true)
	if r := s.bundleRequest(StepReason{Cause: StepFull}); !families(r) {
		t.Fatal("full step under a running window", r)
	}
	if r := s.bundleRequest(StepReason{Cause: StepWake, Stopped: true}); !families(r) {
		t.Fatal("wake carrying the window's stop", r)
	}
	s.lastFull = s.clock.Now()
	if r := s.bundleRequest(StepReason{Cause: StepTimer}); !bare(r) {
		t.Fatal("timer under a running window between full steps", r)
	}
	s.lastFull = time.Time{}
	if r := s.bundleRequest(StepReason{Cause: StepTimer}); !families(r) {
		t.Fatal("timer under a running window with the full step due", r)
	}
}
