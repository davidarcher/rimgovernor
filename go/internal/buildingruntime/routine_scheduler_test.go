package buildingruntime

import (
	"context"
	"os"
	"testing"
	"time"

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

func TestClockSchedulerReviewsRoutineOnlyAtPausedBoundary(t *testing.T) {
	s, f := schedulerFixture(t)
	n := schedulerRoutine(t, s, f)
	first, err := s.Step(context.Background())
	if err != nil || first.Routine == nil || len(first.Routine.Goals) != 16 || f.writes != 1 || n.reads != 1 {
		t.Fatal(first, err, f.writes, n.reads)
	}
	second, err := s.Step(context.Background())
	if err != nil || !second.Running || second.Routine != nil || n.reads != 1 {
		t.Fatal(second, err)
	}
	if err = s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	cleanup, err := s.Step(context.Background())
	if err != nil || !cleanup.Cleaned || n.reads != 1 || f.pauses != 1 {
		t.Fatal(cleanup, err)
	}
}

func TestClockSchedulerFailedRoutineReadCannotStartWindow(t *testing.T) {
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
	s, _ := schedulerFixture(t)
	r, _, _, _, _ := routineFixture(t)
	config := s.config
	config.Routine = r
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("different player accepted")
	}
}
