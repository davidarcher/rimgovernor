package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
)

func TestClockEstablishesOnlyKnownExtent(t *testing.T) {
	s, _ := schedulerFixture(t)
	ctx := context.Background()
	snapshot := s.player.session.State().Snapshot
	cell := domain.Cell{X: 4, Z: 4}
	b, err := domain.NewBuilding("Wall", cell, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	p := observation.ColonyProjection{Identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Tick: 100}, Bounds: policy.Bounds{Width: 100, Height: 100}}
	p.Facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: b, Cells: []domain.Cell{cell}}}})
	p.Facts.OwnedStockpiles = domain.Known([]policy.OwnedStockpile{})
	put := func() {
		facts.Put(s.facts.store, facts.Scope{Load: string(snapshot.Load), Generation: uint64(snapshot.Native)}, facts.Colony, facts.Held[observation.ColonyProjection]{Value: p, Complete: true, AsOf: int64(p.Identity.Tick)})
	}
	put()
	if err = s.establishExtent(ctx, 100); err != nil {
		t.Fatal(err)
	}
	rows, err := s.player.journal.EstablishedColonyExtent(ctx, snapshot, 100)
	if err != nil || len(rows) != 0 {
		t.Fatalf("unknown established: %v %v", rows, err)
	}
	p.Facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "wall", Shape: domain.Known("shape"), Cells: []domain.Cell{cell}, Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}})
	put()
	for i := 0; i < 2; i++ {
		if err = s.establishExtent(ctx, 100); err != nil {
			t.Fatal(err)
		}
	}
	rows, err = s.player.journal.EstablishedColonyExtent(ctx, snapshot, 100)
	if err != nil || len(rows) != 1 {
		t.Fatalf("known extent not idempotently established: %v %v", rows, err)
	}
	r := &RoutineDefenseLayoutPlanner{reviewer: &RoutineReviewer{player: s.player}}
	p.Center = cell
	before, err := r.extentRegion(ctx, snapshot, p)
	if err != nil {
		t.Fatal(err)
	}
	if err = s.player.journal.AddExpansionArea(ctx, snapshot, 100, "east", []domain.Cell{{X: 80, Z: 4}}, "test"); err != nil {
		t.Fatal(err)
	}
	after, err := r.extentRegion(ctx, snapshot, p)
	if err != nil || after == before {
		t.Fatalf("consumer ignored expansion: %v %v %v", before, after, err)
	}
	if err = s.player.journal.RemoveExpansionArea(ctx, snapshot, 100, "east", "test"); err != nil {
		t.Fatal(err)
	}
	restored, err := r.extentRegion(ctx, snapshot, p)
	if err != nil || restored != before {
		t.Fatalf("consumer retained removed expansion: %v %v", restored, err)
	}
	if err = s.player.journal.AddExpansionArea(ctx, snapshot, 150, "later", []domain.Cell{{X: 70, Z: 4}}, "newer than cached census"); err != nil {
		t.Fatal(err)
	}
	if err = s.establishExtent(ctx, 200); err != nil {
		t.Fatal(err)
	}
	areas, err := s.player.journal.ExpansionAreas(ctx, snapshot, 200)
	if err != nil || len(areas) != 1 {
		t.Fatalf("cached census rewound expansion: %v %v", areas, err)
	}
	s.player.worlds = extentWorld{identity: observation.Identity{Colony: snapshot.Colony, Map: snapshot.Map, Load: snapshot.Load, Tick: 200}}
	world := playerWorld(snapshot)
	if err = s.player.ChangeExpansionArea(ctx, world, "player", "test", []domain.Cell{{X: 60, Z: 5}, {X: 60, Z: 4}}, false); err != nil {
		t.Fatal(err)
	}
	if err = s.player.ChangeExpansionArea(ctx, world, "outside", "test", []domain.Cell{{X: 100, Z: 4}}, false); err == nil {
		t.Fatal("out of map expansion accepted")
	}
	wrong := world
	wrong.Load = "other"
	if err = s.player.ChangeExpansionArea(ctx, wrong, "player", "test", nil, true); err == nil {
		t.Fatal("wrong world removal accepted")
	}
	if err = s.player.ChangeExpansionArea(ctx, world, "player", "test", nil, true); err != nil {
		t.Fatal(err)
	}
}

type extentWorld struct{ identity observation.Identity }

func (w extentWorld) ReadWorld(context.Context) (store.World, error) {
	return store.World{Colony: w.identity.Colony, Map: w.identity.Map, Load: w.identity.Load}, nil
}
func (w extentWorld) ReadExtentIdentity(context.Context) (observation.Identity, error) {
	return w.identity, nil
}
