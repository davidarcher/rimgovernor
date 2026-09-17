package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func clockScopeProof(v ClockAttempt) *c.ObservationContext {
	id := proto.Clone(clock.Expectation(v).Identity).(*c.Identity)
	id.LoadToken = proto.String("replacement-load")
	return &c.ObservationContext{Identity: id, Tick: proto.Int64(0)}
}

func TestClockScopeRetirementPersistsWithoutChangingOutcome(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	_, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "start")))
	if err != nil {
		t.Fatal(err)
	}
	_, err = s.DispatchClock(ctx, clockTestID(t, s, "start"))
	if err != nil {
		t.Fatal(err)
	}
	v, err := s.MarkClockUncertain(ctx, clockTestID(t, s, "start"))
	if err != nil {
		t.Fatal(err)
	}
	proof := clockScopeProof(v)
	saved, err := s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), proof)
	if err != nil || saved.Phase != v.Phase || !proto.Equal(saved.Reply, v.Reply) || !proto.Equal(saved.NativeAttempt, v.NativeAttempt) {
		t.Fatal(saved, err)
	}
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), proof); err != nil {
		t.Fatal(err)
	}
	proof.Tick = proto.Int64(1)
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), proof); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	saved.SupersededAt.Tick = proto.Int64(999)
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	replay, created, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "start")))
	if err != nil || created || replay.SupersededAt.GetTick() != 0 {
		t.Fatal(replay, created, err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "start")); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.MarkClockUncertain(ctx, clockTestID(t, s, "start")); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "start"), clockApplied(v)); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	all, err := s.LoadClockAttempts(ctx, 1)
	if err != nil || len(all) != 1 || all[0].SupersededAt.GetTick() != 0 || all[0].Phase != ClockUncertain {
		t.Fatal(all, err)
	}
	epochs, err := s.LoadClockEpochs(ctx, 1)
	if err != nil || len(epochs) != 0 {
		t.Fatal(epochs, err)
	}
}

func TestClockScopeRequiresPositiveReplacementAndUnresolvedStart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	v, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "start")))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), clockScopeProof(v)); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	v, err = s.DispatchClock(ctx, clockTestID(t, s, "start"))
	if err != nil {
		t.Fatal(err)
	}
	same := &c.ObservationContext{Identity: clock.Expectation(v).Identity, Tick: proto.Int64(100), NativeGeneration: proto.Uint64(999)}
	for _, proof := range []*c.ObservationContext{nil, same, {Identity: clockScopeProof(v).Identity}, {Identity: clockScopeProof(v).Identity, Tick: proto.Int64(-1)}} {
		if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), proof); err == nil {
			t.Fatal("invalid replacement accepted", proof)
		}
	}
	unknown := clockScopeProof(v)
	unknown.ProtoReflect().SetUnknown([]byte{0x20, 1})
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), unknown); err == nil {
		t.Fatal("unknown fields accepted")
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "start"), clockApplied(v)); err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), clockScopeProof(v)); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	intent := clockIntent(clockTestID(t, s, "renew"))
	intent.Command = bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: clockApplied(v).GetReceipt().GetApplied().GetStatus().GetRunning().Epoch, LeaseMS: 1000}}
	r, _, err := s.PrepareClock(ctx, intent)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "renew")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "renew"), clockScopeProof(r)); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
}

func TestClockScopeRollbackAndCorruptEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	v, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "start")))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "start")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec(`CREATE TRIGGER reject_scope BEFORE UPDATE OF scope_context ON clock_attempts BEGIN SELECT RAISE(ABORT,'test'); END`); err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), clockScopeProof(v)); err == nil {
		t.Fatal("failed update accepted")
	}
	got, err := s.LookupClockAttempt(ctx, clockTestID(t, s, "start"))
	if err != nil || got.SupersededAt != nil {
		t.Fatal(got, err)
	}
	if _, err = s.db.Exec("DROP TRIGGER reject_scope"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockScopeSuperseded(ctx, clockTestID(t, s, "start"), clockScopeProof(v)); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("INSERT INTO clock_epochs(start_request_id,stage,sequence) VALUES(?,'required','0')", clockTestID(t, s, "start")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LookupClockAttempt(ctx, clockTestID(t, s, "start")); err == nil {
		t.Fatal("retired attempt adopted epoch")
	}
	if _, err = s.db.Exec("DELETE FROM clock_epochs"); err != nil {
		t.Fatal(err)
	}
	for _, data := range [][]byte{{0xff}, make([]byte, clock.RecordLimit+1)} {
		if _, err = s.db.Exec("UPDATE clock_attempts SET scope_context=?", data); err != nil {
			t.Fatal(err)
		}
		if _, err = s.LoadClockAttempts(ctx, 1); err == nil {
			t.Fatal("corrupt proof accepted")
		}
	}
}
