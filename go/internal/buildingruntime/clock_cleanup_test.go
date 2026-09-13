package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func (f *clockCoreFake) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	if f.identityError != nil {
		return nil, bridge.Result{}, f.identityError
	}
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: proto.Clone(f.status.Context).(*c.ObservationContext)}}}, bridge.Result{}, ctx.Err()
}
func (f *clockCoreFake) OwnedPause(ctx context.Context, r *k.OwnedRequest) (*k.StatusReply, bridge.Result, error) {
	f.pauses++
	if !proto.Equal(r.Identity, f.status.Context.Identity) || !proto.Equal(r.Owner, clockCoordinatorEpoch(f.status).Owner) {
		return nil, bridge.Result{}, errors.New("wrong pause target")
	}
	if f.pause != nil {
		if err := f.pause(ctx); err != nil {
			return nil, bridge.Result{}, err
		}
	}
	epoch := clockCoordinatorEpoch(f.status)
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(true), StoppedAtUnixMs: proto.Int64(100)}}
	f.status.ActualPaused = proto.Bool(true)
	return &k.StatusReply{Outcome: &k.StatusReply_Status{Status: proto.Clone(f.status).(*k.Status)}}, bridge.Result{}, nil
}
func clockCleanupStart(t *testing.T) (*ClockCoordinator, *store.Store, *clockCoreFake, store.ClockIntent) {
	t.Helper()
	q, db, f, intent := clockCoreFixture(t)
	if err := q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true}); err != nil {
		t.Fatal(err)
	}
	if _, err := q.Command(context.Background(), intent); err != nil {
		t.Fatal(err)
	}
	return q, db, f, intent
}
func TestClockCleanupAfterStopPausesOnce(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCleanupStart(t)
	if err := q.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := q.Cleanup(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := q.Cleanup(context.Background()); err != nil {
		t.Fatal(err)
	}
	v, err := db.LookupClockEpoch(context.Background(), intent.RequestID)
	if err != nil || v.Stage != store.ClockEpochPaused || v.Sequence != 1 || f.pauses != 1 || f.writes != 1 {
		t.Fatal(v, err, f.pauses)
	}
}
func TestClockCleanupLostPauseObservedBeforeRetry(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCleanupStart(t)
	f.pause = func(context.Context) error { return context.DeadlineExceeded }
	if err := q.Cleanup(context.Background()); err == nil {
		t.Fatal("lost pause accepted")
	}
	v, err := db.LookupClockEpoch(context.Background(), intent.RequestID)
	if err != nil || v.Stage != store.ClockEpochUncertain || v.Sequence != 1 {
		t.Fatal(v, err)
	}
	// Stopping is armed: no second pause until a fresh Running observation.
	epoch := clockCoordinatorEpoch(f.status)
	f.status.State = &k.Status_Stopping{Stopping: &k.Stopping{Epoch: epoch, PendingReason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(false)}}
	if err = q.Cleanup(context.Background()); err == nil || f.pauses != 1 {
		t.Fatal(err, f.pauses)
	}
	f.status.State = &k.Status_Running{Running: &k.Running{Epoch: epoch}}
	f.pause = nil
	if err = q.Cleanup(context.Background()); err != nil || f.pauses != 2 {
		t.Fatal(err, f.pauses)
	}
}
func TestClockCleanupWorldReplacementAndUnavailable(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCleanupStart(t)
	f.identityError = bridge.ErrUnavailable
	if err := q.Cleanup(context.Background()); err == nil || f.pauses != 0 {
		t.Fatal(err)
	}
	f.identityError = nil
	f.status.Context.Identity.LoadToken = proto.String("replacement")
	reads := f.reads
	if err := q.Cleanup(context.Background()); err != nil {
		t.Fatal(err)
	}
	v, err := db.LookupClockEpoch(context.Background(), intent.RequestID)
	if err != nil || v.Stage != store.ClockEpochSuperseded || f.pauses != 0 || f.reads != reads {
		t.Fatal(v, err)
	}
}
func TestClockCleanupUnknownStartRecoveryAndRetirement(t *testing.T) {
	t.Parallel()
	for _, replace := range []bool{false, true} {
		t.Run(map[bool]string{false: "recover", true: "replace"}[replace], func(t *testing.T) {
			q, db, f, intent := clockCoreFixture(t)
			_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
			f.lost = true
			if _, err := q.Command(context.Background(), intent); err == nil {
				t.Fatal("expected lost reply")
			}
			_ = q.Stop(context.Background())
			if replace {
				f.status.Context.Identity.LoadToken = proto.String("replacement")
			}
			if err := q.Cleanup(context.Background()); err != nil {
				t.Fatal(err)
			}
			v, err := db.LookupClockAttempt(context.Background(), intent.RequestID)
			if err != nil {
				t.Fatal(err)
			}
			if replace {
				if v.SupersededAt == nil || v.Phase != store.ClockUncertain || f.lookups != 0 || f.pauses != 0 {
					t.Fatal(v, f)
				}
			} else {
				if v.Phase != store.ClockApplied || f.lookups != 1 || f.pauses != 1 {
					t.Fatal(v, f)
				}
			}
		})
	}
}
func TestClockCleanupUnknownNeverAdoptsRunningStatus(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCoreFixture(t)
	_ = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true})
	f.lost = true
	_, _ = q.Command(context.Background(), intent)
	f.receipt = nil
	if err := q.Cleanup(context.Background()); err == nil {
		t.Fatal("unknown resolved")
	}
	epochs, err := db.LoadClockEpochs(context.Background(), 4096)
	if err != nil || len(epochs) != 0 || f.pauses != 0 {
		t.Fatal(epochs, err)
	}
}
func TestClockCleanupCancellationPersistsAndJoins(t *testing.T) {
	t.Parallel()
	q, db, f, intent := clockCleanupStart(t)
	started := make(chan struct{})
	release := make(chan struct{})
	f.pause = func(ctx context.Context) error { close(started); <-release; return ctx.Err() }
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- q.Cleanup(ctx) }()
	<-started
	cancel()
	short, stop := context.WithTimeout(context.Background(), time.Millisecond)
	defer stop()
	if err := q.Stop(short); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatal(err)
	}
	close(release)
	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	v, err := db.LookupClockEpoch(context.Background(), intent.RequestID)
	if err != nil || v.Stage != store.ClockEpochUncertain {
		t.Fatal(v, err)
	}
	if err = q.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
}

