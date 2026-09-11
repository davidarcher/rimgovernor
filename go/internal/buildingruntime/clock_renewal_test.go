package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func TestClockRenewalSafetyReviewMustCatchUp(t *testing.T) {
	s, n, w, _ := renewalFixture(t)
	n.status.NewestCursor = proto.Int64(1)
	if _, err := s.RenewEpoch(context.Background()); err == nil || w.renews != 0 || s.session.State().Enabled {
		t.Fatal(err, w.renews)
	}
}

func TestClockRenewalRecoversThenValidatesCurrentRunningEpoch(t *testing.T) {
	s, _, w, start := renewalFixture(t)
	original := start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	id := clockTestNextID(t, s.player.journal)
	intent := store.ClockIntent{RequestID: id, Snapshot: start.Intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: original, LeaseMS: s.config.Start.LeaseMS}}}
	w.lost = true
	if _, err := s.session.CommandClock(context.Background(), intent); err == nil {
		t.Fatal("missing uncertainty")
	}
	w.lost = false
	r, err := s.RenewEpoch(context.Background())
	if err != nil || !r.Reconciled || r.Renewed || w.renews != 1 {
		t.Fatal(r, err)
	}
}

type renewalWriter struct {
	*clockCoreFake
	renews   int
	lost     bool
	original *k.Epoch
}

func (f *renewalWriter) Renew(ctx context.Context, r *k.RenewRequest, original *k.Epoch, owner *a.Owner) (*k.ControlReply, bridge.Result, error) {
	f.renews++
	f.original = proto.Clone(original).(*k.Epoch)
	reply, raw, err := f.clockCoreFake.Renew(ctx, r, original, owner)
	if reply != nil {
		f.receipt = proto.Clone(reply.GetReceipt()).(*k.ControlReceipt)
	}
	if f.lost {
		return nil, raw, errors.New("renew reply lost")
	}
	return reply, raw, err
}
func renewalFixture(t *testing.T) (*ClockScheduler, *schedulerNative, *renewalWriter, store.ClockAttempt) {
	t.Helper()
	s, n := schedulerFixture(t)
	start, err := s.Step(context.Background())
	if err != nil || start.Attempt == nil {
		t.Fatal(start, err)
	}
	w := &renewalWriter{clockCoreFake: n.clockCoreFake}
	s.session.clock.writer = w
	return s, n, w, *start.Attempt
}

func TestClockRenewalOriginalDeadlineAndIndependentPlayerGate(t *testing.T) {
	s, n, w, start := renewalFixture(t)
	original := start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	n.status.Context.Tick = proto.Int64(20)
	n.status.GetRunning().Epoch.LastTick = proto.Int64(20)
	s.player.gate <- struct{}{}
	defer func() { <-s.player.gate }()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	r, err := s.RenewEpoch(ctx)
	if err != nil || !r.Renewed || w.renews != 1 || !proto.Equal(w.original, original) {
		t.Fatal(r, err, w.renews)
	}
	if r.Attempt.Intent.Command.Renew.Original.GetTickDeadline() != original.GetTickDeadline() || n.writes != 2 {
		t.Fatal("renew changed original window")
	}
}

func TestClockRenewalReusesPreparedDespiteFreshTick(t *testing.T) {
	s, n, w, start := renewalFixture(t)
	original := start.Reply.GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	id := clockTestNextID(t, s.player.journal)
	intent := store.ClockIntent{RequestID: id, Snapshot: start.Intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: original, LeaseMS: s.config.Start.LeaseMS}}}
	prepared, _, err := s.player.journal.PrepareClock(context.Background(), intent)
	if err != nil {
		t.Fatal(err)
	}
	n.status.Context.Tick = proto.Int64(20)
	n.status.GetRunning().Epoch.LastTick = proto.Int64(20)
	r, err := s.RenewEpoch(context.Background())
	if err != nil || !r.Renewed || r.Attempt.Intent.RequestID != id || !proto.Equal(r.Attempt.NativeAttempt, prepared.NativeAttempt) || w.renews != 1 {
		t.Fatal(r, err)
	}
}

