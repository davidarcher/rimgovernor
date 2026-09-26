package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// surveyNative serves a flat open map; its other reads are never called.
type surveyNative struct {
	observation.RoutineSource
	reads int
}

func (n *surveyNative) ReadMapSurvey(_ context.Context, _ *c.Identity, bounds policy.Bounds) (policy.MapSurvey, bridge.Result, error) {
	n.reads++
	s := policy.MapSurvey{Bounds: bounds}
	for z := int32(0); z < bounds.Height; z++ {
		for x := int32(0); x < bounds.Width; x++ {
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: 1})
		}
	}
	return s, bridge.Result{}, nil
}

func TestReviewColonyGridEstablishesTheMasterPlanFromTheSurvey(t *testing.T) {
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	native := &surveyNative{}
	r := &RoutineReviewer{player: s.player, native: native}
	snapshot := s.player.session.State().Snapshot
	projection := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Tick: 100}, Bounds: policy.Bounds{Width: 160, Height: 160}}
	projection.Facts.Colonists = domain.Known(int64(3))
	if err := r.reviewColonyGrid(ctx, snapshot, &projection); err != nil {
		t.Fatal(err)
	}
	grid, known := projection.ColonyGrid.Value()
	plan, planned := projection.ColonyPlan.Value()
	if !known || grid.Source != policy.ColonyGridFromSurvey || !planned || plan.Grid != grid || len(plan.Modules) == 0 || native.reads != 1 {
		t.Fatalf("grid %+v %v plan %v reads %d", grid, known, planned, native.reads)
	}
	// A later review serves the recorded plan without another survey; the
	// colony outgrowing its housing replans once a day.
	later := observation.ColonyProjection{Identity: projection.Identity, Bounds: projection.Bounds}
	later.Identity.Tick = 200
	later.Facts.Colonists = domain.Known(int64(3))
	if err := r.reviewColonyGrid(ctx, snapshot, &later); err != nil || native.reads != 1 {
		t.Fatal(err, native.reads)
	}
	later.Identity.Tick = 100 + masterReplanEvery
	later.Facts.Colonists = domain.Known(int64(60))
	if err := r.reviewColonyGrid(ctx, snapshot, &later); err != nil || native.reads != 2 {
		t.Fatal(err, native.reads)
	}
	if grown, _ := later.ColonyPlan.Value(); grown.Radius != plan.Radius+1 || grown.Grid != grid {
		t.Fatalf("replan %+v", grown.Radius)
	}
}
