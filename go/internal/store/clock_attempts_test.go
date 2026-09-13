package store

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"testing"
	"time"
)

func clockIntent(id string) ClockIntent {
	s := scope()
	s.Native = 7
	return ClockIntent{RequestID: id, Snapshot: s, Command: bridge.ClockCommand{Start: &bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL, LeaseMS: 1000, MaxTicks: 100, Policy: &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(), HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.2), HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}}}}
}
func clockApplied(v ClockAttempt) *k.ControlReply {
	e := clock.Expectation(v)
	ctx := &c.ObservationContext{Identity: e.Identity, NativeGeneration: proto.Uint64(e.NativeGeneration), Tick: proto.Int64(12)}
	var epoch *k.Epoch
	if start := v.Intent.Command.Start; start != nil {
		epoch = &k.Epoch{Owner: &k.EpochOwner{ControllerSessionId: proto.String(e.Attempt.GetControllerSessionId()), Epoch: proto.Int64(1)}, Origin: ctx, RequestedSpeed: start.Speed.Enum(), Policy: proto.Clone(start.Policy).(*k.WatchPolicy), StartTick: proto.Int64(12), TickDeadline: proto.Int64(112), LastTick: proto.Int64(12), LeaseRemainingMs: proto.Uint32(900)}
	} else if renew := v.Intent.Command.Renew; renew != nil {
		epoch = proto.Clone(renew.Original).(*k.Epoch)
	} else {
		epoch = proto.Clone(v.Intent.Command.Speed.Original).(*k.Epoch)
		epoch.RequestedSpeed = v.Intent.Command.Speed.Speed.Enum()
	}
	status := &k.Status{Context: ctx, State: &k.Status_Running{Running: &k.Running{Epoch: epoch}}, NativeTickBoundary: proto.Bool(true), DurableEvents: proto.Bool(true), NewestCursor: proto.Int64(0), ObservedSpeed: k.ObservedSpeed_OBSERVED_SPEED_NORMAL.Enum(), ActualPaused: proto.Bool(false), EvidenceCompleteness: &c.PageInfo{Complete: proto.Bool(true)}}
	return &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: &k.ControlReceipt{Attempt: e.Attempt, AdmittedContext: ctx, AuthorizingOwner: e.Owner, Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: status}}}}}
}
func TestClockJournalReopenAndImmutableEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "clock.db")
	s := open(t, path)
	input := clockIntent(clockTestID(t, s, "start"))
	v, created, err := s.PrepareClock(ctx, input)
	if err != nil || !created || v.Phase != ClockPrepared {
		t.Fatal(v, created, err)
	}
	input.Command.Start.Policy.AcknowledgedHostileIds = []string{"mutated"}
	replay, created, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "start")))
	if err != nil || created || !proto.Equal(v.NativeAttempt, replay.NativeAttempt) {
		t.Fatal(replay, err)
	}
	if _, _, err = s.PrepareClock(ctx, input); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "start"), clockApplied(v)); err == nil {
		t.Fatal("reply before dispatch")
	}
	if v, err = s.DispatchClock(ctx, clockTestID(t, s, "start")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "start")); !errors.Is(err, ErrConflict) {
		t.Fatal("dispatch replay authorized", err)
	}
	if _, err = s.MarkClockUncertain(ctx, clockTestID(t, s, "start")); err != nil {
		t.Fatal(err)
	}
	if err = s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	v, err = s.LookupClockAttempt(ctx, clockTestID(t, s, "start"))
	if err != nil || v.Phase != ClockUncertain || v.Reply != nil {
		t.Fatal(v, err)
	}
	reply := clockApplied(v)
	saved, err := s.RecordClockReply(ctx, clockTestID(t, s, "start"), reply)
	if err != nil || saved.Phase != ClockApplied {
		t.Fatal(saved, err)
	}
	reply.GetReceipt().Attempt.ActionId = proto.String("foreign")
	current, err := s.LookupClockAttempt(ctx, clockTestID(t, s, "start"))
	if err != nil || !proto.Equal(saved.Reply, current.Reply) {
		t.Fatal(current, err)
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "start"), current.Reply); err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockUncertain(ctx, clockTestID(t, s, "start")); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "start"), reply); err == nil {
		t.Fatal("conflicting terminal overwritten")
	}
}
func TestClockReplyClassificationAndOriginalEpoch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "clock.db"))
	for _, test := range []struct {
		id    string
		reply *k.ControlReply
		phase ClockPhase
	}{
		{"refusal", &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}}, ClockRefused},
		{"conflict", &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}}, ClockUncertain},
		{"pending", &k.ControlReply{Outcome: &k.ControlReply_LongEventPending{LongEventPending: &k.LongEventPending{}}}, ClockRefused},
	} {
		if _, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, test.id))); err != nil {
			t.Fatal(err)
		}
		if _, err := s.DispatchClock(ctx, clockTestID(t, s, test.id)); err != nil {
			t.Fatal(err)
		}
		got, err := s.RecordClockReply(ctx, clockTestID(t, s, test.id), test.reply)
		if err != nil || got.Phase != test.phase {
			t.Fatal(got, err)
		}
		if test.phase == ClockUncertain {
			got, err = s.MarkClockUncertain(ctx, clockTestID(t, s, test.id))
			if err != nil || !proto.Equal(got.Reply, test.reply) {
				t.Fatal(got, err)
			}
		}
	}
	start, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "original")))
	if err != nil {
		t.Fatal(err)
	}
	epoch := clockApplied(start).GetReceipt().GetApplied().GetStatus().GetRunning().Epoch
	for _, kind := range []string{"renew", "speed"} {
		intent := clockIntent(clockTestID(t, s, kind))
		intent.Command = bridge.ClockCommand{}
		if kind == "renew" {
			intent.Command.Renew = &bridge.ClockRenew{Original: epoch, LeaseMS: 2000}
		} else {
			intent.Command.Speed = &bridge.ClockSpeed{Original: epoch, Speed: k.Speed_SPEED_FAST}
		}
		v, _, err := s.PrepareClock(ctx, intent)
		if err != nil {
			t.Fatal(err)
		}
		if _, err = s.DispatchClock(ctx, clockTestID(t, s, kind)); err != nil {
			t.Fatal(err)
		}
		if _, err = s.RecordClockReply(ctx, clockTestID(t, s, kind), clockApplied(v)); err != nil {
			t.Fatal(err)
		}
	}
}
func TestClockNamespaceBoundsAndRollback(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	v, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "z")))
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan(t, "collision", domain.ActionID(v.NativeAttempt.GetActionId()))); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err = s.LoadPlan(ctx, "collision"); !errors.Is(err, ErrNotFound) {
		t.Fatal("partial plan retained", err)
	}
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if err = clock.ActionAvailable(ctx, tx, "a"); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	tx.Rollback()
	if _, _, err = s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "a"))); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LoadClockAttempts(ctx, 1); err == nil {
		t.Fatal("catalog silently truncated")
	}
	all, err := s.LoadClockAttempts(ctx, 2)
	if err != nil || len(all) != 2 || all[0].Intent.RequestID != clockTestID(t, s, "z") {
		t.Fatal(all, err)
	}
	if _, err = s.db.Exec("CREATE TRIGGER clock_fail BEFORE UPDATE ON clock_attempts BEGIN SELECT RAISE(ABORT,'failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "a")); err == nil {
		t.Fatal("transaction failure ignored")
	}
	got, err := s.LookupClockAttempt(ctx, clockTestID(t, s, "a"))
	if err != nil || got.Phase != ClockPrepared {
		t.Fatal(got, err)
	}
}
func TestClockMalformedPersistedEvidenceAndCancelledContention(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path := fixture(t)
	if _, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "one"))); err != nil {
		t.Fatal(err)
	}
	other := open(t, path)
	blockedID := clockTestID(t, s, "blocked")
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	cancelled, cancel := context.WithTimeout(ctx, 30*time.Millisecond)
	defer cancel()
	if _, _, err = other.PrepareClock(cancelled, clockIntent(blockedID)); err == nil {
		t.Fatal("contention ignored cancellation")
	}
	tx.Rollback()
	if _, err = s.db.Exec("UPDATE clock_attempts SET payload=? WHERE request_id=?", []byte(`{}`), clockTestID(t, s, "one")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.LookupClockAttempt(ctx, clockTestID(t, s, "one")); err == nil {
		t.Fatal("malformed intent accepted")
	}
	if _, err = s.LoadClockAttempts(ctx, 10); err == nil {
		t.Fatal("corrupt catalog accepted")
	}
}

