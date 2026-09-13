package buildingruntime

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

type saveFake struct {
	calls   int
	last    *l.SaveRequest
	reply   *l.SaveReply
	err     error
	onCall  func(*l.SaveRequest)
}

func (f *saveFake) Save(_ context.Context, request *l.SaveRequest) (*l.SaveReply, bridge.Result, error) {
	f.calls++
	f.last = request
	if f.onCall != nil {
		f.onCall(request)
	}
	if f.err != nil {
		return nil, bridge.Result{}, f.err
	}
	return f.reply, bridge.Result{}, nil
}

func completedFor(request *l.SaveRequest, tick int64) *l.SaveReply {
	return &l.SaveReply{Outcome: &l.SaveReply_Completed{Completed: &l.SaveCompleted{
		RequestId: request.Player.RequestId, SaveName: request.SaveName,
		Context: &c.ObservationContext{Identity: proto.Clone(request.Player.Identity).(*c.Identity), Tick: proto.Int64(tick), NativeGeneration: proto.Uint64(2)},
		Paused:  proto.Bool(true), PlayerDirection: request.Player.PlayerDirection,
	}}}
}

func TestCheckpointHappyPathDrainsAndSaves(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	scope := controlScope()
	if _, err := control.Acquire(context.Background(), scope); err != nil {
		t.Fatal(err)
	}
	drained := 0
	control.config.CleanupWrites = func(context.Context) error { drained++; return nil }
	save := &saveFake{}
	save.onCall = func(request *l.SaveRequest) { save.reply = completedFor(request, 10) }
	result, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: scope, SaveName: "checkpoint-1"})
	if err != nil {
		t.Fatal(err)
	}
	if drained != 1 || save.calls != 1 {
		t.Fatal("checkpoint did not drain/save exactly once")
	}
	if result.SaveName != "checkpoint-1" || !result.Paused || result.Tick != 10 {
		t.Fatalf("unexpected result: %+v", result)
	}
	if save.last.Player.GetPlayerDirection() != uint64(scope.Direction) {
		t.Fatal("wrong direction sent")
	}
}

func TestCheckpointRefusesWithoutLiveAuthority(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	save := &saveFake{}
	if _, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: controlScope(), SaveName: "checkpoint-1"}); !errors.Is(err, ErrCheckpoint) {
		t.Fatal("checkpoint proceeded without live authority")
	}
	if save.calls != 0 {
		t.Fatal("save dispatched without authority")
	}
}

func TestCheckpointRefusesStaleDirectionOrForeignWorld(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	scope := controlScope()
	if _, err := control.Acquire(context.Background(), scope); err != nil {
		t.Fatal(err)
	}
	save := &saveFake{}
	for name, mutate := range map[string]func(domain.GenerationSnapshot) domain.GenerationSnapshot{
		"wrong direction": func(s domain.GenerationSnapshot) domain.GenerationSnapshot { s.Direction++; return s },
		"wrong colony":    func(s domain.GenerationSnapshot) domain.GenerationSnapshot { s.Colony = "foreign"; return s },
		"wrong map":       func(s domain.GenerationSnapshot) domain.GenerationSnapshot { s.Map++; return s },
		"wrong load":      func(s domain.GenerationSnapshot) domain.GenerationSnapshot { s.Load = "foreign"; return s },
	} {
		t.Run(name, func(t *testing.T) {
			expected := mutate(scope)
			if _, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: expected, SaveName: "checkpoint-1"}); !errors.Is(err, ErrCheckpoint) {
				t.Fatal("stale direction or foreign instance admitted")
			}
		})
	}
	if save.calls != 0 {
		t.Fatal("save dispatched for stale/foreign request")
	}
}

func TestCheckpointRejectsInvalidRequestShape(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	scope := controlScope()
	if _, err := control.Acquire(context.Background(), scope); err != nil {
		t.Fatal(err)
	}
	save := &saveFake{}
	for name, request := range map[string]CheckpointRequest{
		"no direction": {Expected: func() domain.GenerationSnapshot { s := scope; s.Direction = 0; return s }(), SaveName: "checkpoint-1"},
		"empty name":   {Expected: scope, SaveName: ""},
		"nil save":     {Expected: scope, SaveName: "checkpoint-1"},
	} {
		t.Run(name, func(t *testing.T) {
			var err error
			if name == "nil save" {
				_, err = control.Checkpoint(context.Background(), nil, request)
			} else {
				_, err = control.Checkpoint(context.Background(), save, request)
			}
			if !errors.Is(err, ErrCheckpoint) {
				t.Fatalf("invalid request accepted: %v", err)
			}
		})
	}
	if save.calls != 0 {
		t.Fatal("save dispatched for invalid request")
	}
}

func TestCheckpointNeverAcceptsUncertainOutcome(t *testing.T) {
	t.Parallel()
	control, _, sink, _ := controlFixture(t, nil)
	scope := controlScope()
	granted, err := control.Acquire(context.Background(), scope)
	if err != nil {
		t.Fatal(err)
	}
	save := &saveFake{}
	save.onCall = func(request *l.SaveRequest) {
		save.reply = &l.SaveReply{Outcome: &l.SaveReply_Uncertain{Uncertain: &l.SaveUncertain{
			RequestId: request.Player.RequestId, SaveName: request.SaveName, Detail: proto.String("colony changed mid save"),
		}}}
	}
	if _, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: scope, SaveName: "checkpoint-1"}); !errors.Is(err, ErrCheckpoint) {
		t.Fatal("uncertain save outcome silently accepted")
	}
	// Authority must remain live: a checkpoint failure is not a revoke.
	if !sink.enabled() {
		t.Fatal("uncertain checkpoint incorrectly disabled authority")
	}
	if _, err := control.Lease(granted); err != nil {
		t.Fatal("checkpoint refusal cost the live lease")
	}
}

func TestCheckpointPropagatesNativeFailure(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	scope := controlScope()
	if _, err := control.Acquire(context.Background(), scope); err != nil {
		t.Fatal(err)
	}
	save := &saveFake{err: errors.New("native save failed")}
	if _, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: scope, SaveName: "checkpoint-1"}); !errors.Is(err, ErrCheckpoint) {
		t.Fatal("native failure not surfaced as checkpoint refusal")
	}
}

func TestCheckpointDrainFailureRefusesBeforeSave(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	scope := controlScope()
	if _, err := control.Acquire(context.Background(), scope); err != nil {
		t.Fatal(err)
	}
	control.config.CleanupWrites = func(context.Context) error { return errors.New("writer drain failed") }
	save := &saveFake{}
	save.onCall = func(request *l.SaveRequest) { save.reply = completedFor(request, 10) }
	if _, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: scope, SaveName: "checkpoint-1"}); !errors.Is(err, ErrCheckpoint) {
		t.Fatal("drain failure did not refuse checkpoint")
	}
	if save.calls != 0 {
		t.Fatal("save dispatched despite failed drain")
	}
}

func TestCheckpointHonorsExpectedTick(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	scope := controlScope()
	if _, err := control.Acquire(context.Background(), scope); err != nil {
		t.Fatal(err)
	}
	save := &saveFake{}
	save.onCall = func(request *l.SaveRequest) {
		if request.GetExpectedTick() != 10 {
			t.Error("expected tick not forwarded")
		}
		save.reply = completedFor(request, 10)
	}
	if _, err := control.Checkpoint(context.Background(), save, CheckpointRequest{Expected: scope, SaveName: "checkpoint-1", ExpectedTick: 10, HasExpectedTick: true}); err != nil {
		t.Fatal(err)
	}
}
