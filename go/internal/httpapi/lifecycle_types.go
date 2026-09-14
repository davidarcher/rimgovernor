package httpapi

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// LifecycleWriter wraps the explicit session lifecycle capability: one trusted
// checkpoint save and one native load, plus recovery reads for a request the
// caller never received a reply for. It is a direct bridge mutation like
// PresentationMediaWriter, not a store-backed player submission: Save/Load
// never commit a plan and grant no authority of their own.
type LifecycleWriter interface {
	Save(context.Context, *l.SaveRequest) (*l.SaveReply, bridge.Result, error)
	ReadSave(context.Context, string) (*l.SaveReply, bridge.Result, error)
	Load(context.Context, *l.LoadRequest) (*l.LoadReply, bridge.Result, error)
	ReadLoad(context.Context, string) (*l.LoadReply, bridge.Result, error)
}
