package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func epochStoreFixture(t *testing.T) (*Store, string, *k.Status) {
	t.Helper()
	path := filepath.Join(t.TempDir(), "epoch.db")
	s := open(t, path)
	attempt, _, err := s.PrepareClock(context.Background(), clockIntent("start"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(context.Background(), "start"); err != nil {
		t.Fatal(err)
	}
	reply := clockApplied(attempt)
	if _, err = s.RecordClockReply(context.Background(), "start", reply); err != nil {
		t.Fatal(err)
	}
	return s, path, proto.Clone(reply.GetReceipt().GetApplied().GetStatus()).(*k.Status)
}
func epochStopped(status *k.Status, verified bool) *k.Status {
	v := proto.Clone(status).(*k.Status)
	epoch := v.GetRunning().Epoch
	v.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(verified), PauseVerified: proto.Bool(verified), PauseRequested: proto.Bool(true), StoppedAtUnixMs: proto.Int64(100)}}
	v.ActualPaused = proto.Bool(verified)
	if verified {
		v.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	}
	return v
}
func TestClockEpochAtomicCreationAndReopen(t *testing.T) {
	ctx := context.Background()
	s, path, status := epochStoreFixture(t)
	initial, err := s.LookupClockEpoch(ctx, "start")
	if err != nil || initial.Stage != ClockEpochRequired || initial.Sequence != 0 || !proto.Equal(initial.Epoch, status.GetRunning().Epoch) {
		t.Fatal(initial, err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	reopened, err := s.LookupClockEpoch(ctx, "start")
	if err != nil || reopened.Stage != ClockEpochRequired || !proto.Equal(reopened.Epoch, initial.Epoch) {
		t.Fatal(reopened, err)
	}
	// Returned ownership evidence cannot mutate the durable original epoch.
	reopened.Epoch.Owner.ControllerSessionId = proto.String("foreign")
	again, err := s.LookupClockEpoch(ctx, "start")
	if err != nil || !proto.Equal(again.Epoch, initial.Epoch) {
		t.Fatal(again, err)
	}
	if _, err = s.LoadClockEpochs(ctx, 0); err == nil {
		t.Fatal("zero catalog limit")
	}
	rows, err := s.LoadClockEpochs(ctx, 1)
	if err != nil || len(rows) != 1 {
		t.Fatal(rows, err)
	}
}
func TestClockEpochObligationInsertFailureRollsBackAppliedReceipt(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "epoch.db"))
	attempt, _, err := s.PrepareClock(ctx, clockIntent("start"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, "start"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("CREATE TRIGGER fail_epoch BEFORE INSERT ON clock_epochs BEGIN SELECT RAISE(ABORT,'injected'); END"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, "start", clockApplied(attempt)); err == nil {
		t.Fatal("failed obligation committed receipt")
	}
	stored, err := s.LookupClockAttempt(ctx, "start")
	if err != nil || stored.Phase != ClockDispatched || stored.Reply != nil {
		t.Fatal(stored, err)
	}
	if _, err = s.LookupClockEpoch(ctx, "start"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
}
func TestClockEpochPauseSequenceRequiresFreshObservation(t *testing.T) {
	ctx := context.Background()
	s, _, status := epochStoreFixture(t)
	first, err := s.BeginClockPause(ctx, "start", 0)
	if err != nil || first.Stage != ClockEpochPausing || first.Sequence != 1 {
		t.Fatal(first, err)
	}
	if _, err = s.BeginClockPause(ctx, "start", 1); err == nil {
		t.Fatal("blind second dispatch")
	}
	if _, err = s.MarkClockPauseUncertain(ctx, "start", 0); err == nil {
		t.Fatal("old result accepted")
	}
	if _, err = s.MarkClockPauseUncertain(ctx, "start", 1); err != nil {
		t.Fatal(err)
	}
	if _, err = s.BeginClockPause(ctx, "start", 1); err == nil {
		t.Fatal("uncertain pause retried without observation")
	}
	running := proto.Clone(status).(*k.Status)
	running.GetRunning().Epoch.LastTick = proto.Int64(13)
	running.GetRunning().Epoch.LeaseRemainingMs = proto.Uint32(100)
	running.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
	running.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_FAST.Enum()
	running.Context.Tick = proto.Int64(13)
	observed, err := s.ObserveClockEpoch(ctx, "start", 1, running.Context, running)
	if err != nil || observed.Stage != ClockEpochRequired {
		t.Fatal(observed, err)
	}
	second, err := s.BeginClockPause(ctx, "start", 1)
	if err != nil || second.Sequence != 2 {
		t.Fatal(second, err)
	}
	stopped := epochStopped(running, true)
	if _, err = s.ObserveClockEpoch(ctx, "start", 1, stopped.Context, stopped); err == nil {
		t.Fatal("late sequence completed new pause")
	}
	done, err := s.ObserveClockEpoch(ctx, "start", 2, stopped.Context, stopped)
	if err != nil || done.Stage != ClockEpochPaused {
		t.Fatal(done, err)
	}
	if _, err = s.ObserveClockEpoch(ctx, "start", 2, stopped.Context, stopped); err != nil {
		t.Fatal("exact terminal evidence replay", err)
	}
	if _, err = s.ObserveClockEpoch(ctx, "start", 2, running.Context, running); err == nil {
		t.Fatal("terminal cleanup reactivated")
	}
}
func TestClockEpochImmutableMismatchAndPositiveRetirement(t *testing.T) {
	for _, kind := range []string{"policy", "deadline", "epoch", "world", "inactive"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s, _, status := epochStoreFixture(t)
			want := ClockEpochSuperseded
			switch kind {
			case "policy":
				status.GetRunning().Epoch.Policy.HostileWithin = proto.Float32(21)
			case "deadline":
				status.GetRunning().Epoch.TickDeadline = proto.Int64(113)
			case "epoch":
				status.GetRunning().Epoch.Owner.Epoch = proto.Int64(2)
			case "world":
				status.Context.Identity.LoadToken = proto.String("replacement")
				status.State = &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}}
				status.Context.Tick = proto.Int64(0)
				status.ActualPaused = proto.Bool(true)
				status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
			case "inactive":
				status = epochStopped(status, false)
				want = ClockEpochRetired
			}
			got, err := s.ObserveClockEpoch(ctx, "start", 0, status.Context, status)
			if kind == "policy" || kind == "deadline" {
				if err == nil {
					t.Fatal("immutable epoch changed")
				}
				return
			}
			if err != nil || got.Stage != want {
				t.Fatal(got, err)
			}
		})
	}
}
func TestClockEpochUnknownStatesNeverAcquireOrRetireOwnership(t *testing.T) {
	for _, kind := range []string{"never", "unavailable", "stopping"} {
		t.Run(kind, func(t *testing.T) {
			ctx := context.Background()
			s, _, status := epochStoreFixture(t)
			switch kind {
			case "never":
				status.State = &k.Status_NeverStarted{NeverStarted: &k.NeverStarted{}}
			case "unavailable":
				status.State = &k.Status_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}
			case "stopping":
				status.State = &k.Status_Stopping{Stopping: &k.Stopping{Epoch: status.GetRunning().Epoch, PendingReason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(false)}}
			}
			got, err := s.ObserveClockEpoch(ctx, "start", 0, status.Context, status)
			if err != nil || got.Stage != ClockEpochUncertain {
				t.Fatal(got, err)
			}
		})
	}
}
func TestClockEpochNeverAdoptsUncertainStatusAndMissingAppliedRowIsCorrupt(t *testing.T) {
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "epoch.db"))
	attempt, _, err := s.PrepareClock(ctx, clockIntent("start"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, "start"); err != nil {
		t.Fatal(err)
	}
	applied := clockApplied(attempt)
	uncertain := proto.Clone(applied).(*k.ControlReply)
	uncertain.GetReceipt().Outcome = &k.ControlReceipt_Uncertain{Uncertain: &k.UncertainControl{LastObserved: applied.GetReceipt().GetApplied().GetStatus(), Detail: proto.String("lost outcome")}}
	if _, err = s.RecordClockReply(ctx, "start", uncertain); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LookupClockEpoch(ctx, "start"); !errors.Is(err, ErrNotFound) {
		t.Fatal("uncertain status adopted", err)
	}
	if _, err = s.RecordClockReply(ctx, "start", applied); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("DELETE FROM clock_epochs WHERE start_request_id='start'"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LookupClockEpoch(ctx, "start"); err == nil || errors.Is(err, ErrNotFound) {
		t.Fatal("missing applied obligation hidden", err)
	}
	if _, err = s.RecordClockReply(ctx, "start", applied); err == nil {
		t.Fatal("missing obligation repaired")
	}
}

func TestClockEpochPauseRetainsObservationWatermarkAcrossRestart(t *testing.T) {
	ctx := context.Background()
	s, path, status := epochStoreFixture(t)
	status.Context.Tick = proto.Int64(50)
	status.GetRunning().Epoch.LastTick = proto.Int64(50)
	if _, err := s.ObserveClockEpoch(ctx, "start", 0, status.Context, status); err != nil {
		t.Fatal(err)
	}
	first, err := s.BeginClockPause(ctx, "start", 0)
	if err != nil || first.Sequence != 1 {
		t.Fatal(first, err)
	}
	stale := proto.Clone(status).(*k.Status)
	stale.Context.Tick = proto.Int64(20)
	stale.GetRunning().Epoch.LastTick = proto.Int64(20)
	if _, err = s.ObserveClockEpoch(ctx, "start", 1, stale.Context, stale); err == nil {
		t.Fatal("pause dispatch erased the observation watermark")
	}
	if _, err = s.MarkClockPauseUncertain(ctx, "start", 1); err != nil {
		t.Fatal(err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	if _, err = s.ObserveClockEpoch(ctx, "start", 1, stale.Context, stale); err == nil {
		t.Fatal("reopened uncertain pause accepted stale evidence")
	}
	retained, err := s.LookupClockEpoch(ctx, "start")
	if err != nil || retained.Stage != ClockEpochUncertain || retained.Sequence != 1 || retained.Context.GetTick() != 50 {
		t.Fatal(retained, err)
	}
	if _, err = s.ObserveClockEpoch(ctx, "start", 1, status.Context, status); err != nil {
		t.Fatal(err)
	}
	second, err := s.BeginClockPause(ctx, "start", 1)
	if err != nil || second.Sequence != 2 || second.Stage != ClockEpochPausing {
		t.Fatal(second, err)
	}
}
