package buildingruntime

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"google.golang.org/protobuf/proto"
)

// loadFake is a scripted LifecycleLoader: Load and ReadLoad each pop the next
// entry off their own queue, letting a test express "pending N times, then
// completed" without a real native process.
type loadFake struct {
	loadReplies []func(*l.LoadRequest) (*l.LoadReply, error)
	readReplies []func(string) (*l.LoadReply, error)
	loadCalls   int32
	readCalls   int32
	lastLoadReq *l.LoadRequest
}

func (f *loadFake) Load(_ context.Context, request *l.LoadRequest) (*l.LoadReply, bridge.Result, error) {
	f.lastLoadReq = request
	idx := int(atomic.AddInt32(&f.loadCalls, 1)) - 1
	if idx >= len(f.loadReplies) {
		return nil, bridge.Result{}, errors.New("loadFake: no scripted Load reply")
	}
	reply, err := f.loadReplies[idx](request)
	if err != nil {
		return nil, bridge.Result{}, err
	}
	return reply, bridge.Result{}, nil
}

func (f *loadFake) ReadLoad(_ context.Context, requestID string) (*l.LoadReply, bridge.Result, error) {
	idx := int(atomic.AddInt32(&f.readCalls, 1)) - 1
	if idx >= len(f.readReplies) {
		return nil, bridge.Result{}, errors.New("loadFake: no scripted ReadLoad reply")
	}
	reply, err := f.readReplies[idx](requestID)
	if err != nil {
		return nil, bridge.Result{}, err
	}
	return reply, bridge.Result{}, nil
}

func loadCompletedReply(requestID, saveName, colony string) *l.LoadReply {
	return loadCompletedReplyWithReadiness(requestID, saveName, colony, l.Readiness_READINESS_MAP)
}
func loadCompletedReplyWithReadiness(requestID, saveName, colony string, readiness l.Readiness) *l.LoadReply {
	return &l.LoadReply{Outcome: &l.LoadReply_Completed{Completed: &l.LoadCompleted{
		RequestId: proto.String(requestID), SaveName: proto.String(saveName),
		Loaded: &l.LoadedIdentity{
			Context: &c.ObservationContext{
				Identity: &c.Identity{ColonyId: proto.String(colony), LoadToken: proto.String("fresh-token"), MapId: proto.Int32(0)},
				Tick:     proto.Int64(42),
			},
			Paused: proto.Bool(true),
		},
		Readiness: readiness.Enum(),
	}}}
}
func loadPendingReply(requestID, saveName string) *l.LoadReply {
	return &l.LoadReply{Outcome: &l.LoadReply_Pending{Pending: &l.LoadPending{
		RequestId: proto.String(requestID), SaveName: proto.String(saveName), ProcessConnected: proto.Bool(true), MapReady: proto.Bool(false),
	}}}
}
func loadSupersededReply(requestID string) *l.LoadReply {
	return &l.LoadReply{Outcome: &l.LoadReply_Superseded{Superseded: &l.LoadSuperseded{
		RequestId: proto.String(requestID), Detail: proto.String("a newer load request preempted this one"),
	}}}
}

// asPending/asSuperseded wrap a *l.LoadReply the way the real bridge client
// would: LoadFake stands in for LifecycleLoader directly, so it must return
// the same typed errors bridge.LifecycleLoad.Load/ReadLoad would for
// pending/superseded outcomes -- LoadBoundary depends on errors.As matching
// *bridge.LoadPending, not on the raw oneof.
func asPending(reply *l.LoadReply) (*l.LoadReply, error) {
	return nil, &bridge.LoadPending{Value: reply.GetOutcome().(*l.LoadReply_Pending).Pending}
}
func asSuperseded(reply *l.LoadReply) (*l.LoadReply, error) {
	return nil, &bridge.LoadSuperseded{Value: reply.GetOutcome().(*l.LoadReply_Superseded).Superseded}
}

