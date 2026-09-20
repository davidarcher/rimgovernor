package bridge

import (
	"context"
	"errors"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func pbLoadRequest() *l.LoadRequest {
	return &l.LoadRequest{
		RequestId: proto.String("load-req-1"),
		SaveName:  proto.String("checkpoint-1"),
		Readiness: l.Readiness_READINESS_MAP.Enum(),
	}
}
func pbLoadCompleted(request *l.LoadRequest) *l.LoadReply {
	return &l.LoadReply{Outcome: &l.LoadReply_Completed{Completed: &l.LoadCompleted{
		RequestId: request.RequestId, SaveName: request.SaveName,
		Loaded:    &l.LoadedIdentity{Context: pbContext(), Paused: proto.Bool(true)},
		Readiness: l.Readiness_READINESS_MAP.Enum(),
	}}}
}

func TestLoadHappyPathValidatesEchoedFields(t *testing.T) {
	request := pbLoadRequest()
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pbLoadCompleted(request)), nil
	}}
	client := testClient(t, s, testBudget)
	load, err := NewLifecycleLoad(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := load.Load(context.Background(), request)
	if err != nil || reply.GetCompleted() == nil || len(raw.Envelope) == 0 {
		t.Fatalf("load %v %v", reply, err)
	}
	if reply.GetCompleted().GetSaveName() != "checkpoint-1" || reply.GetCompleted().GetReadiness() != l.Readiness_READINESS_MAP {
		t.Fatal("completed fields not preserved")
	}
}

func TestLoadRejectsMalformedRequestBeforeDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, testBudget)
	load, err := NewLifecycleLoad(client)
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*l.LoadRequest){
		func(r *l.LoadRequest) { r.RequestId = nil },
		func(r *l.LoadRequest) { r.RequestId = proto.String("") },
		func(r *l.LoadRequest) { r.SaveName = nil },
		func(r *l.LoadRequest) { r.SaveName = proto.String("") },
		func(r *l.LoadRequest) { r.Readiness = l.Readiness(99).Enum() },
		func(r *l.LoadRequest) {
			r.ExpectedPlayer = &l.PlayerLifecycleContext{Identity: &c.Identity{ColonyId: proto.String("c")}}
		},
		func(r *l.LoadRequest) { r.ExpectedInstanceId = proto.String("") },
	} {
		request := pbLoadRequest()
		change(request)
		if _, _, err := load.Load(context.Background(), request); !errors.Is(err, ErrContract) {
			t.Fatalf("invalid request accepted: %v", err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid request dispatched")
	}
	if _, _, err := (&LifecycleLoad{}).Load(context.Background(), pbLoadRequest()); err == nil {
		t.Fatal("nil client accepted")
	}
}

func TestLoadRejectsIncompleteOrMismatchedCompleted(t *testing.T) {
	for name, change := range map[string]func(*l.LoadReply, *l.LoadRequest){
		"missing loaded":      func(r *l.LoadReply, _ *l.LoadRequest) { r.GetCompleted().Loaded = nil },
		"missing context":     func(r *l.LoadReply, _ *l.LoadRequest) { r.GetCompleted().Loaded.Context = nil },
		"identity invalid":    func(r *l.LoadReply, _ *l.LoadRequest) { r.GetCompleted().Loaded.Context.Identity.LoadToken = nil },
		"request id mismatch": func(r *l.LoadReply, _ *l.LoadRequest) { r.GetCompleted().RequestId = proto.String("other") },
		"save name mismatch":  func(r *l.LoadReply, _ *l.LoadRequest) { r.GetCompleted().SaveName = proto.String("other") },
		"missing readiness":   func(r *l.LoadReply, _ *l.LoadRequest) { r.GetCompleted().Readiness = nil },
		"unspecified readiness": func(r *l.LoadReply, _ *l.LoadRequest) {
			r.GetCompleted().Readiness = l.Readiness_READINESS_UNSPECIFIED.Enum()
		},
	} {
		t.Run(name, func(t *testing.T) {
			request := pbLoadRequest()
			reply := pbLoadCompleted(request)
			change(reply, request)
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(reply), nil
			}}
			load, err := NewLifecycleLoad(testClient(t, s, testBudget))
			if err != nil {
				t.Fatal(err)
			}
			if _, _, err := load.Load(context.Background(), request); err == nil {
				t.Fatalf("invalid completed reply accepted (%s)", name)
			}
		})
	}
}

func TestLoadPendingOutcomeReturnsTypedRefusal(t *testing.T) {
	request := pbLoadRequest()
	pendingReply := &l.LoadReply{Outcome: &l.LoadReply_Pending{Pending: &l.LoadPending{
		RequestId: request.RequestId, SaveName: request.SaveName, ProcessConnected: proto.Bool(true), MapReady: proto.Bool(false),
	}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pendingReply), nil
	}}
	load, err := NewLifecycleLoad(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := load.Load(context.Background(), request)
	var pending *LoadPending
	if !errors.As(err, &pending) || !errors.Is(err, ErrLoadPending) || reply != nil || len(raw.Envelope) == 0 {
		t.Fatalf("pending outcome not preserved: %v", err)
	}
}

func TestLoadSupersededOutcomeReturnsTypedRefusal(t *testing.T) {
	request := pbLoadRequest()
	supersededReply := &l.LoadReply{Outcome: &l.LoadReply_Superseded{Superseded: &l.LoadSuperseded{
		RequestId: request.RequestId, ObservedContext: pbContext(), Detail: proto.String("a newer load request preempted this one"),
	}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(supersededReply), nil
	}}
	load, err := NewLifecycleLoad(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := load.Load(context.Background(), request)
	var superseded *LoadSuperseded
	if !errors.As(err, &superseded) || !errors.Is(err, ErrLoadSuperseded) || reply != nil || len(raw.Envelope) == 0 {
		t.Fatalf("superseded outcome not preserved: %v", err)
	}
}

func TestLoadFailureOutcomeReturnsNativeFailure(t *testing.T) {
	request := pbLoadRequest()
	failureReply := &l.LoadReply{Outcome: &l.LoadReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Detail: proto.String("bad request")}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(failureReply), nil
	}}
	load, err := NewLifecycleLoad(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := load.Load(context.Background(), request)
	var refusal *NativeFailure
	if !errors.As(err, &refusal) || reply != nil {
		t.Fatalf("native failure not preserved: %v", err)
	}
}

func TestReadLoadRequiresRequestID(t *testing.T) {
	s := &testServer{schema: protoSchema}
	load, err := NewLifecycleLoad(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := load.ReadLoad(context.Background(), ""); !errors.Is(err, ErrContract) {
		t.Fatalf("empty request id accepted: %v", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid request dispatched")
	}
}

func TestReadLoadHappyPath(t *testing.T) {
	request := pbLoadRequest()
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pbLoadCompleted(request)), nil
	}}
	load, err := NewLifecycleLoad(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := load.ReadLoad(context.Background(), request.GetRequestId())
	if err != nil || reply.GetCompleted() == nil {
		t.Fatalf("read load %v %v", reply, err)
	}
}
