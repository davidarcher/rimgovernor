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

// masterReplanEvery is the fewest ticks between replans (one game day):
// a colony the widened plan still cannot house waits instead of re-reading
// the whole map every review.
const masterReplanEvery domain.Tick = 60000

// masterTerrainCheckEvery is the fallback terrain check (#727): once a
// quadrum (15 days) a review re-reads the survey and replans when a
// reserved room module is no longer buildable (marsh, water, the edge).
// Mining never trips it: a mined-out module stays sound.
const masterTerrainCheckEvery domain.Tick = 15 * 60000

// MapSurveyNative is the optional native whole-map read behind the master
// layout plan (#727, bridge.Client.ReadMapSurvey). A reviewer whose native
// lacks it, or refuses the foundation field, keeps the starter-shell grid.
type MapSurveyNative interface {
	ReadMapSurvey(context.Context, *c.Identity, policy.Bounds) (policy.MapSurvey, bridge.Result, error)
}

// establishMasterPlan scores the whole map once, before any grid exists
// (settle time), and records the best placement's grid and modules. It
// reports false, without error, when the native serves no survey or no
// placement is sound; the caller then derives the grid from the starter
// shell as before. The starter shell is sited afterwards, inside the
// plan's plaza.
func (r *RoutineReviewer) establishMasterPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) (store.ColonyGridRecord, bool, error) {
	native, ok := r.native.(MapSurveyNative)
	if !ok {
		return store.ColonyGridRecord{}, false, nil
	}
	survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds)
	if err != nil {
		clockSchedulerLog("map survey unavailable, grid from the starter shell: %v", err)
		return store.ColonyGridRecord{}, false, nil
	}
	pawns, _ := projection.Facts.Colonists.Value()
	if err = r.deriveLayoutPlan(ctx, snapshot, projection.Identity.Tick, survey, int(pawns)); err != nil {
		return store.ColonyGridRecord{}, false, err
	}
	plan, known := policy.DeriveMasterPlan(survey, int(pawns)).Value()
	if !known {
		clockSchedulerLog("map survey holds no sound plaza, grid from the starter shell")
		return store.ColonyGridRecord{}, false, nil
	}
	tick := projection.Identity.Tick
	record, established, err := r.player.journal.EstablishColonyGrid(ctx, snapshot, tick, plan.Grid)
	if err != nil || !established {
		return record, err == nil, err
	}
	if err = r.player.journal.RecordColonyPlan(ctx, snapshot, tick, plan.Radius, plan.Modules); err != nil {
		return record, false, err
	}
	g := plan.Grid
	clockEvent(ctx, "layout", "colony_grid", fmt.Sprintf("colony grid origin=(%d,%d) pitch=%d source=%s score=%.2f", g.Origin.X, g.Origin.Z, g.Pitch, g.Source, plan.Score), "origin_x", g.Origin.X, "origin_z", g.Origin.Z, "pitch", g.Pitch, "source", string(g.Source))
	return record, true, nil
}