func TestClockAdmissionEvidenceSurvivesLaterFailures(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	v, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "admitted")))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "admitted")); err != nil {
		t.Fatal(err)
	}
	uncertain := clockApplied(v)
	uncertain.GetReceipt().Outcome = &k.ControlReceipt_Uncertain{Uncertain: &k.UncertainControl{Detail: proto.String("in flight")}}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "admitted"), uncertain); err != nil {
		t.Fatal(err)
	}
	for _, reply := range []*k.ControlReply{
		{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}},
		{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}},
		{Outcome: &k.ControlReply_LongEventPending{LongEventPending: &k.LongEventPending{}}},
	} {
		if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "admitted"), reply); !errors.Is(err, ErrConflict) {
			t.Fatal(err)
		}
		got, e := s.LookupClockAttempt(ctx, clockTestID(t, s, "admitted"))
		if e != nil || got.Phase != ClockUncertain || !proto.Equal(got.Reply, uncertain) {
			t.Fatal(got, e)
		}
	}
	if _, err = s.db.Exec("CREATE TRIGGER clock_no_write BEFORE UPDATE ON clock_attempts BEGIN SELECT RAISE(ABORT,'failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.MarkClockUncertain(ctx, clockTestID(t, s, "admitted")); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "admitted"), uncertain); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("DROP TRIGGER clock_no_write"); err != nil {
		t.Fatal(err)
	}
	applied := clockApplied(v)
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "admitted"), applied); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec("CREATE TRIGGER clock_no_write BEFORE UPDATE ON clock_attempts BEGIN SELECT RAISE(ABORT,'failure'); END"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "admitted"), applied); err != nil {
		t.Fatal(err)
	}
}

