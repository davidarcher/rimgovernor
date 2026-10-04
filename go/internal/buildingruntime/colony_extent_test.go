package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
}
