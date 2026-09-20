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
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
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
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
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
		client := testClient(t, server, testBudget)
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
	client := testClient(t, server, testBudget)
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
	client2 := testClient(t, wrongMethod, testBudget)
	media2, err := NewPresentationMedia(client2)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media2.CapturePawn(context.Background(), &p.PawnImageRequest{Identity: pbIdentity(), PawnId: proto.String("p1"), View: p.PawnView_PAWN_VIEW_PORTRAIT.Enum()}); err == nil {
		t.Fatal("mismatched capture method accepted")
	}
}

var minimalVideoFrame = []byte{1, 2, 3, 4}

func pbVideoState(active bool) *p.VideoState {
	state := &p.VideoState{Context: pbContext(), Supported: proto.Bool(true), Active: proto.Bool(active)}
	if active {
		state.SourceId = proto.String("Local\\RimGovernorVideo-abc")
		state.RemainingLeaseMs = proto.Uint32(8000)
		state.CapturedFrames = proto.Uint64(3)
		state.FramesPerSecond = proto.Float64(60)
		state.PixelFormat = p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP.Enum()
		state.CaptureMethod = p.CaptureMethod_CAPTURE_METHOD_READ_PIXELS.Enum()
	}
	return state
}
func TestLeaseVideoValidation(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native call must not happen for an invalid request")
		return nil, nil
	}}, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.LeaseVideo(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
	if _, _, err = media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{}); err == nil {
		t.Fatal("missing operation accepted")
	}
	if _, _, err = media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Start{Start: &p.VideoStart{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(16)}}}); err == nil {
		t.Fatal("oversized lease accepted")
	}
	if _, _, err = media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Start{Start: &p.VideoStart{LeaseSeconds: proto.Uint32(5)}}}); err == nil {
		t.Fatal("missing start viewer accepted")
	}
	if _, _, err = media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Stop{Stop: &p.VideoStop{}}}); err == nil {
		t.Fatal("missing stop viewer accepted")
	}
}
func TestLeaseVideoStartSuccess(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.VideoReply{Outcome: &p.VideoReply_State{State: pbVideoState(true)}}), nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Start{Start: &p.VideoStart{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(8)}}})
	if err != nil || !reply.GetState().GetActive() || reply.GetState().GetSourceId() == "" {
		t.Fatal(reply, err)
	}
}
func TestLeaseVideoStopSuccess(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.VideoReply{Outcome: &p.VideoReply_State{State: pbVideoState(false)}}), nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Stop{Stop: &p.VideoStop{Viewer: pbPlayerIdentity()}}})
	if err != nil || reply.GetState().GetActive() {
		t.Fatal(reply, err)
	}
}
func TestLeaseVideoRefused(t *testing.T) {
	failureValue := &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		reply := pbResult(&p.VideoReply{Outcome: &p.VideoReply_Failure{Failure: failureValue}})
		reply.IsError = true
		return reply, nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.LeaseVideo(context.Background(), &p.VideoLeaseRequest{Operation: &p.VideoLeaseRequest_Start{Start: &p.VideoStart{Viewer: pbPlayerIdentity(), LeaseSeconds: proto.Uint32(5)}}}); !errors.Is(err, ErrRefused) {
		t.Fatal(err)
	}
}
func TestReadFrameValidation(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native call must not happen for an invalid request")
		return nil, nil
	}}, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.ReadFrame(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
	if _, _, err = media.ReadFrame(context.Background(), &p.FrameRequest{}); err == nil {
		t.Fatal("missing viewer accepted")
	}
}
func TestReadFrameSuccess(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.FrameReply{Outcome: &p.FrameReply_Frame{Frame: &p.MediaFrame{
			Frame: &p.FrameReference{SourceId: proto.String("Local\\RimGovernorVideo-abc"), Sequence: proto.Uint64(7)},
			Width: proto.Uint32(1920), Height: proto.Uint32(1080),
			Encoding:       p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP.Enum(),
			CaptureMethod:  p.CaptureMethod_CAPTURE_METHOD_READ_PIXELS.Enum(),
			CapturedUnixMs: proto.Int64(1700000000000), ReadbackMs: proto.Float64(3.5),
			Data: minimalVideoFrame,
		}}}), nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := media.ReadFrame(context.Background(), &p.FrameRequest{Viewer: pbPlayerIdentity(), SourceId: proto.String("Local\\RimGovernorVideo-abc")})
	if err != nil || reply.GetFrame().GetFrame().GetSequence() != 7 {
		t.Fatal(reply, err)
	}
}

