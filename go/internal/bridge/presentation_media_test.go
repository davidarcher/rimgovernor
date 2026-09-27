package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"google.golang.org/protobuf/proto"
)

func pbPlayerIdentity() *p.PlayerIdentity { return &p.PlayerIdentity{Identity: pbIdentity()} }
func TestReadRenderState(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		return pbResult(&p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: pbContext(), Supported: proto.Bool(true), Suspended: proto.Bool(false), WindowVisible: proto.Bool(true), RemainingLeaseMs: proto.Uint32(2500)}}}), nil
	}}
	client := testClient(t, server, testBudget)
	reply, _, err := client.ReadRenderState(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
	if err != nil || !reply.GetStatus().GetSupported() || reply.GetStatus().GetRemainingLeaseMs() != 2500 {
		t.Fatal(reply, err)
	}
	if _, _, err = client.ReadRenderState(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
}
func TestReadRenderStateUnavailable(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		return pbResult(&p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: pbContext(), Supported: proto.Bool(false), Suspended: proto.Bool(true),
			Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum(), Detail: proto.String("Startup headless mode cannot render")}}}}), nil
	}}
	client := testClient(t, server, testBudget)
	reply, _, err := client.ReadRenderState(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
	if err != nil || reply.GetStatus().GetSupported() || reply.GetStatus().GetUnavailable().GetDetail() == "" {
		t.Fatal(reply, err)
	}
}
func TestDemandRenderingBounds(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		t.Fatal("native call must not happen for an invalid request")
		return nil, nil
	}}, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.DemandRendering(context.Background(), &p.RenderDemand{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(31)}); err == nil {
		t.Fatal("oversized lease accepted")
	}
	if _, _, err = media.DemandRendering(context.Background(), &p.RenderDemand{LeaseSeconds: proto.Uint32(5)}); err == nil {
		t.Fatal("missing viewer accepted")
	}
	if _, _, err = media.DemandRendering(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
}
func TestDemandRenderingSuccess(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		return pbResult(&p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: pbContext(), Supported: proto.Bool(true), Suspended: proto.Bool(false), WindowVisible: proto.Bool(true), RemainingLeaseMs: proto.Uint32(5000)}}}), nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := media.DemandRendering(context.Background(), &p.RenderDemand{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(5)})
	if err != nil || reply.GetStatus().GetRemainingLeaseMs() != 5000 {
		t.Fatal(reply, err)
	}
}
func TestDemandRenderingRefused(t *testing.T) {
	failureValue := &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*callResult, error) {
		reply := pbResult(&p.RenderReply{Outcome: &p.RenderReply_Failure{Failure: failureValue}})
		reply.IsError = true
		return reply, nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	_, _, err = media.DemandRendering(context.Background(), &p.RenderDemand{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(5)})
	if !errors.Is(err, ErrRefused) {
		t.Fatal(err)
	}
}
