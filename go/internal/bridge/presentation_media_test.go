package bridge

import (
	"context"
	"errors"
	"testing"
	"time"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"google.golang.org/protobuf/proto"
)

func pbPlayerIdentity() *p.PlayerIdentity { return &p.PlayerIdentity{Identity: pbIdentity()} }

// minimalPNG is only long enough to satisfy the PNG-signature sanity check;
// it is never decoded as an image.
var minimalPNG = []byte{0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A}

func TestReadRenderState(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: pbContext(), Supported: proto.Bool(true), Suspended: proto.Bool(false), WindowVisible: proto.Bool(true), RemainingLeaseMs: proto.Uint32(2500)}}}), nil
	}}
	client := testClient(t, server, time.Second)
	reply, _, err := client.ReadRenderState(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
	if err != nil || !reply.GetStatus().GetSupported() || reply.GetStatus().GetRemainingLeaseMs() != 2500 {
		t.Fatal(reply, err)
	}
	if _, _, err = client.ReadRenderState(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
}
func TestReadRenderStateUnavailable(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: pbContext(), Supported: proto.Bool(false), Suspended: proto.Bool(true),
			Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_LOADED.Enum(), Detail: proto.String("Startup headless mode cannot render")}}}}), nil
	}}
	client := testClient(t, server, time.Second)
	reply, _, err := client.ReadRenderState(context.Background(), &p.ReadRequest{Identity: pbIdentity()})
	if err != nil || reply.GetStatus().GetSupported() || reply.GetStatus().GetUnavailable().GetDetail() == "" {
		t.Fatal(reply, err)
	}
}
func TestDemandRenderingBounds(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
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
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.RenderReply{Outcome: &p.RenderReply_Status{Status: &p.RenderStatus{Context: pbContext(), Supported: proto.Bool(true), Suspended: proto.Bool(false), WindowVisible: proto.Bool(true), RemainingLeaseMs: proto.Uint32(5000)}}}), nil
	}}
	client := testClient(t, server, time.Second)
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
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		reply := pbResult(&p.RenderReply{Outcome: &p.RenderReply_Failure{Failure: failureValue}})
		reply.IsError = true
		return reply, nil
	}}
	client := testClient(t, server, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	_, _, err = media.DemandRendering(context.Background(), &p.RenderDemand{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(5)})
	if !errors.Is(err, ErrRefused) {
		t.Fatal(err)
	}
}
func TestCapturePawnValidation(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native call must not happen for an invalid request")
		return nil, nil
	}}, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.CapturePawn(context.Background(), &p.PawnImageRequest{Identity: pbIdentity(), View: p.PawnView_PAWN_VIEW_PORTRAIT.Enum()}); err == nil {
		t.Fatal("missing pawn id accepted")
	}
	if _, _, err = media.CapturePawn(context.Background(), &p.PawnImageRequest{Identity: pbIdentity(), PawnId: proto.String("p1")}); err == nil {
		t.Fatal("missing view accepted")
	}
	if _, _, err = media.CapturePawn(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
}
func TestCapturePawnSuccess(t *testing.T) {
	for _, tc := range []struct {
		view   p.PawnView
		method p.CaptureMethod
		width  uint32
		height uint32
	}{
		{p.PawnView_PAWN_VIEW_PORTRAIT, p.CaptureMethod_CAPTURE_METHOD_PORTRAIT, 192, 192},
		{p.PawnView_PAWN_VIEW_FOLLOW, p.CaptureMethod_CAPTURE_METHOD_OFFSCREEN_FOLLOW, 640, 400},
	} {
		server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
			return pbResult(&p.PawnImageReply{Outcome: &p.PawnImageReply_Image{Image: &p.PawnImage{PawnId: proto.String("p1"), View: tc.view.Enum(),
				Frame: &p.MediaFrame{Width: proto.Uint32(tc.width), Height: proto.Uint32(tc.height), Encoding: p.MediaEncoding_MEDIA_ENCODING_PNG.Enum(),
					CaptureMethod: tc.method.Enum(), CapturedUnixMs: proto.Int64(1700000000000), ReadbackMs: proto.Float64(12.5), Data: minimalPNG}}}}), nil
		}}
		client := testClient(t, server, time.Second)
		media, err := NewPresentationMedia(client)
		if err != nil {
			t.Fatal(err)
		}
		reply, _, err := media.CapturePawn(context.Background(), &p.PawnImageRequest{Identity: pbIdentity(), PawnId: proto.String("p1"), View: tc.view.Enum()})
		if err != nil || reply.GetImage().GetFrame().GetWidth() != tc.width {
			t.Fatal(tc, reply, err)
		}
	}
}
func TestCapturePawnFailureAndMismatch(t *testing.T) {
	failureValue := &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum(), Detail: proto.String("Colonist is no longer visible on the current map")}
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		reply := pbResult(&p.PawnImageReply{Outcome: &p.PawnImageReply_Failure{Failure: failureValue}})
		reply.IsError = true
		return reply, nil
	}}
	client := testClient(t, server, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	_, _, err = media.CapturePawn(context.Background(), &p.PawnImageRequest{Identity: pbIdentity(), PawnId: proto.String("unknown"), View: p.PawnView_PAWN_VIEW_PORTRAIT.Enum()})
	if !errors.Is(err, ErrRefused) {
		t.Fatal("unknown pawn id must be a typed refusal, not a crash", err)
	}

	// Native returning the wrong capture_method for the requested view is a
	// contract violation, not a value CapturePawn should pass through.
	wrongMethod := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.PawnImageReply{Outcome: &p.PawnImageReply_Image{Image: &p.PawnImage{PawnId: proto.String("p1"), View: p.PawnView_PAWN_VIEW_PORTRAIT.Enum(),
			Frame: &p.MediaFrame{Width: proto.Uint32(192), Height: proto.Uint32(192), Encoding: p.MediaEncoding_MEDIA_ENCODING_PNG.Enum(),
				CaptureMethod: p.CaptureMethod_CAPTURE_METHOD_OFFSCREEN_FOLLOW.Enum(), CapturedUnixMs: proto.Int64(1), ReadbackMs: proto.Float64(1), Data: minimalPNG}}}}), nil
	}}
	client2 := testClient(t, wrongMethod, time.Second)
	media2, err := NewPresentationMedia(client2)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media2.CapturePawn(context.Background(), &p.PawnImageRequest{Identity: pbIdentity(), PawnId: proto.String("p1"), View: p.PawnView_PAWN_VIEW_PORTRAIT.Enum()}); err == nil {
		t.Fatal("mismatched capture method accepted")
	}
}