// A whole-map or full-screen frame is several MiB; it decodes against the
// media envelope, not the 1 MiB general payload cap. The call timeout only
// bounds a hang: an 8 MiB frame crosses the JSON transport in seconds under
// a whole-module -race pass (#343), and its latency is not what this checks.
func TestReadFrameAcceptsAFullSizeFrame(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.FrameReply{Outcome: &p.FrameReply_Frame{Frame: &p.MediaFrame{
			Frame: &p.FrameReference{SourceId: proto.String("Local\\RimGovernorVideo-abc"), Sequence: proto.Uint64(8)},
			Width: proto.Uint32(1920), Height: proto.Uint32(1080),
			Encoding:       p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP.Enum(),
			CaptureMethod:  p.CaptureMethod_CAPTURE_METHOD_ASYNC_GPU.Enum(),
			CapturedUnixMs: proto.Int64(1700000000000), ReadbackMs: proto.Float64(3.5),
			Data: make([]byte, 1920*1080*4),
		}}}), nil
	}}
	client := testClient(t, server, time.Minute)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := media.ReadFrame(context.Background(), &p.FrameRequest{Viewer: pbPlayerIdentity()})
	if err != nil || len(reply.GetFrame().GetData()) != 1920*1080*4 {
		t.Fatal(len(reply.GetFrame().GetData()), err)
	}
}
func TestReadFrameInvalidReference(t *testing.T) {
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.FrameReply{Outcome: &p.FrameReply_Frame{Frame: &p.MediaFrame{
			Width: proto.Uint32(1920), Height: proto.Uint32(1080),
			Encoding:       p.MediaEncoding_MEDIA_ENCODING_RGBA32_BOTTOM_UP.Enum(),
			CaptureMethod:  p.CaptureMethod_CAPTURE_METHOD_READ_PIXELS.Enum(),
			CapturedUnixMs: proto.Int64(1), ReadbackMs: proto.Float64(1), Data: minimalVideoFrame,
		}}}), nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.ReadFrame(context.Background(), &p.FrameRequest{Viewer: pbPlayerIdentity()}); err == nil {
		t.Fatal("missing frame reference accepted")
	}
}
func TestAcknowledgeFrameValidationAndSuccess(t *testing.T) {
	client := testClient(t, &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		t.Fatal("native call must not happen for an invalid request")
		return nil, nil
	}}, time.Second)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.AcknowledgeFrame(context.Background(), nil); err == nil {
		t.Fatal("nil request accepted")
	}
	if _, _, err = media.AcknowledgeFrame(context.Background(), &p.FrameAcknowledgement{Viewer: pbPlayerIdentity()}); err == nil {
		t.Fatal("missing frame accepted")
	}

	ref := &p.FrameReference{SourceId: proto.String("Local\\RimGovernorVideo-abc"), Sequence: proto.Uint64(9)}
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.FrameAcknowledgementReply{Outcome: &p.FrameAcknowledgementReply_Acknowledged{Acknowledged: &p.FrameAcknowledged{Frame: ref}}}), nil
	}}
	client2 := testClient(t, server, testBudget)
	media2, err := NewPresentationMedia(client2)
	if err != nil {
		t.Fatal(err)
	}
	reply, _, err := media2.AcknowledgeFrame(context.Background(), &p.FrameAcknowledgement{Viewer: pbPlayerIdentity(), Frame: ref, DisplayedUnixMs: proto.Int64(1700000000100)})
	if err != nil || reply.GetAcknowledged().GetFrame().GetSequence() != 9 {
		t.Fatal(reply, err)
	}
}
func TestAcknowledgeFrameMismatch(t *testing.T) {
	ref := &p.FrameReference{SourceId: proto.String("s"), Sequence: proto.Uint64(1)}
	other := &p.FrameReference{SourceId: proto.String("s"), Sequence: proto.Uint64(2)}
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(&p.FrameAcknowledgementReply{Outcome: &p.FrameAcknowledgementReply_Acknowledged{Acknowledged: &p.FrameAcknowledged{Frame: other}}}), nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.AcknowledgeFrame(context.Background(), &p.FrameAcknowledgement{Viewer: pbPlayerIdentity(), Frame: ref}); err == nil {
		t.Fatal("mismatched acknowledged frame reference accepted")
	}
}
func TestAcknowledgeFrameRefused(t *testing.T) {
	failureValue := &c.Failure{Code: c.FailureCode_FAILURE_CODE_UNAVAILABLE.Enum()}
	ref := &p.FrameReference{SourceId: proto.String("s"), Sequence: proto.Uint64(1)}
	server := &testServer{schema: protoSchema, handler: func(context.Context, nativeArgument) (*mcp.CallToolResult, error) {
		reply := pbResult(&p.FrameAcknowledgementReply{Outcome: &p.FrameAcknowledgementReply_Refusal{Refusal: failureValue}})
		reply.IsError = true
		return reply, nil
	}}
	client := testClient(t, server, testBudget)
	media, err := NewPresentationMedia(client)
	if err != nil {
		t.Fatal(err)
	}
	if _, _, err = media.AcknowledgeFrame(context.Background(), &p.FrameAcknowledgement{Viewer: pbPlayerIdentity(), Frame: ref}); !errors.Is(err, ErrRefused) {
		t.Fatal(err)
	}
}
