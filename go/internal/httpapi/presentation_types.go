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
// Only DemandRendering and CapturePawn are in scope; LeaseVideo/ReadFrame/
// AcknowledgeFrame/CaptureScreenshot remain unimplemented follow-up slices.
type PresentationMediaWriter interface {
	DemandRendering(context.Context, *p.RenderDemand) (*p.RenderReply, bridge.Result, error)
	CapturePawn(context.Context, *p.PawnImageRequest) (*p.PawnImageReply, bridge.Result, error)
}

// NotificationReader preserves unavailable sections without granting acknowledgement.
type NotificationReader interface {
	ReadNotifications(context.Context, *p.NotificationsRequest) (*p.NotificationsReply, bridge.Result, error)
}