func TestLoadBoundaryImmediateCompletion(t *testing.T) {
	t.Parallel()
	fake := &loadFake{loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
		func(r *l.LoadRequest) (*l.LoadReply, error) {
			return loadCompletedReply(r.GetRequestId(), r.GetSaveName(), "colony-1"), nil
		},
	}}
	boundary := &LoadBoundary{Native: fake}
	result, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1"})
	if err != nil {
		t.Fatal(err)
	}
	if result.SaveName != "save-1" || !result.Paused || result.Tick != 42 || result.Snapshot.Colony != "colony-1" {
		t.Fatalf("unexpected result: %+v", result)
	}
	if fake.readCalls != 0 {
		t.Fatal("polled ReadLoad despite immediate completion")
	}
}

func TestLoadBoundaryPollsThenCompletes(t *testing.T) {
	t.Parallel()
	fake := &loadFake{
		loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
			func(r *l.LoadRequest) (*l.LoadReply, error) {
				return asPending(loadPendingReply(r.GetRequestId(), r.GetSaveName()))
			},
		},
		readReplies: []func(string) (*l.LoadReply, error){
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) { return loadCompletedReply(id, "save-1", "colony-1"), nil },
		},
	}
	boundary := &LoadBoundary{Native: fake, PollInterval: time.Millisecond}
	result, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1", Deadline: time.Now().Add(5 * time.Second)})
	if err != nil {
		t.Fatal(err)
	}
	if result.SaveName != "save-1" || fake.readCalls != 3 {
		t.Fatalf("unexpected result/poll count: %+v readCalls=%d", result, fake.readCalls)
	}
}

func TestLoadBoundarySuperseded(t *testing.T) {
	t.Parallel()
	fake := &loadFake{
		loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
			func(r *l.LoadRequest) (*l.LoadReply, error) {
				return asPending(loadPendingReply(r.GetRequestId(), r.GetSaveName()))
			},
		},
		readReplies: []func(string) (*l.LoadReply, error){
			func(id string) (*l.LoadReply, error) { return asSuperseded(loadSupersededReply(id)) },
		},
	}
	boundary := &LoadBoundary{Native: fake, PollInterval: time.Millisecond}
	_, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1", Deadline: time.Now().Add(5 * time.Second)})
	if !errors.Is(err, ErrLoad) {
		t.Fatal("superseded outcome did not refuse")
	}
	var superseded *bridge.LoadSuperseded
	if !errors.As(err, &superseded) {
		t.Fatal("superseded detail not preserved")
	}
}

func TestLoadBoundaryDeadlineExceeded(t *testing.T) {
	t.Parallel()
	fake := &loadFake{
		loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
			func(r *l.LoadRequest) (*l.LoadReply, error) {
				return asPending(loadPendingReply(r.GetRequestId(), r.GetSaveName()))
			},
		},
		readReplies: []func(string) (*l.LoadReply, error){
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
		},
	}
	boundary := &LoadBoundary{Native: fake, PollInterval: 20 * time.Millisecond}
	_, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1", Deadline: time.Now().Add(40 * time.Millisecond)})
	if !errors.Is(err, ErrLoad) || !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("deadline was not honored: %v", err)
	}
}

func TestLoadBoundaryRejectsInvalidRequestShape(t *testing.T) {
	t.Parallel()
	fake := &loadFake{}
	boundary := &LoadBoundary{Native: fake}
	for name, request := range map[string]LoadRequest{
		"empty request id": {RequestID: "", SaveName: "save-1"},
		"empty save name":  {RequestID: "req-1", SaveName: ""},
		"zero direction":   {RequestID: "req-1", SaveName: "save-1", HasPlayerDirection: true, PlayerDirection: 0},
		"empty colony":     {RequestID: "req-1", SaveName: "save-1", HasExpectedColony: true, ExpectedColony: ""},
		"deadline passed":  {RequestID: "req-1", SaveName: "save-1", Deadline: time.Now().Add(-time.Second)},
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := boundary.Load(context.Background(), request); !errors.Is(err, ErrLoad) {
				t.Fatalf("invalid request accepted: %v", err)
			}
		})
	}
	if fake.loadCalls != 0 {
		t.Fatal("native dispatched for invalid request")
	}
}

