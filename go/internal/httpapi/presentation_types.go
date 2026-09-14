package httpapi

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

// PresentationReader validates fixed native replies and owns no input capability.
// Its lifetime belongs to the service; each read must honor request cancellation.
type PresentationReader interface {
	ReadCamera(context.Context, *p.ReadRequest) (*p.CameraReply, bridge.Result, error)
	ReadSelection(context.Context, *p.ReadRequest) (*p.SelectionReply, bridge.Result, error)
	ReadColonistRoster(context.Context, *p.ColonistRosterRequest) (*p.ColonistRosterReply, bridge.Result, error)
	ReadRenderState(context.Context, *p.ReadRequest) (*p.RenderReply, bridge.Result, error)
}

// PresentationMediaWriter is an active native capture, gated behind the player
// token like other mutations, unlike the free PresentationReader facts above.
// DemandRendering, CapturePawn and the video streaming trio (LeaseVideo/
// ReadFrame/AcknowledgeFrame) are in scope; CaptureScreenshot remains an
// unimplemented follow-up slice.
type PresentationMediaWriter interface {
	DemandRendering(context.Context, *p.RenderDemand) (*p.RenderReply, bridge.Result, error)
	CapturePawn(context.Context, *p.PawnImageRequest) (*p.PawnImageReply, bridge.Result, error)
	LeaseVideo(context.Context, *p.VideoLeaseRequest) (*p.VideoReply, bridge.Result, error)
	ReadFrame(context.Context, *p.FrameRequest) (*p.FrameReply, bridge.Result, error)
	AcknowledgeFrame(context.Context, *p.FrameAcknowledgement) (*p.FrameAcknowledgementReply, bridge.Result, error)
}

// NotificationReader preserves unavailable sections without granting acknowledgement.
type NotificationReader interface {
	ReadNotifications(context.Context, *p.NotificationsRequest) (*p.NotificationsReply, bridge.Result, error)
}
