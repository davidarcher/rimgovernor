package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// drawSafetyOverlay pushes the safety layer (#824) after a review, gated by
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
				clockSchedulerLog("safety overlay not cleared: %v", err)
				return
			}
			r.safety = statusStripState{cleared: true}
		}
		return
	}
	key := fmt.Sprint(layer)
	if key == r.safety.key && tick >= r.safety.drawn && tick-r.safety.drawn < statusRedrawEvery {
		return
	}
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.SafetyLayer, layer, true); err != nil {
		clockSchedulerLog("safety overlay not drawn: %v", err)
		return
	}
	r.safety = statusStripState{key: key, drawn: tick}
}
