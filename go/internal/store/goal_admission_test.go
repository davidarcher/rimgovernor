package store

import (
	"context"
	"errors"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func methodRequest(t *testing.T, g GoalState, id string, costs ...int64) BuildingMethodRequest {
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
	s := g.Goal.Snapshot
	s.Plan = p.ID()
	s.Revision = p.Revision()
	r := BuildingMethodRequest{Goal: g.Goal.ID, Revision: g.Revision, Method: "build", Plan: p, Current: s, Tick: g.Goal.Tick, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Purpose: policy.Routine,
		Stock: policy.StockObservation{Snapshot: s, Tick: g.Goal.Tick, Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(100))}}}}
	for i, a := range actions {
		b, _ := a.Building()
		r.Previews = append(r.Previews, policy.Preview{Action: a, Snapshot: s, Tick: r.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known([]domain.Cell{b.Cell()}), Costs: domain.Known([]policy.Amount{{Resource: "WoodLog", Count: costs[i]}})})
	}
	return r
}
func anotherGoal(t *testing.T, s *Store, id domain.GoalID) GoalState {
	t.Helper()
	ctx := context.Background()
	g, e := domain.NewGoal(id, domain.PlayerGoal, 3, scope(), 10)
	if e != nil {
		t.Fatal(e)
	}
	if e = s.CreateGoal(ctx, g); e != nil {
		t.Fatal(e)
	}
	v, e := s.ReviewGoal(ctx, id, 0, scope(), 10, domain.NeedDeficit, false)
	if e != nil {
		t.Fatal(e)
	}
	return v
}
func TestBuildingMethodAdmissionAllOrNothing(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := methodRequest(t, g, "method", 60, 60)
	d, e := s.AdmitBuildingMethod(ctx, r)
	if e != nil || d.Admitted || len(d.Refused) != 1 {
		t.Fatal(d, e)
	}
	if _, e = s.LoadPlan(ctx, r.Plan.ID()); !errors.Is(e, ErrNotFound) {
		t.Fatal("partial method persisted", e)
	}
	r.Stock.Values[0].Available = domain.Known(int64(120))
	d, e = s.AdmitBuildingMethod(ctx, r)
	if e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
	p, e := s.LoadPlan(ctx, r.Plan.ID())
	if e != nil || len(p.Admissions) != 2 {
		t.Fatal(p, e)
	}
	for _, v := range p.Progress {
		if v.View().Stage != domain.Pending {
			t.Fatal("admission prepared execution")
		}
	}
}

// A PartialStock request admits the candidates the stock covers and commits
// the whole plan; the refused ones stay pending without a reservation for
// the worker to admit afresh (#602). Any other refusal, or no candidate
// covered, still refuses the method whole.
func TestBuildingMethodAdmitsAffordablePrefixWhenPartialStock(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := methodRequest(t, g, "method", 60, 30, 30)
	r.PartialStock = true
	d, e := s.AdmitBuildingMethod(ctx, r)
	if e != nil || !d.Admitted || len(d.Refused) != 1 || d.Refused[0].Reason != policy.InsufficientStock || d.Refused[0].Action != r.Plan.Actions()[2].ID() {
		t.Fatal(d, e)
	}
	p, e := s.LoadPlan(ctx, r.Plan.ID())
	if e != nil || len(p.Progress) != 3 || len(p.Admissions) != 2 {
		t.Fatal(p, e)
	}
	for _, a := range p.Admissions {
		if a.Action == r.Plan.Actions()[2].ID() || a.Admission.Purpose != policy.Routine {
			t.Fatal("refused candidate reserved or purpose lost", a)
		}
	}
	for _, v := range p.Progress {
		if v.View().Stage != domain.Pending {
			t.Fatal("admission prepared execution")
		}
	}
	// The unreserved action holds nothing against a competing method.
	other := anotherGoal(t, s, "storage")
	competing := methodRequest(t, other, "competing", 10)
	if d, e = s.AdmitBuildingMethod(ctx, competing); e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
	// Nothing covered: refused whole, as is a mixed refusal.
	third := anotherGoal(t, s, "third")
	none := methodRequest(t, third, "none", 5, 5)
	none.PartialStock = true
	if d, e = s.AdmitBuildingMethod(ctx, none); e != nil || d.Admitted || len(d.Refused) != 2 {
		t.Fatal(d, e)
	}
	mixed := methodRequest(t, third, "mixed", 1, 1, 60)
	mixed.PartialStock = true
	mixed.Previews[1].SafeToPlace = domain.Known(false)
	if d, e = s.AdmitBuildingMethod(ctx, mixed); e != nil || d.Admitted || d.Refused[1].Reason != policy.UnsafePlacement {
		t.Fatal(d, e)
	}
	if _, e = s.LoadPlan(ctx, mixed.Plan.ID()); !errors.Is(e, ErrNotFound) {
		t.Fatal("partial method persisted", e)
	}
}

