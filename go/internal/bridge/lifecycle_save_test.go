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

func pbSaveRequest() *l.SaveRequest {
	return &l.SaveRequest{
		Player:   &l.PlayerLifecycleContext{Identity: pbIdentity(), PlayerDirection: proto.Uint64(7), RequestId: proto.String("req-1")},
		SaveName: proto.String("checkpoint-1"),
	}
}
func pbSaveCompleted(request *l.SaveRequest) *l.SaveReply {
	return &l.SaveReply{Outcome: &l.SaveReply_Completed{Completed: &l.SaveCompleted{
		RequestId: request.Player.RequestId, SaveName: request.SaveName, Context: pbContext(),
		Paused: proto.Bool(true), PlayerDirection: request.Player.PlayerDirection,
	}}}
}

func TestSaveHappyPathValidatesEchoedFields(t *testing.T) {
	request := pbSaveRequest()
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pbSaveCompleted(request)), nil
	}}
	client := testClient(t, s, testBudget)
	save, err := NewLifecycleSave(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := save.Save(context.Background(), request)
	if err != nil || reply.GetCompleted() == nil || len(raw.Envelope) == 0 {
		t.Fatalf("save %v %v", reply, err)
	}
	if reply.GetCompleted().GetSaveName() != "checkpoint-1" || !reply.GetCompleted().GetPaused() {
		t.Fatal("completed fields not preserved")
	}
}

func TestSaveRejectsMalformedRequestBeforeDispatch(t *testing.T) {
	s := &testServer{schema: protoSchema}
	client := testClient(t, s, testBudget)
	save, err := NewLifecycleSave(client)
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*l.SaveRequest){
		func(r *l.SaveRequest) { r.Player = nil },
		func(r *l.SaveRequest) { r.Player.Identity = nil },
		func(r *l.SaveRequest) { r.Player.Identity.MapId = nil },
		func(r *l.SaveRequest) { r.Player.RequestId = nil },
		func(r *l.SaveRequest) { r.Player.PlayerDirection = proto.Uint64(0) },
		func(r *l.SaveRequest) { r.Player.PlayerDirection = nil },
		func(r *l.SaveRequest) { r.SaveName = nil },
		func(r *l.SaveRequest) { r.SaveName = proto.String("") },
		func(r *l.SaveRequest) { r.ExpectedTick = proto.Int64(-1) },
	} {
		request := pbSaveRequest()
		change(request)
		if _, _, err := save.Save(context.Background(), request); !errors.Is(err, ErrContract) {
			t.Fatalf("invalid request accepted: %v", err)
		}
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid request dispatched")
	}
	if _, _, err := (&LifecycleSave{}).Save(context.Background(), pbSaveRequest()); err == nil {
		t.Fatal("nil client accepted")
	}
}

func TestSaveRejectsIncompleteOrMismatchedCompleted(t *testing.T) {
	for name, change := range map[string]func(*l.SaveReply, *l.SaveRequest){
		"missing context": func(r *l.SaveReply, _ *l.SaveRequest) { r.GetCompleted().Context = nil },
		"identity mismatch": func(r *l.SaveReply, _ *l.SaveRequest) {
			r.GetCompleted().Context.Identity.LoadToken = proto.String("other")
		},
		"not paused":          func(r *l.SaveReply, _ *l.SaveRequest) { r.GetCompleted().Paused = proto.Bool(false) },
		"missing paused":      func(r *l.SaveReply, _ *l.SaveRequest) { r.GetCompleted().Paused = nil },
		"request id mismatch": func(r *l.SaveReply, _ *l.SaveRequest) { r.GetCompleted().RequestId = proto.String("other") },
		"save name mismatch":  func(r *l.SaveReply, _ *l.SaveRequest) { r.GetCompleted().SaveName = proto.String("other") },
		"direction mismatch":  func(r *l.SaveReply, _ *l.SaveRequest) { r.GetCompleted().PlayerDirection = proto.Uint64(99) },
	} {
		t.Run(name, func(t *testing.T) {
			request := pbSaveRequest()
			reply := pbSaveCompleted(request)
			change(reply, request)
			s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				return pbResult(reply), nil
			}}
			save, err := NewLifecycleSave(testClient(t, s, testBudget))
			if err != nil {
				t.Fatal(err)
			}
			if _, _, err := save.Save(context.Background(), request); err == nil {
				t.Fatalf("invalid completed reply accepted (%s)", name)
			}
		})
	}
}

