package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"testing"
)

func TestCompletedBillConsumptionAndReviewPersistence(t *testing.T) {
	ctx := context.Background()
	s, path, a := billStoreFixture(t)
	if _, err := s.PrepareBill(ctx, "plan", "bill", a); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", a.Snapshot, a.Tick); err != nil {
		t.Fatal(err)
	}
	steel, components := int64(100), int64(10)
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "bill", Attempt: 1, Snapshot: a.Snapshot, Tick: 13, Effect: domain.EffectCompleted, BillConsumption: &domain.BillConsumption{Steel: &steel, Components: &components}}, a.Snapshot); err != nil {
		t.Fatal(err)
	}
	r := routineRequest()
	r.Current = a.Snapshot
	r.Tick = 60012
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 200}, {Resource: "ComponentIndustrial", Count: 20}})
	r.Facts.ResourceSurfaceOre = map[policy.Resource]domain.Fact[int64]{"Steel": domain.Known(int64(0)), "ComponentIndustrial": domain.Known(int64(0))}
	out := reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedDeficit {
		t.Fatal(out.Needs)
	}
	if len(out.Review.ResourceRunways) != 3 || out.Review.ResourceRunways[0].DaysLeft == nil || *out.Review.ResourceRunways[0].DaysLeft != 2 {
		t.Fatal(out.Review.ResourceRunways)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if n, k := loaded.ResourceRunwayState()[0].DaysLeft.Value(); !k || n != 2 {
		t.Fatal(loaded.ResourceRunways)
	}
}