func TestClockAttemptCapacityPreservesReplay(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	v, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "first")))
	if err != nil {
		t.Fatal(err)
	}
	payload, err := clock.EncodeIntent(v)
	if err != nil {
		t.Fatal(err)
	}
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	defer tx.Rollback()
	head := clock.SequenceHead{LastAllocated: 4096, Retained: []uint64{1}}
	for i := 1; i < 4096; i++ {
		id, _ := ClockRequestID(ControllerSessionID(v.NativeAttempt.GetControllerSessionId()), uint64(i+1))
		head.Retained = append(head.Retained, uint64(i+1))
		if _, err = tx.ExecContext(ctx, "INSERT INTO clock_attempts(request_id,native_action_id,payload,phase) VALUES(?,?,?,?)", id, id, payload, ClockPrepared); err != nil {
			t.Fatal(err)
		}
	}
	if err = clock.SaveSequence(ctx, tx, head); err != nil {
		t.Fatal(err)
	}
	if err = tx.Commit(); err != nil {
		t.Fatal(err)
	}
	if _, _, err = s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "overflow"))); !errors.Is(err, ErrCapacity) {
		t.Fatal(err)
	}
	if _, err = s.LookupClockAttempt(ctx, clockTestID(t, s, "overflow")); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if _, created, e := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "first"))); e != nil || created {
		t.Fatal(created, e)
	}
	all, err := s.LoadClockAttempts(ctx, 4096)
	if err != nil || len(all) != 4096 {
		t.Fatal(len(all), err)
	}
}

func TestClockAttemptConflictRequiresReceiptResolution(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	v, _, err := s.PrepareClock(ctx, clockIntent(clockTestID(t, s, "conflict")))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.DispatchClock(ctx, clockTestID(t, s, "conflict")); err != nil {
		t.Fatal(err)
	}
	conflictReply := &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "conflict"), conflictReply); err != nil {
		t.Fatal(err)
	}
	refusal := &k.ControlReply{Outcome: &k.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_AUTHORITY_REQUIRED.Enum()}}}
	if _, err = s.RecordClockReply(ctx, clockTestID(t, s, "conflict"), refusal); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	got, err := s.RecordClockReply(ctx, clockTestID(t, s, "conflict"), conflictReply)
	if err != nil || got.Phase != ClockUncertain || !proto.Equal(got.Reply, conflictReply) {
		t.Fatal(got, err)
	}
	got, err = s.RecordClockReply(ctx, clockTestID(t, s, "conflict"), clockApplied(v))
	if err != nil || got.Phase != ClockApplied {
		t.Fatal(got, err)
	}
}
