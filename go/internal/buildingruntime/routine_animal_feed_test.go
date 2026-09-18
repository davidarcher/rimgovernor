package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// A kibble bill plan counts as standing only once its bill action completed;
// an open or cancelled bill, or a non-bill plan, earns no clock window.
func TestCompletedBillPlan(t *testing.T) {
	t.Parallel()
	bill, err := domain.NewProductionBill("Thing_ButcherSpot1", "Make_Kibble", "bill-token", domain.StockTarget, 65)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("kibble-0", bill)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("kibble-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewProgress(spec, action.ID())
	if err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 1, Load: "load", Plan: spec.ID(), Revision: spec.Revision(), Native: 1}
	if p, err = p.Prepare(snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if p, err = p.MarkDispatched(snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if completedBillPlan(store.PlanState{Spec: spec, Progress: []domain.Progress{p}}) {
		t.Fatal("dispatched bill counted as standing")
	}
	if completedBillPlan(store.PlanState{Spec: spec}) {
		t.Fatal("plan without progress counted as standing")
	}
	done, err := p.Observe(domain.Observation{Action: action.ID(), Attempt: p.View().Attempt, Snapshot: snapshot, Tick: 20, Causality: domain.AfterDispatch, Effect: domain.EffectCompleted}, snapshot)
	if err != nil {
		t.Fatal(err)
	}
	if done.View().Stage != domain.Completed {
		t.Fatal(done.View())
	}
	if !completedBillPlan(store.PlanState{Spec: spec, Progress: []domain.Progress{done}}) {
		t.Fatal("completed bill not counted as standing")
	}
}
