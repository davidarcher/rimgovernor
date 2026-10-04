package executor

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestAdmittedMethodDependenciesGateNativeHands(t *testing.T) {
	f := newFixture(t)
	ctx := context.Background()
	var actions []domain.Action
	for i, id := range []domain.ActionID{"foundation", "finish"} {
		b, err := domain.NewBuilding("Wall", domain.Cell{X: int32(30 + i), Z: 30}, domain.North, "WoodLog")
		if err != nil {
			t.Fatal(err)
		}
		a, err := domain.NewBuildingAction(id, b)
		if err != nil {
			t.Fatal(err)
		}
		actions = append(actions, a)
	}
	plan, err := domain.NewPlan("method", 1, actions, domain.ActionDependency{Action: "finish", Requires: "foundation"})
	if err != nil {
		t.Fatal(err)
	}
	scope := f.authority.Snapshot
	scope.Plan = plan.ID()
	goal, err := domain.NewStandard("shelter", 2, scope, 100)
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.SeedStandard(ctx, goal); err != nil {
		t.Fatal(err)
	}
	g, err := f.store.ReviewStandard(ctx, goal.ID, 0, scope, 100, domain.FindingUnmet)
	if err != nil {
		t.Fatal(err)
	}
	r := store.BuildingMethodRequest{Owner: g, Method: "walls", Plan: plan, Current: scope, Tick: 100, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Purpose: policy.Rounds, Stock: policy.StockObservation{Snapshot: scope, Tick: 100, Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(20))}}}}
	for _, a := range actions {
		b, _ := a.Building()
		r.Previews = append(r.Previews, policy.Preview{Action: a, Snapshot: scope, Tick: 100, CanPlace: domain.Known(true), SafeToPlace: domain.Known(true), MadeFromStuff: domain.Known(true), Footprint: domain.Known([]domain.Cell{b.Cell()}), Costs: domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 10}})})
	}
	d, err := f.store.AdmitBuildingMethod(ctx, r)
	if err != nil || !d.Admitted {
		t.Fatal(d, err)
	}
	f.authority.Snapshot = scope
	if err = f.executor.UpdateAuthority(f.authority); err != nil {
		t.Fatal(err)
	}
	result, err := f.executor.runOne(ctx, plan.ID(), "finish")
	if err == nil || result.NativeCalled {
		t.Fatal("dependency permitted native call", result, err)
	}
	result, err = f.executor.runOne(ctx, plan.ID(), "foundation")
	if err != nil || !result.NativeCalled {
		t.Fatal("upfront reservation could not enter Hands", result, err)
	}
	// The applied receipt is terminal (#856), but the successor waits for
	// the census to report the building built, not its blueprint (#937).
	if result.Progress.View().Stage != domain.Completed {
		t.Fatal("applied intent did not complete", result)
	}
	f.env.tick = 101
	if result, err = f.executor.runOne(ctx, plan.ID(), "finish"); err == nil || result.NativeCalled {
		t.Fatal("blueprint released its successor", result, err)
	}
	foundation, _ := actions[0].Building()
	census := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "Wall1", Building: foundation, Cells: []domain.Cell{foundation.Cell()}}}}
	if _, err = f.store.ReviewRounds(ctx, store.RoundsRequest{Current: scope, Tick: 101, Policy: policy.DefaultRoundsPolicy(), Facts: policy.RoundsFacts{CurrentConstruction: domain.Known(census)}}); err != nil {
		t.Fatal(err)
	}
	f.env.tick = 102
	result, err = f.executor.runOne(ctx, plan.ID(), "finish")
	if err != nil || !result.NativeCalled {
		t.Fatal("completed dependency did not release successor", result, err)
	}
	if _, calls := f.env.counts(); calls != 2 {
		t.Fatal("unexpected native call count", calls)
	}
}
