package store

import (
	"context"
	"errors"
	"fmt"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"testing"
)

func retirementPrepare(t *testing.T, s *Store) ClockAttempt {
	t.Helper()
	head, err := s.ReadClockSequence(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	id, err := head.NextRequestID()
	if err != nil {
		t.Fatal(err)
	}
	intent := clockIntent(id)
	intent.Key = id
	v, _, err := s.PrepareClock(context.Background(), intent)
	if err != nil {
		t.Fatal(err)
	}
	return v
}
func retirementHead(t *testing.T, s *Store) ClockSequenceState {
	t.Helper()
	v, err := s.ReadClockSequence(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return v
}
func TestClockRetirementSparseUnknownAndRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "retirement.db")
	s := open(t, path)
	unknown := retirementPrepare(t, s)
	if _, err := s.DispatchClock(ctx, unknown.Intent.RequestID); err != nil {
		t.Fatal(err)
	}
	var last ClockAttempt
	for range 20 {
		last = retirementPrepare(t, s)
	}
	head := retirementHead(t, s)
	got, err := s.RetireClockHistory(ctx, head, 1)
	if err != nil || got.RemovedAttempts != 19 || got.RetainedAttempts != 2 || got.State.RetiredThrough != 21 {
		t.Fatal(got, err)
	}
	if _, err = s.LookupClockAttempt(ctx, unknown.Intent.RequestID); err != nil {
		t.Fatal(err)
	}
	retired, _ := ClockRequestID(head.Namespace, 2)
	if _, err = s.LookupClockAttempt(ctx, retired); !errors.Is(err, ErrRetired) {
		t.Fatal(err)
	}
	old := clockIntent(retired)
	old.Key = retired
	if _, _, err = s.PrepareClock(ctx, old); !errors.Is(err, ErrRetired) {
		t.Fatal("retired request reallocated", err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	if _, err = s.LookupClockAttempt(ctx, last.Intent.RequestID); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RetireClockHistory(ctx, head, 0); !errors.Is(err, ErrConflict) {
		t.Fatal("stale CAS", err)
	}
	// Deleting a pinned row below the watermark remains corruption, not retirement.
	if _, err = s.db.ExecContext(ctx, "DELETE FROM clock_attempts WHERE request_id=?", unknown.Intent.RequestID); err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReadClockSequence(ctx); err == nil {
		t.Fatal("missing pinned row accepted")
	}
}
func TestClockRetirementStartProvenanceAndAtomicRollback(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "epochs.db"))
	start := retirementPrepare(t, s)
	if _, err := s.DispatchClock(ctx, start.Intent.RequestID); err != nil {
		t.Fatal(err)
	}
	reply := clockApplied(start)
	if _, err := s.RecordClockReply(ctx, start.Intent.RequestID, reply); err != nil {
		t.Fatal(err)
	}
	if got, err := s.RetireClockHistory(ctx, retirementHead(t, s), 0); err != nil || got.RemovedAttempts != 0 || got.RetainedAttempts != 1 {
		t.Fatal("live epoch retired", got, err)
	}
	status := reply.GetReceipt().GetApplied().GetStatus()
	stopped := epochStopped(status, true)
	if _, err := s.ObserveClockEpoch(ctx, start.Intent.RequestID, 0, stopped.Context, stopped); err != nil {
		t.Fatal(err)
	}
	h := retirementHead(t, s)
	id, _ := h.NextRequestID()
	renew := ClockIntent{RequestID: id, Key: id, Snapshot: start.Intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: proto.Clone(status.GetRunning().Epoch).(*k.Epoch), LeaseMS: 1000}}}
	if _, _, err := s.PrepareClock(ctx, renew); err != nil {
		t.Fatal(err)
	}
	if _, err := s.DispatchClock(ctx, id); err != nil {
		t.Fatal(err)
	}
	if got, err := s.RetireClockHistory(ctx, retirementHead(t, s), 0); err != nil || got.RemovedAttempts != 0 || got.RetainedAttempts != 2 {
		t.Fatal(got, err)
	}
	// A settled renewal no longer pins provenance when both rows are eligible.
	attempt, err := s.LookupClockAttempt(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, id, clockApplied(attempt)); err != nil {
		t.Fatal(err)
	}
	h = retirementHead(t, s)
	if _, err = s.db.ExecContext(ctx, "CREATE TRIGGER fail_retirement BEFORE DELETE ON clock_attempts BEGIN SELECT RAISE(ABORT,'retain evidence'); END"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RetireClockHistory(ctx, h, 0); err == nil {
		t.Fatal("injected deletion succeeded")
	}
	if _, err = s.LookupClockEpoch(ctx, start.Intent.RequestID); err != nil {
		t.Fatal("epoch deletion escaped rollback", err)
	}
	if retirementHead(t, s) != h {
		t.Fatal("watermark escaped rollback")
	}
	if _, err = s.db.ExecContext(ctx, "DROP TRIGGER fail_retirement"); err != nil {
		t.Fatal(err)
	}
	if got, err := s.RetireClockHistory(ctx, h, 0); err != nil || got.RemovedAttempts != 2 || got.RemovedEpochs != 1 {
		t.Fatal(got, err)
	}
}

