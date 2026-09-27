package buildingruntime

import (
	"context"
	"fmt"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/presentationpb"
)

// layoutReplanEvery is the fewest ticks between survey reads for a missing
// or outgrown plan (one game day): a colony the grown plan still cannot
// house waits instead of re-reading the whole map every review.
const layoutReplanEvery domain.Tick = 60000

// layoutTerrainCheckEvery is the fallback terrain check: once a quadrum
// (15 days) a review re-reads the survey and replans the layout.
const layoutTerrainCheckEvery domain.Tick = 15 * 60000

// MapSurveyNative is the optional native whole-map read behind the layout
// plan (bridge.Client.ReadMapSurvey). A reviewer whose native lacks it, or
// refuses the foundation field, plans nothing.
type MapSurveyNative interface {
	ReadMapSurvey(context.Context, *c.Identity, policy.Bounds) (policy.MapSurvey, bridge.Result, error)
}

// reviewLayoutPlan serves the saved v2 layout plan (#783) on the
// projection. It derives one at once when none is saved (then at most
// once a day), grows it when the colony outgrows it (at most once a day)
// and re-reads the survey once a quadrum.
func (r *RoutineReviewer) reviewLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	tick := projection.Identity.Tick
	layout, haveLayout, err := r.layoutPlan(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	pawns, known := projection.Facts.Colonists.Value()
	// The last check is the newer of the saved plan's tick and the last
	// survey this process read; a rewind past it or a restart falls back
	// to the plan's tick.
	checked := layout.Tick
	if r.planChecked > checked && r.planChecked <= tick {
		checked = r.planChecked
	}
	outgrown := known && haveLayout && layout.Plan.LayoutOutgrown(int(pawns)) && tick-layout.Tick >= layoutReplanEvery
	missing := !haveLayout && (!r.planSurveyed || tick-checked >= layoutReplanEvery)
	quadrum := haveLayout && tick-checked >= layoutTerrainCheckEvery
	// Every tomb full (#857): grow one more, at most once a day.
	tombs := 1
	tomb := haveLayout && tombsFull(layout.Plan, *projection)
	if tomb {
		tombs = layout.Plan.TombRooms() + 1
	}
	tomb = tomb && (!r.planSurveyed || tick-checked >= layoutReplanEvery)
	if native, ok := r.native.(MapSurveyNative); ok && (outgrown || missing || quadrum || tomb) {
		if survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds); err != nil {
			clockSchedulerLog("layout plan check deferred, map survey unavailable: %v", err)
		} else {
			r.planChecked, r.planSurveyed = tick, true
			if !haveLayout {
				topology, _ := projection.PowerPlanning.Value()
				err = r.deriveLayoutPlan(ctx, snapshot, tick, survey, int(pawns), topology.Geysers)
			} else {
				err = r.replanLayout(ctx, snapshot, tick, layout.Plan, survey, int(pawns), tombs, outgrown)
			}
			if err != nil {
				return err
			}
			if layout, haveLayout, err = r.layoutPlan(ctx, snapshot, tick); err != nil {
				return err
			}
		}
	}
	if layout, haveLayout, err = r.servePlayerRequests(ctx, snapshot, projection, layout, haveLayout); err != nil {
		return err
	}
	if haveLayout {
		projection.LayoutPlan = domain.Known(layout.Plan)
	}
	r.drawLayoutOverlay(ctx, snapshot, projection, layout, haveLayout)
	return nil
}

// overlayRedrawEvery is how often an unchanged plan's overlay is redrawn
// (one game day).
const overlayRedrawEvery domain.Tick = 60000

// overlayLayer is the native overlay layer the layout plan draws on.
const overlayLayer = "layout"

// LayoutOverlayNative draws the layout plan as a native overlay layer
// (#817, bridge.Client.DrawOverlay).
type LayoutOverlayNative interface {
	DrawOverlay(context.Context, *c.Identity, string, policy.LayoutOverlay, bool) (*p.OverlayApplied, bridge.Result, error)
}

