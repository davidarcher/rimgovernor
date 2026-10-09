package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func methodRequest(t *testing.T, g WorkOwner, id string, costs ...int64) BuildingMethodRequest {
	t.Helper()
	var actions []domain.Action
	for i := range costs {
		b, e := domain.NewBuilding("Wall", domain.Cell{X: int32(len(id)*10 + i), Z: 5}, domain.North, "WoodLog")
		if e != nil {
			t.Fatal(e)
		}
		a, e := domain.NewBuildingAction(domain.ActionID(id+string(rune('a'+i))), b)
		if e != nil {
			t.Fatal(e)
		}
		actions = append(actions, a)
	}
	p, e := domain.NewPlan(domain.PlanID(id), 1, actions)
	if e != nil {
		t.Fatal(e)
	}
	summary, _ := SummarizeOwner(g)
	s := summary.Snapshot
	s.Plan = p.ID()
	s.Revision = p.Revision()
	r := BuildingMethodRequest{Owner: g, Method: "build", Plan: p, Current: s, Tick: 10, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Purpose: policy.Rounds,
		Stock: policy.StockObservation{Snapshot: s, Tick: 10, Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(101))}}}}
	for i, a := range actions {
		b, _ := a.Building()
		r.Previews = append(r.Previews, policy.Preview{Action: a, Snapshot: s, Tick: r.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known([]domain.Cell{b.Cell()}), Costs: domain.Known([]policy.Amount{{Resource: "WoodLog", Count: costs[i]}})})
	}
	return r
}
func anotherGoal(t *testing.T, s *Store, id domain.ConcernID) StandardState {
	t.Helper()
	ctx := context.Background()
	g, e := domain.NewStandard(id, 3, scope())
	if e != nil {
		t.Fatal(e)
	}
	if e = s.SeedStandard(ctx, g); e != nil {
		t.Fatal(e)
	}
	v, e := s.ReviewStandard(ctx, id, 0, scope(), 10, domain.FindingUnmet)
	if e != nil {
		t.Fatal(e)
	}
	return v
}

// A building intent is validated natively when applied: admission
// neither prices nor sites it, so a method whose previews exceed the stock
// is admitted and records no admission row.
func TestBuildingMethodAdmitsWithoutPricing(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := methodRequest(t, g, "shell", 600, 600)
	d, e := s.AdmitBuildingMethod(ctx, r)
	if e != nil || !d.Admitted || len(d.Refused) != 0 {
		t.Fatal(d, e)
	}
	p, e := s.LoadPlan(ctx, r.Plan.ID())
	if e != nil || len(p.Admissions) != 0 {
		t.Fatal(p, e)
	}
	for _, v := range p.Progress {
		if v.View().Stage != domain.Pending {
			t.Fatal("admission prepared execution")
		}
	}
	held, e := s.BuildingReservations(ctx, r.Current)
	if e != nil || len(held) != 0 {
		t.Fatal(held, e)
	}
}

// A goal rebuilt from the save carries the load it was reviewed under; a
// later load of the same colony and map still admits its work.
func TestBuildingMethodAdmitsUnderNewLoad(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := methodRequest(t, g, "reload", 10)
	r.Current.Load = "reloaded"
	if d, e := s.AdmitBuildingMethod(ctx, r); e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
}
