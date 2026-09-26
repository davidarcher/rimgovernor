package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// PlanningWindowViewMaxCells is the largest region a bundle's planning
// window view serves; a larger window is read as the legacy band.
const PlanningWindowViewMaxCells = 4096

// PlanningWindowView is a decoded planning_window_view bundle section
// (#650): the published native view of the planning window, whose chunks
// carry their own content revision, capture tick and validation tick
// rather than the bundle's. Contract:
// docs/developers/contracts/planning-window-view.md.
type PlanningWindowView struct {
	// Context is the serving hop's: its tick is the publication tick.
	Context       *c.ObservationContext
	Region        policy.Rectangle
	Incarnation   uint64
	Revision      uint64
	PublishedTick int64
	Chunks        []PlanningViewChunk
	// Cells and Filtered are every chunk's rows decoded as the planning
	// window's (PlanningCells), row-major.
	Cells    []policy.SiteCell
	Filtered uint64
}

// PlanningViewChunk is one chunk's provenance: rows MinZ..MaxZ, the
// publication that captured them, when, and when the native change grid
// last confirmed them unchanged.
type PlanningViewChunk struct {
	MinZ, MaxZ int32
	Revision   uint64
	Captured   int64
	Validated  int64
}

// Validated is the oldest chunk validation tick: the view's actual age.
func (v PlanningWindowView) Validated() int64 {
	oldest := v.PublishedTick
	for _, chunk := range v.Chunks {
		oldest = min(oldest, chunk.Validated)
	}
	return oldest
}

// Reused counts the chunks the native served without reading them again
// (captured before the publication that served them).
func (v PlanningWindowView) Reused() int {
	n := 0
	for _, chunk := range v.Chunks {
		if chunk.Revision != v.Revision {
			n++
		}
	}
	return n
}

// BundlePlanningWindowViewRequest is the opt-in view section for region, or
// nil when the region cannot ride as a view.
func BundlePlanningWindowViewRequest(region policy.Rectangle) *o.BundlePlanningWindowViewRequest {
	if region.X < 0 || region.Z < 0 || region.Width < 1 || region.Height < 1 || int64(region.Width)*int64(region.Height) > PlanningWindowViewMaxCells {
		return nil
	}
	return &o.BundlePlanningWindowViewRequest{Region: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(region.X), Z: proto.Int32(region.Z)}, Maximum: &c.Cell{X: proto.Int32(region.X + region.Width - 1), Z: proto.Int32(region.Z + region.Height - 1)}}}
}

// validateBundleView is the bundle's structural check of the view
// section: present only when requested, and served under the bundle's
// identity and tick. Its content is judged by DecodePlanningWindowView,
// whose refusal leaves the section unused rather than failing the bundle.
func validateBundleView(request *o.BundleRequest, v *o.BundleSnapshot) error {
	view := v.PlanningWindowView
	if view == nil {
		return nil
	}
	if request.PlanningWindowView == nil {
		return contract("bundle carries an unrequested planning window view")
	}
	if err := ValidateContext(view.Context); err != nil {
		return err
	}
	if !proto.Equal(view.Context, v.Context) {
		return contract("planning window view context mismatch")
	}
	return nil
}

// DecodePlanningWindowView checks a bundle's view against the request that
// asked for it and decodes it: the requested region and planning mask, a
// complete root whose chunks tile the region's rows in order, every chunk
// captured no later than validated and validated no later than published,
// each chunk's revision within the root's, and every cell decoded under the
// planning window's row rules. Any failure is a contract error; the caller
// then serves the window another way.
func DecodePlanningWindowView(v *o.PlanningWindowView, request *o.BundlePlanningWindowViewRequest) (PlanningWindowView, error) {
	if v == nil || request == nil {
		return PlanningWindowView{}, contract("planning window view missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return PlanningWindowView{}, err
	}
	if !colonySize(v.MapSize) || !proto.Equal(v.Region, request.Region) || !colonyCell(v.Region.GetMinimum(), v.MapSize) || !colonyCell(v.Region.GetMaximum(), v.MapSize) {
		return PlanningWindowView{}, contract("planning window view region differs")
	}
	if !proto.Equal(v.AppliedFields, planningWindowFields()) {
		return PlanningWindowView{}, contract("planning window view mask differs")
	}
	if v.Incarnation == nil || v.GetIncarnation() == 0 || v.Revision == nil || v.GetRevision() == 0 || v.PublishedTick == nil || v.GetPublishedTick() != v.Context.GetTick() {
		return PlanningWindowView{}, contract("invalid planning window view publication")
	}
	if !v.GetComplete() {
		return PlanningWindowView{}, contract("incomplete planning window view")
	}
	minX, minZ, maxX, maxZ := v.Region.Minimum.GetX(), v.Region.Minimum.GetZ(), v.Region.Maximum.GetX(), v.Region.Maximum.GetZ()
	out := PlanningWindowView{Context: v.Context, Region: policy.Rectangle{X: minX, Z: minZ, Width: maxX - minX + 1, Height: maxZ - minZ + 1}, Incarnation: v.GetIncarnation(), Revision: v.GetRevision(), PublishedTick: v.GetPublishedTick()}
	if int64(out.Region.Width)*int64(out.Region.Height) > PlanningWindowViewMaxCells {
		return PlanningWindowView{}, contract("planning window view exceeds bound")
	}
	next := minZ
	for _, chunk := range v.Chunks {
		if chunk == nil || chunk.MinZ == nil || chunk.MaxZ == nil || chunk.GetMinZ() != next || chunk.GetMaxZ() < chunk.GetMinZ() || chunk.GetMaxZ() > maxZ {
			return PlanningWindowView{}, contract("planning window view chunks do not tile the region")
		}
		if chunk.Revision == nil || chunk.GetRevision() == 0 || chunk.GetRevision() > out.Revision || chunk.CapturedTick == nil || chunk.ValidatedTick == nil || chunk.GetCapturedTick() < 0 || chunk.GetCapturedTick() > chunk.GetValidatedTick() || chunk.GetValidatedTick() > out.PublishedTick {
			return PlanningWindowView{}, contract("invalid planning window view chunk provenance")
		}
		band := &o.CellsSnapshot{Context: v.Context, MapSize: v.MapSize, Region: &o.Rectangle{Minimum: &c.Cell{X: proto.Int32(minX), Z: proto.Int32(chunk.GetMinZ())}, Maximum: &c.Cell{X: proto.Int32(maxX), Z: proto.Int32(chunk.GetMaxZ())}}, AppliedFields: planningWindowFields(), Compact: chunk.Cells}
		if band.Compact == nil {
			return PlanningWindowView{}, contract("planning window view chunk has no cells")
		}
		if err := ExpandCompactCells(band); err != nil {
			return PlanningWindowView{}, err
		}
		n := uint64(len(band.Cells))
		band.Completeness = &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
		if err := validatePlanningCells(band, v.Context, v.MapSize, 0); err != nil {
			return PlanningWindowView{}, err
		}
		cells, filtered := PlanningCells(band)
		out.Cells = append(out.Cells, cells...)
		out.Filtered += filtered
		out.Chunks = append(out.Chunks, PlanningViewChunk{MinZ: chunk.GetMinZ(), MaxZ: chunk.GetMaxZ(), Revision: chunk.GetRevision(), Captured: chunk.GetCapturedTick(), Validated: chunk.GetValidatedTick()})
		next = chunk.GetMaxZ() + 1
	}
	if next != maxZ+1 {
		return PlanningWindowView{}, contract("planning window view chunks do not tile the region")
	}
	return out, nil
}