func TestSaveExpectedTickMustMatchCompletedContext(t *testing.T) {
	request := pbSaveRequest()
	request.ExpectedTick = proto.Int64(pbContext().GetTick())
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pbSaveCompleted(request)), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := save.Save(context.Background(), request); err != nil {
		t.Fatal(err)
	}
	request.ExpectedTick = proto.Int64(pbContext().GetTick() + 1)
	if _, _, err := save.Save(context.Background(), request); err == nil {
		t.Fatal("tick moved but accepted")
	}
}

func TestSaveUncertainOutcomeReturnsTypedRefusal(t *testing.T) {
	request := pbSaveRequest()
	uncertainReply := &l.SaveReply{Outcome: &l.SaveReply_Uncertain{Uncertain: &l.SaveUncertain{
		RequestId: request.Player.RequestId, SaveName: request.SaveName, ObservedContext: pbContext(), Detail: proto.String("colony changed"),
	}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(uncertainReply), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := save.Save(context.Background(), request)
	var uncertain *SaveUncertain
	if !errors.As(err, &uncertain) || !errors.Is(err, ErrSaveUncertain) || reply != nil || len(raw.Envelope) == 0 {
		t.Fatalf("uncertain outcome not preserved: %v", err)
	}
}

func TestSaveFailureOutcomeReturnsNativeFailure(t *testing.T) {
	request := pbSaveRequest()
	failureReply := &l.SaveReply{Outcome: &l.SaveReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_IDENTITY.Enum(), Detail: proto.String("colony changed")}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(failureReply), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := save.Save(context.Background(), request)
	var refusal *NativeFailure
	if !errors.As(err, &refusal) || reply != nil {
		t.Fatalf("native failure not preserved: %v", err)
	}
}

func TestReadSaveRequiresRequestID(t *testing.T) {
	s := &testServer{schema: protoSchema}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := save.ReadSave(context.Background(), ""); !errors.Is(err, ErrContract) {
		t.Fatalf("empty request id accepted: %v", err)
	}
	if len(s.calls) != 0 {
		t.Fatal("invalid request dispatched")
	}
	if _, _, err := (&LifecycleSave{}).ReadSave(context.Background(), "req-1"); err == nil {
		t.Fatal("nil client accepted")
	}
}

func TestReadSaveHappyPathReplaysCompleted(t *testing.T) {
	request := pbSaveRequest()
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(pbSaveCompleted(request)), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := save.ReadSave(context.Background(), request.Player.GetRequestId())
	if err != nil || reply.GetCompleted() == nil || len(raw.Envelope) == 0 {
		t.Fatalf("read save %v %v", reply, err)
	}
	if reply.GetCompleted().GetSaveName() != "checkpoint-1" {
		t.Fatal("completed fields not preserved")
	}
}

func TestReadSaveUncertainOutcomeReturnsTypedRefusal(t *testing.T) {
	request := pbSaveRequest()
	uncertainReply := &l.SaveReply{Outcome: &l.SaveReply_Uncertain{Uncertain: &l.SaveUncertain{
		RequestId: request.Player.RequestId, SaveName: request.SaveName, ObservedContext: pbContext(), Detail: proto.String("colony changed"),
	}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(uncertainReply), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, raw, err := save.ReadSave(context.Background(), request.Player.GetRequestId())
	var uncertain *SaveUncertain
	if !errors.As(err, &uncertain) || !errors.Is(err, ErrSaveUncertain) || reply != nil || len(raw.Envelope) == 0 {
		t.Fatalf("uncertain outcome not preserved: %v", err)
	}
}

func TestReadSaveUnknownRequestIDReturnsFailure(t *testing.T) {
	failureReply := &l.SaveReply{Outcome: &l.SaveReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_NOT_FOUND.Enum(), Detail: proto.String("unknown or expired save request id")}}}
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(failureReply), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := save.ReadSave(context.Background(), "req-unknown")
	var refusal *NativeFailure
	if !errors.As(err, &refusal) || reply != nil {
		t.Fatalf("unknown request id not refused: %v", err)
	}
}

func TestReadSaveMismatchedRequestIDRejected(t *testing.T) {
	request := pbSaveRequest()
	completed := pbSaveCompleted(request)
	completed.GetCompleted().RequestId = proto.String("other")
	s := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(completed), nil
	}}
	save, err := NewLifecycleSave(testClient(t, s, testBudget))
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err := save.ReadSave(context.Background(), request.Player.GetRequestId()); !errors.Is(err, ErrContract) {
		t.Fatalf("mismatched request id accepted: %v", err)
	}
}