// The independent identity read is a generation watermark even at a paused tick.
type clockCleanupFreshIdentity struct {
	*clockCoreFake
	generation uint64
}

func (f clockCleanupFreshIdentity) Identity(ctx context.Context) (*l.IdentityReply, bridge.Result, error) {
	reply, raw, err := f.clockCoreFake.Identity(ctx)
	if err == nil {
		reply.GetLoaded().Context.NativeGeneration = proto.Uint64(f.generation)
	}
	return reply, raw, err
}
func TestClockCleanupRejectsStatusGenerationBehindIdentity(t *testing.T) {
	t.Parallel()
	for _, missing := range []bool{false, true} {
		t.Run(map[bool]string{false: "regressed", true: "unknown"}[missing], func(t *testing.T) {
			q, db, f, intent := clockCleanupStart(t)
			q.native = clockCleanupFreshIdentity{f, 8}
			if missing {
				f.status.Context.NativeGeneration = nil
			}
			if err := q.Cleanup(context.Background()); err == nil {
				t.Fatal("stale status admitted")
			}
			epoch, err := db.LookupClockEpoch(context.Background(), intent.RequestID)
			if err != nil || epoch.Stage != store.ClockEpochRequired || epoch.Sequence != 0 || epoch.Context != nil || f.pauses != 0 {
				t.Fatal(epoch, err, f.pauses)
			}
		})
	}
}