// The purpose a method is admitted under is recorded with each reservation
// and survives a restart, so dispatch spends under the same class (#602).
func TestBuildingMethodRecordsPurpose(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	r := methodRequest(t, g, "shell", 60, 60, 60)
	r.Purpose = policy.Shelter
	if d, e := s.AdmitBuildingMethod(ctx, r); e != nil || !d.Admitted || len(d.Refused) != 0 {
		t.Fatal("shelter held for stock", d, e)
	}
	s.Close()
	s = open(t, path)
	p, e := s.LoadPlan(ctx, r.Plan.ID())
	if e != nil || len(p.Admissions) != 3 {
		t.Fatal(p, e)
	}
	for _, a := range p.Admissions {
		if a.Admission.Purpose != policy.Shelter || a.Admission.SpendingPurpose() != policy.Shelter {
			t.Fatal(a)
		}
	}
	if (Admission{}).SpendingPurpose() != policy.Routine {
		t.Fatal("unrecorded purpose is not routine")
	}
	held, e := s.BuildingReservations(ctx, r.Current)
	if e != nil || len(held) != 3 {
		t.Fatal(held, e)
	}
}
func TestBuildingMethodReservesDependenciesBeforeTheyAreReady(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	r := methodRequest(t, g, "method", 40, 40)
	var e error
	r.Plan, e = domain.NewPlan(r.Plan.ID(), r.Plan.Revision(), r.Plan.Actions(), domain.ActionDependency{Action: r.Plan.Actions()[1].ID(), Requires: r.Plan.Actions()[0].ID()})
	if e != nil {
		t.Fatal(e)
	}
	if d, e := s.AdmitBuildingMethod(ctx, r); e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
	s.Close()
	s = open(t, path)
	other := anotherGoal(t, s, "storage")
	competing := methodRequest(t, other, "competing", 30)
	d, e := s.AdmitBuildingMethod(ctx, competing)
	if e != nil || d.Admitted || d.Refused[0].Reason != policy.InsufficientStock {
		t.Fatal(d, e)
	}
	current, e := s.LoadGoal(ctx, g.Goal.ID)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.CancelGoal(ctx, g.Goal.ID, current.Revision); e != nil {
		t.Fatal(e)
	}
	d, e = s.AdmitBuildingMethod(ctx, competing)
	if e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
}
func TestBuildingMethodRejectsUnknownCostsFloorsAndGeometry(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"cost", "stock", "floor", "stopped", "overlap", "direction"} {
		t.Run(change, func(t *testing.T) {
			s, _, g := goalFixture(t)
			r := methodRequest(t, g, "method", 40, 40)
			switch change {
			case "cost":
				r.Previews[1].Costs = domain.Unknown[[]policy.Amount]()
			case "stock":
				r.Stock.Values[0].Available = domain.Unknown[int64]()
			case "floor":
				r.Rules = []policy.ResourceRule{{Resource: "WoodLog", Reserve: 30, Spending: policy.Allow}}
			case "stopped":
				r.Rules = []policy.ResourceRule{{Resource: "WoodLog", Spending: policy.Stop}}
			case "overlap":
				r.Previews[1].Footprint = r.Previews[0].Footprint
			case "direction":
				r.Current.Native--
			}
			d, e := s.AdmitBuildingMethod(context.Background(), r)
			if e == nil && d.Admitted {
				t.Fatal("invalid method accepted")
			}
			if _, e = s.LoadPlan(context.Background(), r.Plan.ID()); !errors.Is(e, ErrNotFound) {
				t.Fatal(e)
			}
		})
	}
}
func TestConcurrentMethodsCannotDoubleSpendObservedStock(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, g := goalFixture(t)
	other := anotherGoal(t, s, "second")
	second := open(t, path)
	requests := []BuildingMethodRequest{methodRequest(t, g, "one", 60), methodRequest(t, other, "four", 60)}
	stores := []*Store{s, second}
	results := make(chan BuildingMethodDecision, 2)
	failures := make(chan error, 2)
	var wg sync.WaitGroup
	for i := range requests {
		wg.Go(func() { d, e := stores[i].AdmitBuildingMethod(ctx, requests[i]); results <- d; failures <- e })
	}
	wg.Wait()
	admitted := 0
	for range requests {
		d := <-results
		if e := <-failures; e != nil {
			t.Fatal(e)
		}
		if d.Admitted {
			admitted++
		}
	}
	if admitted != 1 {
		t.Fatal("expected exactly one admitted method", admitted)
	}
}
func TestMethodCostsAndGoalRollbackTogether(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	r := methodRequest(t, g, "method", 40)
	if _, e := s.db.ExecContext(ctx, `CREATE TRIGGER fail_method_reservation BEFORE INSERT ON admissions BEGIN SELECT RAISE(ABORT,'reservation failure'); END`); e != nil {
		t.Fatal(e)
	}
	if _, e := s.AdmitBuildingMethod(ctx, r); e == nil {
		t.Fatal("injected failure was ignored")
	}
	if _, e := s.LoadPlan(ctx, r.Plan.ID()); !errors.Is(e, ErrNotFound) {
		t.Fatal("orphan plan", e)
	}
	after, e := s.LoadGoal(ctx, g.Goal.ID)
	if e != nil || after.Revision != g.Revision || len(after.Methods) != 0 {
		t.Fatal(after, e)
	}
}

