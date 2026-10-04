package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A day of journal history with no charged production reads a known zero
// rate: steel below its reserve is a deficit, and the review persists it.
func TestResourceRunwayReserveDeficitAndReviewPersistence(t *testing.T) {
	ctx := context.Background()
	s, path, a := billStoreFixture(t)
	if _, err := s.Prepare(ctx, "plan", "bill", a.Snapshot, a.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "bill", a.Snapshot, a.Tick); err != nil {
		t.Fatal(err)
	}
	r := routineRequest()
	r.Current = a.Snapshot
	r.Tick = 60012
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 300}
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 200}, {Resource: "ComponentIndustrial", Count: 20}})
	r.Facts.ResourceSurfaceOre = map[policy.Resource]domain.Fact[int64]{"Steel": domain.Known(int64(0)), "ComponentIndustrial": domain.Known(int64(0))}
	out := reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainResource).Standard.Need != domain.NeedDeficit {
		t.Fatal(out.Needs)
	}
	if len(out.Review.ResourceRunways) != 3 {
		t.Fatal(out.Review.ResourceRunways)
	}
	if steel := out.Review.ResourceRunways[0]; steel.ConsumptionPerDay == nil || *steel.ConsumptionPerDay != 0 || steel.Deficit == nil || !*steel.Deficit || steel.DaysLeft != nil {
		t.Fatal(out.Review.ResourceRunways)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if deficit, k := loaded.ResourceRunwayState()[0].Deficit.Value(); !k || !deficit {
		t.Fatal(loaded.ResourceRunways)
	}
}
