package executor

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestExternalHoldsRecoverUnknownAttemptsAndSeparateWorlds(t *testing.T) {
	f, held := multiple(t)
	ctx := context.Background()
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), held.ID(), heldAdmission(f, held, 12)); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.Dispatch(ctx, f.plan.ID(), held.ID(), f.authority.Snapshot, 100); err != nil {
		t.Fatal(err)
	}
	if _, err := f.store.RecordReceipt(ctx, f.plan.ID(), held.ID(), 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	reopenExecutor(t, f)
	current := f.authority.Snapshot
	current.Plan = "other"
	got, err := ExternalHolds(ctx, f.store, current)
	if err != nil || len(got) != 1 || got[0].Action != held || !got[0].Progress.View().Unresolved || got[0].Costs[0].Count != 12 {
		t.Fatalf("holds=%v error=%v", got, err)
	}
	got[0].Costs[0].Count = 999
	got[0].Footprint[0].X = 999
	again, err := ExternalHolds(ctx, f.store, current)
	if err != nil || again[0].Costs[0].Count != 12 || again[0].Footprint[0].X == 999 {
		t.Fatal("mutable returned hold changed durable accounting")
	}
	current.Load = "replacement"
	if got, err = ExternalHolds(ctx, f.store, current); err != nil || len(got) != 0 {
		t.Fatalf("old world affected replacement: %v %v", got, err)
	}
	if got, err = ExternalHolds(ctx, f.store, f.authority.Snapshot); err != nil || len(got) != 0 {
		t.Fatalf("current plan leaked into external accounting: %v %v", got, err)
	}
}

type brokenCatalog struct {
	states []store.PlanState
	err    error
}

func (b brokenCatalog) LoadPlans(context.Context, int) ([]store.PlanState, error) {
	return b.states, b.err
}

func TestExternalHoldsRejectPartialOrMalformedCatalog(t *testing.T) {
	f, held := multiple(t)
	ctx := context.Background()
	if _, err := f.store.ReserveAndPrepare(ctx, f.plan.ID(), held.ID(), heldAdmission(f, held, 12)); err != nil {
		t.Fatal(err)
	}
	state, err := f.store.LoadPlan(ctx, f.plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	current := f.authority.Snapshot
	current.Plan = "other"
	for _, catalog := range []brokenCatalog{
		{err: errors.New("catalog limit exceeded")},
		{states: make([]store.PlanState, 257)},
		{states: []store.PlanState{state, state}},
	} {
		if got, err := ExternalHolds(ctx, catalog, current); err == nil || got != nil {
			t.Fatalf("invalid catalog accepted: %v", err)
		}
	}
	state.Admissions = nil
	if _, err := ExternalHolds(ctx, brokenCatalog{states: []store.PlanState{state}}, current); err == nil {
		t.Fatal("missing admission became empty resource hold")
	}
}

// A dispatched action of a kind that never records a building admission
// (supply, acquisition, policy, ...) holds no resources or cells, so another
// plan's construction must not be held on its behalf.
func TestExternalHoldsIgnoreDispatchedNonBuildingActions(t *testing.T) {
	f, _ := multiple(t)
	ctx := context.Background()
	supply, err := domain.NewSupplyAllow("Thing_WoodLog1", "WoodLog", domain.Cell{X: 5, Z: 5})
	if err != nil {
		t.Fatal(err)
	}
	allow, err := domain.NewSupplyAllowAction("allow", supply)
	if err != nil {
		t.Fatal(err)
	}
	other, err := domain.NewPlan("supply", 1, []domain.Action{allow})
	if err != nil {
		t.Fatal(err)
	}
	progress, err := domain.NewProgress(other, allow.ID())
	if err != nil {
		t.Fatal(err)
	}
	snapshot := f.authority.Snapshot
	snapshot.Plan, snapshot.Revision = other.ID(), other.Revision()
	if progress, err = progress.Prepare(snapshot, 100); err != nil {
		t.Fatal(err)
	}
	if progress, err = progress.MarkDispatched(snapshot, 100); err != nil {
		t.Fatal(err)
	}
	state := store.PlanState{Spec: other, Progress: []domain.Progress{progress}}
	current := f.authority.Snapshot
	current.Plan = "target"
	got, err := ExternalHolds(ctx, brokenCatalog{states: []store.PlanState{state}}, current)
	if err != nil || len(got) != 0 {
		t.Fatalf("dispatched supply action held construction: holds=%v err=%v", got, err)
	}
}
