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
	if native, ok := r.native.(MapSurveyNative); ok && (outgrown || missing || quadrum) {
		if survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds); err != nil {
			clockSchedulerLog("layout plan check deferred, map survey unavailable: %v", err)
		} else {
			r.planChecked, r.planSurveyed = tick, true
			if !haveLayout {
				err = r.deriveLayoutPlan(ctx, snapshot, tick, survey, int(pawns))
			} else {
				err = r.replanLayout(ctx, snapshot, tick, layout.Plan, survey, int(pawns), outgrown)
			}
			if err != nil {
				return err
			}
			if layout, haveLayout, err = r.layoutPlan(ctx, snapshot, tick); err != nil {
				return err
			}
		}
	}
	if haveLayout {
		projection.LayoutPlan = domain.Known(layout.Plan)
	}
	r.drawLayoutOverlay(ctx, snapshot, projection, layout, haveLayout)
	return nil
}

// overlayRedrawEvery is how often an unchanged plan's overlay is redrawn
// (one game day), so rooms built since take their colors and labels.
const overlayRedrawEvery domain.Tick = 60000

// LayoutOverlayNative draws the layout plan as native plan designations
// (#726, bridge.Client.DrawLayoutPlan).
type LayoutOverlayNative interface {
	DrawLayoutPlan(context.Context, *c.Identity, policy.LayoutOverlay, bool) (*p.LayoutPlanApplied, bridge.Result, error)
}

// drawLayoutOverlay rewrites the overlay when the plan changed or a day
// passed since the last draw; with the overlay off it deletes the owned
// plans once per process. Output only: a failure is logged, never fatal.
func (r *RoutineReviewer) drawLayoutOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection, layout store.LayoutPlanRecord, haveLayout bool) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	tick := projection.Identity.Tick
	if !r.layoutOverlay || !haveLayout {
		if !r.overlayCleared {
			if _, _, err := native.DrawLayoutPlan(ctx, controlIdentity(snapshot), policy.LayoutOverlay{}, false); err != nil {
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
	applied, _, err := native.DrawLayoutPlan(ctx, controlIdentity(snapshot), layout.Plan.Overlay(projection.Bounds), true)
	if err != nil {
		clockSchedulerLog("layout overlay not drawn: %v", err)
		return
	}
	r.overlayKey, r.overlayDrawn, r.overlayCleared = key, tick, false
	clockSchedulerLog("layout overlay drawn plans=%d cells=%d rooms=%d skipped=%d removed=%d", applied.GetPlans(), applied.GetCells(), applied.GetRooms(), applied.GetSkipped(), applied.GetRemoved())
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

// deriveLayoutPlan lays a fresh v2 plan over survey and records it.
func (r *RoutineReviewer) deriveLayoutPlan(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, survey policy.MapSurvey, pawns int) error {
	plan, known := policy.DeriveLayoutPlan(survey, pawns).Value()
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
func (r *RoutineReviewer) replanLayout(ctx context.Context, snapshot domain.GenerationSnapshot, tick domain.Tick, plan policy.LayoutPlan, survey policy.MapSurvey, pawns int, outgrown bool) error {
	next, changed := policy.ReplanLayout(plan, survey, pawns)
	if !changed {
		return nil
	}
	if err := r.player.journal.RecordLayoutPlan(ctx, snapshot, tick, next); err != nil {
		return err
	}
	clockEvent(ctx, "layout", "layout_replan", fmt.Sprintf("layout plan replanned for %d colonists outgrown=%t %s", pawns, outgrown, next.Summary()), "colonists", pawns, "outgrown", outgrown)
	return nil
}