func TestClockRetirementKeepsLatestWindowAnchor(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, _, intent := windowStoreFixture(t)
	var latest string
	for range 3 {
		head := retirementHead(t, s)
		id, err := head.NextRequestID()
		if err != nil {
			t.Fatal(err)
		}
		intent.RequestID = id
		intent.Key = id
		if _, _, err = s.PrepareClock(ctx, intent); err != nil {
			t.Fatal(err)
		}
		latest = id
		intent.Window.Tick++
	}
	head := retirementHead(t, s)
	foreign := head
	foreign.Namespace = "other"
	if _, err := s.RetireClockHistory(ctx, foreign, 0); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err := s.RetireClockHistory(ctx, head, 257); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
	got, err := s.RetireClockHistory(ctx, head, 0)
	if err != nil || got.RemovedAttempts != 2 || got.RetainedAttempts != 1 {
		t.Fatal(got, err)
	}
	if _, err = s.LookupClockAttempt(ctx, latest); err != nil {
		t.Fatal(err)
	}
}
func TestClockRetirementPinnedCapacity(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "capacity.db"))
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	ns, h, err := loadClockSequence(ctx, tx)
	if err != nil {
		t.Fatal(err)
	}
	for i := uint64(1); i <= 4096; i++ {
		id, _ := ClockRequestID(ns, i)
		intent := clockIntent(id)
		intent.Key = id
		v := ClockAttempt{Intent: intent, NativeAttempt: &c.AttemptKey{ControllerSessionId: proto.String(string(ns)), ActionId: proto.String(fmt.Sprintf("native-%d", i)), AttemptId: proto.Uint64(1)}, Phase: ClockDispatched}
		payload, err := encodeClockIntent(v)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = tx.ExecContext(ctx, "INSERT INTO clock_attempts(request_id,native_action_id,payload,phase) VALUES(?,?,?,?)", id, v.NativeAttempt.GetActionId(), payload, v.Phase); err != nil {
			t.Fatal(err)
		}
		h.Retained = append(h.Retained, i)
	}
	h.LastAllocated = 4096
	if err = saveClockSequence(ctx, tx, h); err != nil {
		t.Fatal(err)
	}
	if err = tx.Commit(); err != nil {
		t.Fatal(err)
	}
	got, err := s.RetireClockHistory(ctx, retirementHead(t, s), 0)
	if err != nil || got.RemovedAttempts != 0 || got.RetainedAttempts != 4096 {
		t.Fatal(got, err)
	}
	id, _ := got.State.NextRequestID()
	intent := clockIntent(id)
	intent.Key = id
	if _, _, err = s.PrepareClock(ctx, intent); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
}
