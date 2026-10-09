package bridge

import (
	"context"
	"fmt"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/bridge/cellgrid"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// The whole-map cell grid. Every frame carries the map as a
// mirror.CellGrid: a keyframe (every array) or a delta against the last
// keyframe, cumulative rather than chained, so a reader that skips frames
// holds only the keyframe and applies each delta it reads to it. A delta
// whose keyframe_seq is not the held keyframe's follows a keyframe this
// reader skipped: the frame serves no grid and the stream asks for a
// keyframe. Grid glow is artificial light only; the frame's sky_glow is
// added back where a planner reads total light (cellgrid.Grid.Window).

// gridFrameMethod keys a frame's grid in its table, frames-only like
// roundsFrameMethod; the reply is the frame's context.
const gridFrameMethod = "rimgovernor/snapshot_frame_grid"

// gridHold is the keyframe deltas apply to, for one world and native
// generation.
type gridHold struct {
	world      *c.Identity
	generation uint64
	seq        uint64
	keyframe   *cellgrid.Grid
}

// frameGrid is the grid of the newest decoded frame that carried one.
type frameGrid struct {
	context *c.ObservationContext
	grid    *cellgrid.Grid
	sky     float64
}

// apply decodes v's grid against the hold: the map as of v, or nil when v
// carries none or a delta against a keyframe the hold lacks (gap). kind
// names what v carried, for the frame's telemetry.
func (h *gridHold) apply(v *o.BundleSnapshot) (grid *cellgrid.Grid, kind string, gap bool, err error) {
	ctx := v.GetContext()
	if h.keyframe == nil || !sameIdentity(h.world, ctx.GetIdentity()) || h.generation != ctx.GetNativeGeneration() {
		h.world, h.generation, h.seq, h.keyframe = ctx.GetIdentity(), ctx.GetNativeGeneration(), 0, nil
	}
	if v.Grid == nil {
		return nil, "", false, nil
	}
	if sky := v.GetSkyGlow(); v.SkyGlow == nil || math.IsNaN(sky) || sky < 0 || sky > 1 {
		return nil, "invalid", true, contract("frame grid sky glow")
	}
	if cellgrid.Complete(v.Grid) {
		grid, err := cellgrid.Apply(nil, true, v.Grid)
		if err != nil {
			h.keyframe = nil
			return nil, "invalid", true, err
		}
		h.seq, h.keyframe = v.GetKeyframeSeq(), grid
		return grid, "keyframe", false, nil
	}
	if h.keyframe == nil || h.seq != v.GetKeyframeSeq() {
		return nil, "gap", true, nil
	}
	grid, err = cellgrid.Apply(h.keyframe, false, v.Grid)
	if err != nil {
		return nil, "invalid", true, err
	}
	return grid, "delta", false, nil
}

// frameWindow serves the planning window over rect from the held grid of
// the newest frame past this client's last write.
func (client *Client) frameWindow(ctx context.Context, identity *c.Identity, rect policy.Rectangle) (PlanningWindow, error) {
	if _, err := client.frameReadKey(ctx, gridFrameMethod, readCacheKey{method: gridFrameMethod}, identity, false, &c.ObservationContext{}); err != nil {
		return PlanningWindow{}, err
	}
	s := client.frames
	s.mu.Lock()
	held := s.grid
	s.mu.Unlock()
	if held.grid == nil || !sameIdentity(held.context.GetIdentity(), identity) {
		return PlanningWindow{}, fmt.Errorf("%w: no snapshot frame grid for this world", ErrUnavailable)
	}
	cells, fogged := held.grid.Window(rect, held.sky)
	return PlanningWindow{Context: held.context, Region: rect, Cells: cells, Filtered: fogged}, nil
}
