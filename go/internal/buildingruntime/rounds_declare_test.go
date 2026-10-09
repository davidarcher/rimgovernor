package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// spyDeclarer records what a planner declares during a review.
type spyDeclarer struct {
	inner  OrderDeclarer
	got    policy.Declared
	err    error
	called bool
}

func (s *spyDeclarer) DeclareOrders(ctx context.Context, snapshot domain.GenerationSnapshot, projection observation.ColonyProjection, benches []policy.GearBench) (policy.Declared, error) {
	s.got, s.err = s.inner.DeclareOrders(ctx, snapshot, projection, benches)
	s.called = true
	return s.got, s.err
}

// declaredOrders registers the planner and returns what its next review
// declares.
func declaredOrders(t *testing.T, reviewer *Rounder, planner OrderDeclarer) policy.Declared {
	t.Helper()
	spy := &spyDeclarer{inner: planner}
	reviewer.AddOrderDeclarer(spy)
	if _, err := reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	if !spy.called || spy.err != nil {
		t.Fatal("the review did not declare", spy.called, spy.err)
	}
	return spy.got
}

// orderFor is the declared order making recipe.
func orderFor(declared policy.Declared, recipe string) (policy.OrderSpec, bool) {
	for _, order := range declared.Orders {
		if order.Recipe == recipe {
			return order, true
		}
	}
	return policy.OrderSpec{}, false
}
