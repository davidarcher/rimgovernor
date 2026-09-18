package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// A player Resume that lands while a window is running (a harness
// keepalive, #188) revokes and re-grants authority: native stops the epoch
// with an external pause under the new generation. The next step settles
// that epoch and reviews under the re-granted snapshot instead of failing
// the same way every backoff.
func TestClockSchedulerReviewsAfterAMidWindowRegrant(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	ctx := context.Background()
	got, err := s.Step(ctx)
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || !s.WindowRunning() {
		t.Fatal(got, err)
	}
	authority := s.session.control.native.(*controlNative)
	// The revoke reaches the native watcher before the cleanup reads the
	// status: the epoch is stopped under the revoked generation.
	authority.onRevoke = func() {
		epoch := f.status.GetRunning().GetEpoch()
		f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_EXTERNAL_PAUSE.Enum(), Detail: proto.String("Authorizing native authority stopped: Manual; StaleGeneration"), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(false), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(1)}}
		f.status.ActualPaused = proto.Bool(true)
		f.status.Context.NativeGeneration = proto.Uint64(authority.generation + 1)
	}
	authority.onGrant = func(_ context.Context, reply *a.ControlReply) error {
		f.status.Context.NativeGeneration = proto.Uint64(reply.GetGranted().Context.GetNativeGeneration())
		return nil
	}
	world := playerWorld(s.session.State().Snapshot)
	record, err := s.player.Resume(ctx, store.ControlRequest{RequestID: "keepalive-resume", Kind: store.ResumeControl, World: world})
	if err != nil || record.Phase != store.RunningControl {
		t.Fatal(record, err)
	}
	state := s.session.State()
	if !state.Enabled || !state.ObservationKnown || uint64(state.Snapshot.Native) != f.status.Context.GetNativeGeneration() {
		t.Fatal(state, f.status.Context.GetNativeGeneration())
	}
	for i := 0; i < 3; i++ {
		got, err = s.Step(ctx)
		t.Logf("step %d: %+v err=%v", i, got, err)
		if err != nil && got.Reason.Cause == "" {
			t.Fatalf("step %d never reached the review: %v", i, err)
		}
	}
	epochs, err := s.player.journal.LoadClockEpochs(ctx, 16)
	if err != nil {
		t.Fatal(err)
	}
	for _, owned := range epochs {
		if !clockCoordinatorTerminal(owned.Stage) {
			t.Fatal("epoch still owed", owned)
		}
	}
	if state = s.session.State(); !state.Enabled {
		t.Fatal("authority lost", state)
	}
}