func TestClockRenewalTerminalUnknownIsIdleWhileDisabled(t *testing.T) {
	s, _, w, _ := renewalFixture(t)
	w.lost = true
	first, err := s.RenewEpoch(context.Background())
	if err == nil || first.Attempt == nil || first.Attempt.Phase != store.ClockUncertain || w.renews != 1 || s.session.State().Enabled {
		t.Fatal(first, err)
	}
	second, err := s.RenewEpoch(context.Background())
	if err != nil || second.Reconciled || second.Attempt != nil || w.renews != 1 {
		t.Fatal(second, err, w.renews)
	}
	old, err := s.player.journal.LookupClockAttempt(context.Background(), first.Attempt.Intent.RequestID)
	if err != nil || old.Phase != store.ClockUncertain {
		t.Fatal(old, err)
	}
	reads, pauses := w.reads, w.pauses
	w.identityError = errors.New("idle must not read native")
	defer func() { w.identityError = nil }()
	if _, err = s.RenewEpoch(context.Background()); err != nil || w.reads != reads || w.pauses != pauses {
		t.Fatal("idle cleanup repeated", err)
	}
}

func TestClockRenewalOldTerminalUncertaintyDoesNotBlockNewEpoch(t *testing.T) {
	s, n, w, start := renewalFixture(t)
	w.lost = true
	old, err := s.RenewEpoch(context.Background())
	if err == nil || old.Attempt == nil || old.Attempt.Phase != store.ClockUncertain {
		t.Fatal(old, err)
	}
	w.lost = false
	if err = s.session.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	current, err := s.session.Acquire(context.Background(), start.Intent.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	n.status.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
	n.status.DurableEvents = proto.Bool(true)
	next, err := s.Step(context.Background())
	if err != nil || next.Attempt == nil || next.Attempt.Phase != store.ClockApplied {
		t.Fatal(next, err)
	}
	renewed, err := s.RenewEpoch(context.Background())
	if err != nil || !renewed.Renewed || renewed.Reconciled || renewed.Attempt.Intent.Snapshot != current || w.renews != 2 {
		t.Fatal(renewed, err, w.renews)
	}
	retained, err := s.player.journal.LookupClockAttempt(context.Background(), old.Attempt.Intent.RequestID)
	if err != nil || retained.Phase != store.ClockUncertain {
		t.Fatal("old uncertainty overwritten", retained, err)
	}
}

func TestClockRenewalDisabledOrReplacementNeverRenews(t *testing.T) {
	for _, reason := range []string{"disabled", "owner", "world", "deadline", "speed"} {
		t.Run(reason, func(t *testing.T) {
			s, n, w, _ := renewalFixture(t)
			original := proto.Clone(n.status.GetRunning().Epoch).(*k.Epoch)
			defer func() {
				if reason == "deadline" || reason == "speed" {
					n.status.State = &k.Status_Running{Running: &k.Running{Epoch: original}}
				}
			}()
			switch reason {
			case "disabled":
				if err := s.session.Disable(); err != nil {
					t.Fatal(err)
				}
			case "owner":
				n.status.GetRunning().Epoch.Owner.Epoch = proto.Int64(9)
			case "world":
				n.status.Context.Identity.LoadToken = proto.String("replacement")
			case "deadline":
				n.status.GetRunning().Epoch.TickDeadline = proto.Int64(200)
			case "speed":
				n.status.GetRunning().Epoch.RequestedSpeed = k.Speed_SPEED_FAST.Enum()
			}
			if _, err := s.RenewEpoch(context.Background()); err == nil || w.renews != 0 || s.session.State().Enabled {
				t.Fatal(reason, err, w.renews)
			}
		})
	}
}

func TestClockRenewalConcurrentCallsHaveDistinctDurableIdentities(t *testing.T) {
	s, _, w, _ := renewalFixture(t)
	results := make(chan ClockRenewResult, 2)
	errs := make(chan error, 2)
	var group sync.WaitGroup
	for range 2 {
		group.Add(1)
		go func() { defer group.Done(); r, e := s.RenewEpoch(context.Background()); results <- r; errs <- e }()
	}
	group.Wait()
	close(results)
	close(errs)
	for err := range errs {
		if err != nil {
			t.Fatal(err)
		}
	}
	ids := map[string]bool{}
	for r := range results {
		if !r.Renewed || r.Attempt == nil || ids[r.Attempt.Intent.RequestID] {
			t.Fatal(r)
		}
		ids[r.Attempt.Intent.RequestID] = true
	}
	if w.renews != 2 || len(ids) != 2 {
		t.Fatal(w.renews, ids)
	}
}
