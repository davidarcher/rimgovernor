package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// overlayResendEvery is how often an unchanged safety or stock layer is
// resent (one game hour).
const overlayResendEvery domain.Tick = domain.TicksPerHour

// overlayState is the last key and tick a safety or stock layer was sent,
// and whether it was cleared.
type overlayState struct {
	key     string
	drawn   domain.Tick
	cleared bool
}

// drawSafetyOverlay pushes the safety layer after a review, gated by
// the layout overlay flag like the other layers: the holding threats'
// reach, the raid edge and the vetoed loot, sent when they change or an
// hour passed, and cleared once when nothing holds. Output only: a failure
// is logged, never fatal.
func (r *Rounder) drawSafetyOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, emergency policy.EmergencyFacts) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	var layer policy.LayoutOverlay
	if r.layoutOverlay {
		loot, _ := projection.Facts.EventLoot.Value()
		layer = policy.SafetyOverlay(emergency.Threats, loot, projection.Bounds)
	}
	on := len(layer.Layers) > 0
	tick := projection.Identity.Tick
	if !on {
		if !r.safety.cleared {
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.SafetyLayer, policy.LayoutOverlay{}, false); err != nil {
				return
			}
			r.safety = overlayState{cleared: true}
		}
		return
	}
	key := fmt.Sprint(layer)
	if key == r.safety.key && tick >= r.safety.drawn && tick-r.safety.drawn < overlayResendEvery {
		return
	}
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.SafetyLayer, layer, true); err != nil {
		return
	}
	r.safety = overlayState{key: key, drawn: tick}
}
