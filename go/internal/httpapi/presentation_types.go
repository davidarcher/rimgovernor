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
}
