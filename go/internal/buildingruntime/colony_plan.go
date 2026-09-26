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
)

// masterReplanEvery is the fewest ticks between replans (one game day):
// a colony the widened plan still cannot house waits instead of re-reading
// the whole map every review.
const masterReplanEvery domain.Tick = 60000

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
// on the one trigger a review can see cheaply: more colonists than the
// housing modules hold. A replan re-reads the survey, keeps the grid and
// every sound slot, and records the new module set.
func (r *RoutineReviewer) reviewMasterPlan(ctx context.Context, snapshot domain.GenerationSnapshot, projection *observation.ColonyProjection) error {
	grid, _ := projection.ColonyGrid.Value()
	if grid.Source != policy.ColonyGridFromSurvey {
		return nil
	}
	tick := projection.Identity.Tick
	record, ok, err := r.player.journal.ColonyPlan(ctx, snapshot, tick)
	if err != nil || !ok {
		return err
	}
	plan := policy.MasterPlan{Grid: grid, Radius: record.Radius, Modules: record.Modules}
	pawns, known := projection.Facts.Colonists.Value()
	if native, ok := r.native.(MapSurveyNative); ok && known && plan.Outgrown(int(pawns)) && tick-record.Tick >= masterReplanEvery {
		if survey, _, err := native.ReadMapSurvey(ctx, controlIdentity(snapshot), projection.Bounds); err != nil {
			clockSchedulerLog("master plan replan deferred, map survey unavailable: %v", err)
		} else {
			plan = plan.Replan(survey, int(pawns))
			if err = r.player.journal.RecordColonyPlan(ctx, snapshot, tick, plan.Radius, plan.Modules); err != nil {
				return err
			}
			clockEvent(ctx, "layout", "master_replan", fmt.Sprintf("master plan replanned for %d colonists radius=%d", pawns, plan.Radius), "colonists", pawns, "radius", plan.Radius)
		}
	}
	projection.ColonyPlan = domain.Known(plan)
	return nil
}
