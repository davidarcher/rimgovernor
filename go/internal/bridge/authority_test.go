package bridge

import (
	"context"
	"encoding/json"
	"errors"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
	"testing"
	"time"
)

func authorityTestContext(generation uint64) *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(12), NativeGeneration: proto.Uint64(generation)}
}
func authorityTestSetMode() *a.SetMode {
	return &a.SetMode{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(7), Mode: a.Mode_MODE_AUTO.Enum()}
}
func authorityTestGranted(generation uint64) *a.ControlReply {
	return &a.ControlReply{Outcome: &a.ControlReply_Granted{Granted: &a.Granted{Context: authorityTestContext(generation), Authority: &a.ActiveAuthority{Mode: a.Mode_MODE_AUTO.Enum()}}}}
}
func authorityTestControl(t *testing.T, handler func(context.Context, nativeArgument) (*mcp.CallToolResult, error)) (*Client, *AuthorityControl) {
	t.Helper()
	client := testClient(t, &testServer{schema: protoSchema, handler: handler}, time.Second)
	control, err := NewAuthorityControl(client)
	if err != nil {
		t.Fatal(err)
	}
	return client, control
}
func TestAuthorityFixedSDKCapabilities(t *testing.T) {
	calls := 0
	client, control := authorityTestControl(t, func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		calls++
		var envelope struct {
			Request string `json:"request"`
		}
		if err := json.Unmarshal(arg.Arguments, &envelope); err != nil {
			t.Fatal(err)
		}
		if arg.Tool == "rimgovernor/authority_read_status" {
			request := &a.StatusRequest{}
			if err := protojson.Unmarshal([]byte(envelope.Request), request); err != nil || !proto.Equal(request.Identity, pbIdentity()) {
				t.Fatalf("read request %v %v", request, err)
			}
			return pbResult(&a.StatusReply{Outcome: &a.StatusReply_Status{Status: &a.Status{Context: authorityTestContext(7), State: &a.Status_Inactive{Inactive: &a.InactiveAuthority{Reason: a.RevocationReason_REVOCATION_REASON_MANUAL.Enum()}}}}}), nil
		}
		if arg.Tool != "rimgovernor/authority_control" {
			t.Fatalf("unexpected method %s", arg.Tool)
		}
		request := &a.ControlRequest{}
		if err := protojson.Unmarshal([]byte(envelope.Request), request); err != nil {
			t.Fatal(err)
		}
		switch operation := request.Operation.(type) {
		case *a.ControlRequest_SetMode:
			if !proto.Equal(operation.SetMode, authorityTestSetMode()) {
				t.Fatal("changed set-mode")
			}
			return pbResult(authorityTestGranted(8)), nil
		case *a.ControlRequest_Revoke:
			return pbResult(&a.ControlReply{Outcome: &a.ControlReply_Revoked{Revoked: &a.Revoked{Context: authorityTestContext(9), Authority: &a.InactiveAuthority{Reason: operation.Revoke.Reason}}}}), nil
		default:
			t.Fatal("missing command")
			return nil, nil
		}
	})
	if _, _, err := client.ReadAuthority(context.Background(), pbIdentity()); err != nil {
		t.Fatal(err)
	}
	if _, err := client.protoRead(context.Background(), "rimgovernor/authority_control", &a.ControlRequest{}, &a.ControlReply{}); !errors.Is(err, ErrContract) {
		t.Fatal("read capability admitted control", err)
	}
	if _, _, err := control.SetMode(context.Background(), authorityTestSetMode()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := control.Revoke(context.Background(), &a.Revoke{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(8), Reason: a.RevocationReason_REVOCATION_REASON_MANUAL.Enum()}); err != nil {
		t.Fatal(err)
	}
	if calls != 3 {
		t.Fatalf("unexpected calls %d", calls)
	}
}
func TestAuthorityInvalidRequestsNeverCall(t *testing.T) {
	_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request called native")
		return nil, nil
	})
	for name, edit := range map[string]func(*a.SetMode){
		"identity": func(v *a.SetMode) { v.Identity.MapId = nil }, "generation": func(v *a.SetMode) { v.ExpectedGeneration = proto.Uint64(0) }, "overflow": func(v *a.SetMode) { v.ExpectedGeneration = proto.Uint64(^uint64(0)) },
		"mode": func(v *a.SetMode) { v.Mode = nil }, "NUL": func(v *a.SetMode) { v.Identity.LoadToken = proto.String("a\x00b") },
		"unknown": func(v *a.SetMode) { v.Identity.ProtoReflect().SetUnknown([]byte{0x18, 0x01}) },
	} {
		t.Run(name, func(t *testing.T) {
			v := authorityTestSetMode()
			edit(v)
			if _, _, err := control.SetMode(context.Background(), v); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if _, _, err := control.Revoke(context.Background(), &a.Revoke{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(7), Reason: a.RevocationReason_REVOCATION_REASON_UNSPECIFIED.Enum()}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	if _, err := NewAuthorityControl(nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
func TestAuthorityBadAcknowledgementRemainsUncertain(t *testing.T) {
	for name, edit := range map[string]func(*a.ControlReply){
		"missing": func(v *a.ControlReply) { v.Outcome = nil }, "old generation": func(v *a.ControlReply) { v.GetGranted().Context.NativeGeneration = proto.Uint64(7) }, "incidental generation": func(v *a.ControlReply) { v.GetGranted().Context.NativeGeneration = proto.Uint64(9) },
		"load": func(v *a.ControlReply) { v.GetGranted().Context.Identity.LoadToken = proto.String("replacement") }, "mode": func(v *a.ControlReply) { v.GetGranted().Authority.Mode = a.Mode_MODE_MANUAL.Enum() }, "missing authority": func(v *a.ControlReply) { v.GetGranted().Authority = nil },
	} {
		t.Run(name, func(t *testing.T) {
			calls := 0
			_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				reply := authorityTestGranted(8)
				edit(reply)
				return pbResult(reply), nil
			})
			reply, raw, err := control.SetMode(context.Background(), authorityTestSetMode())
			var uncertain *AuthorityUncertain
			if reply != nil || !errors.As(err, &uncertain) || !errors.Is(err, ErrContract) || len(raw.Envelope) == 0 || calls != 1 {
				t.Fatalf("%v %v calls=%d", reply, err, calls)
			}
		})
	}
}
func TestAuthorityFailureAndLostResponse(t *testing.T) {
	for _, lost := range []bool{false, true} {
		t.Run(map[bool]string{false: "typed refusal", true: "lost response"}[lost], func(t *testing.T) {
			calls := 0
			_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				if lost {
					return nil, errors.New("lost after possible admission")
				}
				reply := pbResult(&a.ControlReply{Outcome: &a.ControlReply_Failure{Failure: &c.Failure{Code: c.FailureCode_FAILURE_CODE_STALE_GENERATION.Enum()}}})
				reply.IsError = true
				return reply, nil
			})
			reply, _, err := control.SetMode(context.Background(), authorityTestSetMode())
			var uncertain *AuthorityUncertain
			if lost {
				if !errors.As(err, &uncertain) || reply != nil {
					t.Fatal(err)
				}
			} else {
				var refused *NativeFailure
				if !errors.As(err, &refused) || errors.As(err, &uncertain) || reply == nil {
					t.Fatal(err)
				}
			}
			if calls != 1 {
				t.Fatalf("retried %d", calls)
			}
		})
	}
}
func TestAuthorityReadRejectsStaleAndIncompleteStatus(t *testing.T) {
	for name, edit := range map[string]func(*a.Status){
		"stale identity":     func(v *a.Status) { v.Context.Identity.LoadToken = proto.String("other") },
		"missing generation": func(v *a.Status) { v.Context.NativeGeneration = nil },
		"missing mode":       func(v *a.Status) { v.GetActive().Mode = nil },
		"missing state":      func(v *a.Status) { v.State = nil },
	} {
		t.Run(name, func(t *testing.T) {
			client, _ := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				status := &a.Status{Context: authorityTestContext(7), State: &a.Status_Active{Active: &a.ActiveAuthority{Mode: a.Mode_MODE_AUTO.Enum()}}}
				edit(status)
				return pbResult(&a.StatusReply{Outcome: &a.StatusReply_Status{Status: status}}), nil
			})
			reply, _, err := client.ReadAuthority(context.Background(), pbIdentity())
			if reply != nil || !errors.Is(err, ErrContract) {
				t.Fatal(reply, err)
			}
		})
	}
}
func TestAuthorityTransportCancellationDoesNotRetry(t *testing.T) {
	started := make(chan struct{}, 1)
	_, control := authorityTestControl(t, func(ctx context.Context, _ nativeArgument) (*mcp.CallToolResult, error) {
		started <- struct{}{}
		<-ctx.Done()
		return nil, ctx.Err()
	})
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { _, _, err := control.SetMode(ctx, authorityTestSetMode()); done <- err }()
	select {
	case <-started:
		cancel()
	case <-time.After(time.Second):
		t.Fatal("native call never started")
	}
	select {
	case err := <-done:
		var uncertain *AuthorityUncertain
		if !errors.As(err, &uncertain) || !errors.Is(err, context.Canceled) {
			t.Fatal(err)
		}
	case <-time.After(time.Second):
		t.Fatal("cancellation did not finish")
	}
	select {
	case <-started:
		t.Fatal("control retried")
	default:
	}
}
func TestAuthorityRevocationPreconditions(t *testing.T) {
	_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid control reached native")
		return nil, nil
	})
	if _, _, err := control.Revoke(context.Background(), &a.Revoke{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(^uint64(0)), Reason: a.RevocationReason_REVOCATION_REASON_MANUAL.Enum()}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
