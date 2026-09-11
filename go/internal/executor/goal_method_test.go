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
	goal, err := domain.NewGoal("shelter", domain.AutopilotGoal, 2, scope, 100)
	if err != nil {
		t.Fatal(err)
	}
	if err = f.store.CreateGoal(ctx, goal); err != nil {
		t.Fatal(err)
	}
	g, err := f.store.ReviewGoal(ctx, goal.ID, 0, scope, 100, domain.NeedDeficit, false)
	if err != nil {
		t.Fatal(err)
	}
	r := store.BuildingMethodRequest{Goal: goal.ID, Revision: g.Revision, Method: "walls", Plan: plan, Current: scope, Tick: 100, Bounds: domain.Known(policy.Bounds{Width: 100, Height: 100}), Purpose: policy.Routine, Stock: policy.StockObservation{Snapshot: scope, Tick: 100, Values: []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(20))}}}}
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
	result, err := f.executor.Run(ctx, plan.ID(), "finish")
	if err == nil || result.NativeCalled {
		t.Fatal("dependency permitted native call", result, err)
	}
	result, err = f.executor.Run(ctx, plan.ID(), "foundation")
	if err != nil || !result.NativeCalled {
		t.Fatal("upfront reservation could not enter Hands", result, err)
	}
	result, err = f.executor.Run(ctx, plan.ID(), "finish")
	if err == nil || result.NativeCalled {
		t.Fatal("receipt satisfied dependency", result, err)
	}
	f.env.onObserve = func(p Placement, g domain.GenerationSnapshot, _ int) Evidence {
		return f.env.evidence(p, g, domain.EffectCompleted, true)
	}
	result, err = f.executor.Run(ctx, plan.ID(), "foundation")
	if err != nil || result.Progress.View().Stage != domain.Completed {
		t.Fatal(result, err)
	}
	f.env.tick = 102
	f.env.stock = 10
	result, err = f.executor.Run(ctx, plan.ID(), "finish")
	if err != nil || !result.NativeCalled {
		t.Fatal("observed dependency did not release successor", result, err)
	}
	if _, calls, _ := f.env.counts(); calls != 2 {
		t.Fatal("unexpected native call count", calls)
	}
}