func TestLoadBoundaryRejectsNilBoundaryOrNative(t *testing.T) {
	t.Parallel()
	var nilBoundary *LoadBoundary
	if _, err := nilBoundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1"}); !errors.Is(err, ErrLoad) {
		t.Fatal("nil boundary accepted")
	}
	boundary := &LoadBoundary{}
	if _, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1"}); !errors.Is(err, ErrLoad) {
		t.Fatal("missing native accepted")
	}
}

func TestLoadBoundaryRefusesColonyMismatch(t *testing.T) {
	t.Parallel()
	fake := &loadFake{loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
		func(r *l.LoadRequest) (*l.LoadReply, error) {
			return loadCompletedReply(r.GetRequestId(), r.GetSaveName(), "wrong-colony"), nil
		},
	}}
	boundary := &LoadBoundary{Native: fake}
	_, err := boundary.Load(context.Background(), LoadRequest{
		RequestID: "req-1", SaveName: "save-1", HasExpectedColony: true, ExpectedColony: domain.ColonyID("expected-colony"),
	})
	if !errors.Is(err, ErrLoad) {
		t.Fatal("colony mismatch silently accepted")
	}
}

func TestLoadBoundaryRequestsVisualReadiness(t *testing.T) {
	t.Parallel()
	fake := &loadFake{
		loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
			func(r *l.LoadRequest) (*l.LoadReply, error) {
				return asPending(loadPendingReply(r.GetRequestId(), r.GetSaveName()))
			},
		},
		readReplies: []func(string) (*l.LoadReply, error){
			// Native holds LoadCompleted until VISUAL readiness: a MAP-ready
			// but not-yet-visual poll still comes back Pending.
			func(id string) (*l.LoadReply, error) { return asPending(loadPendingReply(id, "save-1")) },
			func(id string) (*l.LoadReply, error) {
				return loadCompletedReplyWithReadiness(id, "save-1", "colony-1", l.Readiness_READINESS_VISUAL), nil
			},
		},
	}
	boundary := &LoadBoundary{Native: fake, PollInterval: time.Millisecond}
	result, err := boundary.Load(context.Background(), LoadRequest{
		RequestID: "req-1", SaveName: "save-1", RequireVisualReadiness: true, Deadline: time.Now().Add(5 * time.Second),
	})
	if err != nil {
		t.Fatal(err)
	}
	if !result.VisualReady {
		t.Fatal("visual readiness was requested but not reflected in the result")
	}
	if fake.lastLoadReq.GetReadiness() != l.Readiness_READINESS_VISUAL {
		t.Fatalf("native request did not ask for VISUAL readiness: %v", fake.lastLoadReq.GetReadiness())
	}
}

func TestLoadBoundaryDefaultsToMapReadiness(t *testing.T) {
	t.Parallel()
	fake := &loadFake{loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
		func(r *l.LoadRequest) (*l.LoadReply, error) {
			return loadCompletedReply(r.GetRequestId(), r.GetSaveName(), "colony-1"), nil
		},
	}}
	boundary := &LoadBoundary{Native: fake}
	result, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1"})
	if err != nil {
		t.Fatal(err)
	}
	if result.VisualReady {
		t.Fatal("MAP-only request reported visual readiness")
	}
	if fake.lastLoadReq.GetReadiness() != l.Readiness_READINESS_MAP {
		t.Fatalf("default request did not ask for MAP readiness: %v", fake.lastLoadReq.GetReadiness())
	}
}

func TestLoadBoundaryPropagatesNativeFailure(t *testing.T) {
	t.Parallel()
	fake := &loadFake{loadReplies: []func(*l.LoadRequest) (*l.LoadReply, error){
		func(*l.LoadRequest) (*l.LoadReply, error) { return nil, errors.New("native load failed") },
	}}
	boundary := &LoadBoundary{Native: fake}
	if _, err := boundary.Load(context.Background(), LoadRequest{RequestID: "req-1", SaveName: "save-1"}); !errors.Is(err, ErrLoad) {
		t.Fatal("native failure not surfaced as load refusal")
	}
}
