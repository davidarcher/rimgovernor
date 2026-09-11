package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestSelectedPlacementUsesSharedMethodAccounting(t *testing.T) {
	ctx := context.Background()
	s, _, g := goalFixture(t)
	other := anotherGoal(t, s, "other")
	held, err := s.AdmitBuildingMethod(ctx, methodRequest(t, other, "held", 50))
	if err != nil || !held.Admitted {
		t.Fatal(held, err)
	}
	r := methodRequest(t, g, "bed", 60, 60)
	r.Previews[0].SafeToPlace = domain.Known(false)
	searchRequest := policy.PlacementSearchRequest{Snapshot: r.Current, Tick: r.Tick, Bounds: policy.Bounds{Width: 100, Height: 100}, Center: domain.Cell{X: 30, Z: 5}, Environment: policy.PlacementAnywhere, Radius: 22, Limit: 64}
	for _, a := range r.Plan.Actions() {
		b, _ := a.Building()
		searchRequest.Cells = append(searchRequest.Cells, policy.SiteCell{Cell: b.Cell(), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false)})
	}
	search, err := policy.NewPlacementSearch(searchRequest)
	if err != nil {
		t.Fatal(err)
	}
	selected, ok, err := search.Select("Wall", "WoodLog", r.Previews)
	if err != nil || !ok || selected.Action.ID() != r.Plan.Actions()[1].ID() {
		t.Fatal(selected, ok, err)
	}
	r.Plan, err = domain.NewPlan(r.Plan.ID(), r.Plan.Revision(), []domain.Action{selected.Action})
	if err != nil {
		t.Fatal(err)
	}
	r.Previews = []policy.Preview{selected}
	d, err := s.AdmitBuildingMethod(ctx, r)
	if err != nil || d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != policy.InsufficientStock {
		t.Fatal("selection bypassed shared stock", d, err)
	}
	if _, err = s.CancelGoal(ctx, other.Goal.ID, held.Goal.Revision); err != nil {
		t.Fatal(err)
	}
	d, err = s.AdmitBuildingMethod(ctx, r)
	if err != nil || !d.Admitted || len(d.Goal.Methods) != 1 {
		t.Fatal(d, err)
	}
	p, err := s.LoadPlan(ctx, r.Plan.ID())
	if err != nil || p.Progress[0].View().Stage != domain.Pending {
		t.Fatal("selection issued work", p, err)
	}
}
