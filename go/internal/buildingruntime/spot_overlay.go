package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// drawSpotOverlay pushes the work-spot layer after a review, gated by the
// layout overlay flag like the others: the butcher spot, butcher table and
// crafting spot, which the layout plan does not hold, drawn from the
// construction census. Output only: a failure is dropped and the next
// review draws again.
func (r *Rounder) drawSpotOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	var layer policy.LayoutOverlay
	if census, known := projection.Facts.CurrentConstruction.Value(); r.layoutOverlay && known {
		layer = policy.SpotOverlay(census, projection.Bounds)
	}
	tick := projection.Identity.Tick
	if len(layer.Layers) == 0 {
		if !r.spots.cleared {
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.SpotLayer, policy.LayoutOverlay{}, false); err != nil {
				return
			}
			r.spots = overlayState{cleared: true}
		}
		return
	}
	key := fmt.Sprint(layer)
	if key == r.spots.key && tick >= r.spots.drawn && tick-r.spots.drawn < overlayResendEvery {
		return
	}
	if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), policy.SpotLayer, layer, true); err != nil {
		return
	}
	r.spots = overlayState{key: key, drawn: tick}
}