func TestBuildingMethodAdmitsMixedCostedAndWallRemovalBundle(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, g := goalFixture(t)
	building, e := domain.NewBuilding("Wall", domain.Cell{X: 3, Z: 5}, domain.North, "BlocksGranite")
	if e != nil {
		t.Fatal(e)
	}
	place, e := domain.NewBuildingAction("place", building)
	if e != nil {
		t.Fatal(e)
	}
	removal, e := domain.NewWallRemoval("original-wall", "", 3, 5, 0, 1, false, false, "")
	if e != nil {
		t.Fatal(e)
	}
	demolish, e := domain.NewWallRemovalAction("demolish", removal)
	if e != nil {
		t.Fatal(e)
	}
	p, e := domain.NewPlan("stone-shell", 1, []domain.Action{place, demolish})
	if e != nil {
		t.Fatal(e)
	}
	scope := g.Goal.Snapshot
	scope.Plan, scope.Revision = p.ID(), p.Revision()
	r := BuildingMethodRequest{Goal: g.Goal.ID, Revision: g.Revision, Method: "stone-shell", Plan: p, Current: scope, Tick: g.Goal.Tick, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Purpose: policy.Routine,
		Stock:    policy.StockObservation{Snapshot: scope, Tick: g.Goal.Tick, Values: []policy.Stock{{Resource: "BlocksGranite", Available: domain.Known(int64(100))}}},
		Previews: []policy.Preview{{Action: place, Snapshot: scope, Tick: g.Goal.Tick, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known([]domain.Cell{{X: 3, Z: 5}}), Costs: domain.Known([]policy.Amount{{Resource: "BlocksGranite", Count: 10}})}},
	}
	d, e := s.AdmitBuildingMethod(ctx, r)
	if e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
	loaded, e := s.LoadPlan(ctx, p.ID())
	if e != nil || len(loaded.Admissions) != 1 || loaded.Admissions[0].Action != "place" {
		t.Fatal(loaded, e)
	}

	// A preview on a wall-removal action is refused outright, and the plan is
	// not persisted.
	bad := r
	bad.Plan, e = domain.NewPlan("stone-shell-bad", 1, []domain.Action{place, demolish})
	if e != nil {
		t.Fatal(e)
	}
	bad.Current.Plan = bad.Plan.ID()
	bad.Previews = append(bad.Previews, policy.Preview{Action: demolish, Snapshot: scope, Tick: g.Goal.Tick})
	if d, e := s.AdmitBuildingMethod(ctx, bad); e == nil && d.Admitted {
		t.Fatal("wall removal action accepted a preview", d)
	}
	if _, e := s.LoadPlan(ctx, bad.Plan.ID()); !errors.Is(e, ErrNotFound) {
		t.Fatal("invalid bundle persisted", e)
	}
}

func TestMethodAdmissionReadAfterRestartHasCompleteReservations(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	g := anotherGoal(t, s, "goal")
	r := methodRequest(t, g, "method", 80)
	if d, e := s.AdmitBuildingMethod(ctx, r); e != nil || !d.Admitted {
		t.Fatal(d, e)
	}
	s.Close()
	s = open(t, path)
	p, e := s.LoadPlan(ctx, r.Plan.ID())
	if e != nil || len(p.Admissions) != 1 || p.Admissions[0].Admission.Costs[0].Count != 80 {
		t.Fatal(p, e)
	}
}
