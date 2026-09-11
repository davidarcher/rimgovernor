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

func authorityTestOwner() *a.Owner {
	return &a.Owner{ControllerSessionId: proto.String("controller"), PlayerDirection: proto.Uint64(3)}
}
func authorityTestContext(generation uint64) *c.ObservationContext {
	return &c.ObservationContext{Identity: pbIdentity(), Tick: proto.Int64(12), NativeGeneration: proto.Uint64(generation)}
}
func authorityTestAcquire() *a.Acquire {
	return &a.Acquire{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(7), Owner: authorityTestOwner(), LeaseMs: proto.Uint32(1000)}
}
func authorityTestGranted(generation uint64) *a.ControlReply {
	return &a.ControlReply{Outcome: &a.ControlReply_Granted{Granted: &a.Granted{Context: authorityTestContext(generation), Authority: &a.ActiveAuthority{Owner: authorityTestOwner(), RemainingLeaseMs: proto.Uint32(999)}, LeaseId: proto.String("lease")}}}
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
		case *a.ControlRequest_Acquire:
			if !proto.Equal(operation.Acquire, authorityTestAcquire()) {
				t.Fatal("changed acquire")
			}
			return pbResult(authorityTestGranted(8)), nil
		case *a.ControlRequest_Renew:
			if operation.Renew.GetLeaseId() != "lease" || operation.Renew.GetExpectedGeneration() != 8 {
				t.Fatal("changed renew")
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
	if _, _, err := control.Acquire(context.Background(), authorityTestAcquire()); err != nil {
		t.Fatal(err)
	}
	renew := &a.Renew{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(8), ControllerSessionId: proto.String("controller"), LeaseId: proto.String("lease"), LeaseMs: proto.Uint32(1000)}
	if _, _, err := control.Renew(context.Background(), renew, authorityTestOwner()); err != nil {
		t.Fatal(err)
	}
	if _, _, err := control.Revoke(context.Background(), &a.Revoke{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(8), Reason: a.RevocationReason_REVOCATION_REASON_MANUAL.Enum()}); err != nil {
		t.Fatal(err)
	}
	if calls != 4 {
		t.Fatalf("unexpected calls %d", calls)
	}
}
func TestAuthorityInvalidRequestsNeverCall(t *testing.T) {
	_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid request called native")
		return nil, nil
	})
	for name, edit := range map[string]func(*a.Acquire){
		"identity": func(v *a.Acquire) { v.Identity.MapId = nil }, "generation": func(v *a.Acquire) { v.ExpectedGeneration = proto.Uint64(0) }, "overflow": func(v *a.Acquire) { v.ExpectedGeneration = proto.Uint64(^uint64(0)) },
		"owner": func(v *a.Acquire) { v.Owner.PlayerDirection = nil }, "NUL": func(v *a.Acquire) { v.Owner.ControllerSessionId = proto.String("a\x00b") }, "short": func(v *a.Acquire) { v.LeaseMs = proto.Uint32(999) }, "long": func(v *a.Acquire) { v.LeaseMs = proto.Uint32(30001) },
		"unknown": func(v *a.Acquire) { v.Owner.ProtoReflect().SetUnknown([]byte{0x18, 0x01}) },
	} {
		t.Run(name, func(t *testing.T) {
			v := authorityTestAcquire()
			edit(v)
			if _, _, err := control.Acquire(context.Background(), v); !errors.Is(err, ErrContract) {
				t.Fatal(err)
			}
		})
	}
	if _, _, err := control.Revoke(context.Background(), &a.Revoke{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(7), Reason: a.RevocationReason_REVOCATION_REASON_LEASE_EXPIRED.Enum()}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	if _, err := NewAuthorityControl(nil); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
func TestAuthorityBadAcknowledgementRemainsUncertain(t *testing.T) {
	for name, edit := range map[string]func(*a.ControlReply){
		"missing": func(v *a.ControlReply) { v.Outcome = nil }, "old generation": func(v *a.ControlReply) { v.GetGranted().Context.NativeGeneration = proto.Uint64(7) }, "incidental generation": func(v *a.ControlReply) { v.GetGranted().Context.NativeGeneration = proto.Uint64(9) },
		"load": func(v *a.ControlReply) { v.GetGranted().Context.Identity.LoadToken = proto.String("replacement") }, "owner": func(v *a.ControlReply) { v.GetGranted().Authority.Owner.PlayerDirection = proto.Uint64(4) }, "expired": func(v *a.ControlReply) { v.GetGranted().Authority.RemainingLeaseMs = proto.Uint32(0) }, "duration": func(v *a.ControlReply) { v.GetGranted().Authority.RemainingLeaseMs = proto.Uint32(1001) }, "lease": func(v *a.ControlReply) { v.GetGranted().LeaseId = nil },
	} {
		t.Run(name, func(t *testing.T) {
			calls := 0
			_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				calls++
				reply := authorityTestGranted(8)
				edit(reply)
				return pbResult(reply), nil
			})
			reply, raw, err := control.Acquire(context.Background(), authorityTestAcquire())
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
			reply, _, err := control.Acquire(context.Background(), authorityTestAcquire())
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
func TestAuthorityRenewRetainsOriginalDirection(t *testing.T) {
	_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		reply := authorityTestGranted(8)
		reply.GetGranted().Authority.Owner.PlayerDirection = proto.Uint64(4)
		return pbResult(reply), nil
	})
	request := &a.Renew{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(8), ControllerSessionId: proto.String("controller"), LeaseId: proto.String("lease"), LeaseMs: proto.Uint32(1000)}
	_, _, err := control.Renew(context.Background(), request, authorityTestOwner())
	var uncertain *AuthorityUncertain
	if !errors.As(err, &uncertain) {
		t.Fatal(err)
	}
}

func TestAuthorityReadRejectsStaleAndIncompleteStatus(t *testing.T) {
	for name, edit := range map[string]func(*a.Status){
		"stale identity":     func(v *a.Status) { v.Context.Identity.LoadToken = proto.String("other") },
		"missing generation": func(v *a.Status) { v.Context.NativeGeneration = nil },
		"missing owner":      func(v *a.Status) { v.GetActive().Owner = nil },
		"missing state":      func(v *a.Status) { v.State = nil },
	} {
		t.Run(name, func(t *testing.T) {
			client, _ := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
				status := &a.Status{Context: authorityTestContext(7), State: &a.Status_Active{Active: &a.ActiveAuthority{Owner: authorityTestOwner(), RemainingLeaseMs: proto.Uint32(1)}}}
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
	go func() { _, _, err := control.Acquire(ctx, authorityTestAcquire()); done <- err }()
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
func TestAuthorityRevocationAndRenewalPreconditions(t *testing.T) {
	_, control := authorityTestControl(t, func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("invalid control reached native")
		return nil, nil
	})
	if _, _, err := control.Revoke(context.Background(), &a.Revoke{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(^uint64(0)), Reason: a.RevocationReason_REVOCATION_REASON_MANUAL.Enum()}); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
	request := &a.Renew{Identity: pbIdentity(), ExpectedGeneration: proto.Uint64(8), ControllerSessionId: proto.String("controller"), LeaseId: proto.String("lease"), LeaseMs: proto.Uint32(1000)}
	owner := authorityTestOwner()
	owner.ControllerSessionId = proto.String("another")
	if _, _, err := control.Renew(context.Background(), request, owner); !errors.Is(err, ErrContract) {
		t.Fatal(err)
	}
}
