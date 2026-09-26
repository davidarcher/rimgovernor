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
	if native, ok := r.native.(MapSurveyNative); ok && (outgrown || tick-checked >= masterTerrainCheckEvery) {
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
		}
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
	if !r.layoutOverlay || !known {
		if !r.overlayCleared {
			if _, _, err := native.DrawLayoutPlan(ctx, controlIdentity(snapshot), policy.LayoutOverlay{}, false); err != nil {
				clockSchedulerLog("layout overlay not cleared: %v", err)
				return
			}
			r.overlayCleared = true
		}
		return
	}
	key := fmt.Sprint(plan.Grid, plan.Radius, plan.Modules)
	if key == r.overlayKey && tick >= r.overlayDrawn && tick-r.overlayDrawn < overlayRedrawEvery {
		return
	}
	applied, _, err := native.DrawLayoutPlan(ctx, controlIdentity(snapshot), plan.Overlay(projection.Bounds), true)
	if err != nil {
		clockSchedulerLog("layout overlay not drawn: %v", err)
		return
	}
	r.overlayKey, r.overlayDrawn, r.overlayCleared = key, tick, false
	clockSchedulerLog("layout overlay drawn plans=%d cells=%d rooms=%d skipped=%d removed=%d", applied.GetPlans(), applied.GetCells(), applied.GetRooms(), applied.GetSkipped(), applied.GetRemoved())
}