// drawLayoutOverlay rewrites the overlay when the plan changed or a day
// passed since the last draw; with the overlay off it deletes the owned
// plans once per process. Output only: a failure is logged, never fatal.
func (r *RoutineReviewer) drawLayoutOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord, haveLayout bool) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	r.drawHeatOverlay(ctx, native, snapshot, projection)
	r.drawProposalOverlay(ctx, native, snapshot, projection, layout)
	tick := projection.Identity.Tick
	if !r.layoutOverlay || !haveLayout {
		if !r.overlayCleared {
			if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, policy.LayoutOverlay{}, false); err != nil {
				clockSchedulerLog("layout overlay not cleared: %v", err)
				return
			}
			r.overlayCleared = true
		}
		return
	}
	key := fmt.Sprint("v2@", layout.Tick)
	if key == r.overlayKey && tick >= r.overlayDrawn && tick-r.overlayDrawn < overlayRedrawEvery {
		return
	}
	applied, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), overlayLayer, layout.Plan.Overlay(projection.Bounds), true)
	if err != nil {
		clockSchedulerLog("layout overlay not drawn: %v", err)
		return
	}
	r.overlayKey, r.overlayDrawn, r.overlayCleared = key, tick, false
	clockSchedulerLog("layout overlay drawn layers=%d cells=%d", applied.GetLayers(), applied.GetCells())
}

// layoutPlan reads the v2 layout plan (#783). A saved plan that no longer
// decodes or validates reads as none, logged once per process, so the next
// survey derives a fresh one.
func (r *RoutineReviewer) layoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick) (store.LayoutPlanRecord, bool, error) {
	record, ok, err := r.player.journal.LayoutPlan(ctx, snapshot, tick)
	if err == nil && record.Invalid && !r.layoutInvalidLogged {
		r.layoutInvalidLogged = true
		clockSchedulerLog("saved layout plan from tick %d is invalid, replanning", record.Tick)
	}
	return record, ok, err
}

// deriveLayoutPlan lays a fresh v2 plan over survey and the reported
// geysers and records it.
func (r *RoutineReviewer) deriveLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, survey policy.MapSurvey, pawns int, geysers []policy.PowerGeyser) error {
	plan, known := policy.DeriveLayoutPlan(survey, pawns, geysers).Value()
	if !known {
		clockSchedulerLog("map survey holds no core for the layout plan")
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, plan); err != nil {
		return err
	}
	clockEvent(ctx, "layout", "layout_plan", fmt.Sprintf("layout plan for %d colonists %s", pawns, plan.Summary()), "colonists", pawns)
	return nil
}

// replanLayout grows the recorded v2 plan over a fresh survey and records
// it when it changed.
func (r *RoutineReviewer) replanLayout(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, plan policy.LayoutPlan, survey policy.MapSurvey, pawns, tombs int, outgrown bool) error {
	next, changed := policy.ReplanLayout(plan, survey, pawns, tombs)
	if next.TombRooms() < tombs {
		clockSchedulerLog("layout plan holds no room for tomb %d", tombs)
	}
	if !changed {
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, next); err != nil {
		return err
	}
	clockEvent(ctx, "layout", "layout_replan", fmt.Sprintf("layout plan replanned for %d colonists outgrown=%t %s", pawns, outgrown, next.Summary()), "colonists", pawns, "outgrown", outgrown)
	return nil
}

// heatRedrawEvery is how often the traffic heat layers are redrawn (one
// game hour).
const heatRedrawEvery domain.Tick = 2500

// drawHeatOverlay redraws a "heat.<layer>" overlay layer per traffic layer
// (#817) from the census's busiest cells, on its own hourly cadence; with
// the overlay off it removes them once. Output only, like the layout.
func (r *RoutineReviewer) drawHeatOverlay(ctx context.Context, native LayoutOverlayNative, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) {
	census, known := projection.Facts.Upkeep.Flooring.Value()
	on := r.layoutOverlay && known
	if !on && r.heatCleared {
		return
	}
	tick := projection.Identity.Tick
	if on && r.heatDrawn != 0 && tick >= r.heatDrawn && tick-r.heatDrawn < heatRedrawEvery {
		return
	}
	for _, layer := range policy.TrafficLayers {
		var heat policy.LayoutOverlay
		if on {
			heat = policy.TrafficOverlay(census.Traffic, layer, projection.Bounds)
		}
		if _, _, err := native.DrawOverlay(ctx, controlIdentity(snapshot), "heat."+string(layer), heat, on && len(heat.Layers) > 0); err != nil {
			clockSchedulerLog("heat overlay %s not drawn: %v", layer, err)
			return
		}
	}
	r.heatDrawn, r.heatCleared = tick, !on
}

// tombGrowthRefused reports that a layout survey ran within the last day
// while the projection's plan still has every tomb full (#857): the review
// replans for another tomb whenever they are all full and no survey ran
// that day, and at once after a restart (planSurveyed is unset), so a plan
// still full after it holds no room for one. Nothing is remembered beyond
// the survey tick the reviewer already keeps.
func (r *RoutineReviewer) tombGrowthRefused(tick domain.Tick) bool {
	return r.planSurveyed && r.planChecked <= tick && tick-r.planChecked < layoutReplanEvery
}