// reviewMasterPlan serves the recorded plan on the projection and replans
// on two triggers: more colonists than the housing modules hold (at
// most once a day), and a reserved module gone unbuildable (checked once
// a quadrum). A replan re-reads the survey, keeps the grid and
// every sound slot, and records the new module set.
func (r *RoutineReviewer) reviewMasterPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	grid, _ := projection.ColonyGrid.Value()
	if grid.Source != policy.ColonyGridFromSurvey {
		r.drawLayoutOverlay(ctx, snapshot, projection)
		return nil
	}
	tick := projection.Identity.Tick
	record, ok, err := r.player.journal.ColonyPlan(ctx, snapshot, tick)
	if err != nil || !ok {
		if err == nil {
			r.drawLayoutOverlay(ctx, snapshot, projection)
		}
		return err
	}
	plan := policy.MasterPlan{Grid: grid, Radius: record.Radius, Modules: record.Modules}
	pawns, known := projection.Facts.Colonists.Value()
	// The last terrain check is the newer of the last replan and the last
	// survey this process read; a rewind past it or a restart falls back
	// to the replan's tick.
	checked := record.Tick
	if r.planChecked > checked && r.planChecked <= tick {
		checked = r.planChecked
	}
	outgrown := known && plan.Outgrown(int(pawns)) && tick-record.Tick >= masterReplanEvery
	layout, haveLayout, err := r.layoutPlan(ctx, snapshot, tick)
	if err != nil {
		return err
	}
	layoutOutgrown := known && haveLayout && layout.Plan.LayoutOutgrown(int(pawns)) && tick-layout.Tick >= masterReplanEvery
	layoutMissing := !haveLayout && tick-checked >= masterReplanEvery
	quadrum := tick-checked >= masterTerrainCheckEvery
	if native, ok := r.native.(MapSurveyNative); ok && (outgrown || layoutOutgrown || layoutMissing || quadrum) {
		if survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds); err != nil {
			clockSchedulerLog("master plan check deferred, map survey unavailable: %v", err)
		} else {
			r.planChecked = tick
			if outgrown || plan.ReplanNeeded(survey, int(pawns)) {
				plan = plan.Replan(survey, int(pawns))
				if err = r.player.journal.RecordColonyPlan(ctx, snapshot, tick, plan.Radius, plan.Modules); err != nil {
					return err
				}
				clockEvent(ctx, "layout", "master_replan", fmt.Sprintf("master plan replanned for %d colonists radius=%d outgrown=%t", pawns, plan.Radius, outgrown), "colonists", pawns, "radius", plan.Radius, "outgrown", outgrown)
			}
			if !haveLayout {
				err = r.deriveLayoutPlan(ctx, snapshot, tick, survey, int(pawns))
			} else if layoutOutgrown || quadrum {
				err = r.replanLayout(ctx, snapshot, tick, layout.Plan, survey, int(pawns), layoutOutgrown)
			}
			if err != nil {
				return err
			}
		}
	}
	if layout, ok, err := r.layoutPlan(ctx, snapshot, tick); err != nil {
		return err
	} else if ok {
		projection.LayoutPlan = domain.Known(layout.Plan)
	}
	projection.ColonyPlan = domain.Known(plan)
	r.drawLayoutOverlay(ctx, snapshot, projection)
	return nil
}

// overlayRedrawEvery is how often an unchanged plan's overlay is redrawn
// (one game day), so rooms built since take their colors and labels.
const overlayRedrawEvery domain.Tick = 60000

// LayoutOverlayNative draws the master plan as native plan designations
// (#726, bridge.Client.DrawLayoutPlan).
type LayoutOverlayNative interface {
	DrawLayoutPlan(context.Context, *c.Identity, policy.LayoutOverlay, bool) (*p.LayoutPlanApplied, bridge.Result, error)
}

// drawLayoutOverlay rewrites the overlay when the plan changed or a day
// passed since the last draw; with the overlay off it deletes the owned
// plans once per process. Output only: a failure is logged, never fatal.
func (r *RoutineReviewer) drawLayoutOverlay(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) {
	native, ok := r.native.(LayoutOverlayNative)
	if !ok {
		return
	}
	tick := projection.Identity.Tick
	plan, known := projection.ColonyPlan.Value()
	var overlay func() policy.LayoutOverlay
	var key string
	// The v2 layout plan (#784) draws when one is saved; the master plan
	// is the fallback until D1 retires it.
	layout, haveLayout, err := r.layoutPlan(ctx, snapshot, tick)
	if err != nil {
		clockSchedulerLog("layout overlay: layout plan unreadable: %v", err)
	}
	switch {
	case haveLayout:
		key = fmt.Sprint("v2@", layout.Tick)
		overlay = func() policy.LayoutOverlay { return layout.Plan.Overlay(projection.Bounds) }
	case known:
		key = fmt.Sprint(plan.Grid, plan.Radius, plan.Modules)
		overlay = func() policy.LayoutOverlay { return plan.Overlay(projection.Bounds) }
	}
	if !r.layoutOverlay || overlay == nil {
		if !r.overlayCleared {
			if _, _, err := native.DrawLayoutPlan(ctx, controlIdentity(snapshot), policy.LayoutOverlay{}, false); err != nil {
				clockSchedulerLog("layout overlay not cleared: %v", err)
				return
			}
			r.overlayCleared = true
		}
		return
	}
	if key == r.overlayKey && tick >= r.overlayDrawn && tick-r.overlayDrawn < overlayRedrawEvery {
		return
	}
	applied, _, err := native.DrawLayoutPlan(ctx, controlIdentity(snapshot), overlay(), true)
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
