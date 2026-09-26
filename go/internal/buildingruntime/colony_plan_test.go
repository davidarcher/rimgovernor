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
	// marsh floods these cells.
	marsh func(domain.Cell) bool
}

func (n *surveyNative) ReadMapSurvey(_ context.Context, _ *c.Identity, bounds policy.Bounds) (policy.MapSurvey, bridge.Result, error) {
	n.reads++
	s := policy.MapSurvey{Bounds: bounds}
	for z := int32(0); z < bounds.Height; z++ {
		for x := int32(0); x < bounds.Width; x++ {
			s.Cells = append(s.Cells, policy.SurveyCell{Cell: domain.Cell{X: x, Z: z}, Walkable: true, Fertility: 1, Marsh: n.marsh != nil && n.marsh(domain.Cell{X: x, Z: z})})
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

func TestReviewMasterPlanTerrainCheckReplansAQuadrumLater(t *testing.T) {
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	native := &surveyNative{}
	r := &RoutineReviewer{player: s.player, native: native}
	snapshot := s.player.session.State().Snapshot
	review := func(tick domain.Tick) policy.MasterPlan {
		t.Helper()
		p := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Tick: tick}, Bounds: policy.Bounds{Width: 160, Height: 160}}
		p.Facts.Colonists = domain.Known(int64(3))
		if err := r.reviewColonyGrid(ctx, snapshot, &p); err != nil {
			t.Fatal(err)
		}
		plan, _ := p.ColonyPlan.Value()
		return plan
	}
	plan := review(100)
	var storage policy.PlanModule
	for _, m := range plan.Modules {
		if m.Role == policy.ModuleStorage {
			storage = m
			break
		}
	}
	// Sound terrain a quadrum on: one survey, no replan.
	if again := review(100 + masterTerrainCheckEvery); native.reads != 2 || len(again.Modules) != len(plan.Modules) {
		t.Fatal(native.reads)
	}
	if record, _, _ := s.player.journal.ColonyPlan(ctx, snapshot, 100+masterTerrainCheckEvery); record.Tick != 100 {
		t.Fatal("a sound check recorded a replan", record.Tick)
	}
	// Within the quadrum no survey is read.
	review(200 + masterTerrainCheckEvery)
	if native.reads != 2 {
		t.Fatal(native.reads)
	}
	// Marsh floods the storage module: the next check moves storage.
	flooded := plan.Rect(storage)
	native.marsh = func(c domain.Cell) bool {
		return c.X >= flooded.X && c.X < flooded.X+flooded.Width && c.Z >= flooded.Z && c.Z < flooded.Z+flooded.Height
	}
	replanned := review(100 + 2*masterTerrainCheckEvery)
	if native.reads != 3 {
		t.Fatal(native.reads)
	}
	for _, m := range replanned.Modules {
		if m.U == storage.U && m.V == storage.V && m.Role == policy.ModuleStorage {
			t.Fatal("storage stayed on marsh")
		}
	}
}
