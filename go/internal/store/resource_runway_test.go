package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A day of observed consumption with no recurring spend reads a known zero
// rate: steel below its reserve is a deficit, every targeted resource gets a
// row, and the review persists them.
func TestResourceRunwayReserveDeficitAndReviewPersistence(t *testing.T) {
	ctx := context.Background()
	s, path, a := billStoreFixture(t)
	r := roundsRequest()
	r.Current = a.Snapshot
	r.Tick = 60012
	r.Policy.ResourceTargets = map[policy.Resource]int64{"Steel": 300, "ComponentIndustrial": 10}
	r.Facts.Resources = domain.Known([]policy.Amount{{Resource: "Steel", Count: 200}, {Resource: "ComponentIndustrial", Count: 20}})
	r.Facts.ResourceSurfaceOre = map[policy.Resource]domain.Fact[int64]{"Steel": domain.Known(int64(0)), "ComponentIndustrial": domain.Known(int64(0))}
	r.Facts.ResourceConsumption = domain.Known(policy.ResourceConsumption{WindowDays: 1, Recurring: map[policy.Resource]int64{}})
	out := reviewRounds(t, s, &r)
	if roundsGoal(t, out, policy.MaintainResource).Standard.Finding != domain.FindingUnmet {
		t.Fatal(out.Needs)
	}
	if len(out.Review.ResourceRunways) != 2 || out.Review.ResourceRunways[0].Resource != "ComponentIndustrial" {
		t.Fatal(out.Review.ResourceRunways)
	}
	if steel := out.Review.ResourceRunways[1]; steel.ConsumptionPerDay == nil || *steel.ConsumptionPerDay != 0 || steel.Deficit == nil || !*steel.Deficit || steel.DaysLeft != nil {
		t.Fatal(out.Review.ResourceRunways)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if deficit, k := loaded.ResourceRunwayState()[1].Deficit.Value(); !k || !deficit {
		t.Fatal(loaded.ResourceRunways)
	}
}
