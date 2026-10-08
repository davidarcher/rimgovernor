package store

import (
	"context"

	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Food facility work remains available during combat.
func TestFoodFacilityAdmittedDuringEmergency(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoundsRequest()
	r.Facts.Hostiles = domain.Known(int64(2))
	out := reviewRounds(t, s, &r)
	if len(out.Review.Emergency) == 0 {
		t.Fatal("hostiles raised no emergency")
	}
	g := roundsGoal(t, out, policy.EnsureFoodSupply)
	if _, workable, err := s.Workable(ctx, out.Review, policy.EnsureFoodSupply); err != nil || !workable {
		t.Fatal("food is workable under an emergency", workable, err)
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "butcher-spot", butcherSpotPlan(t, "spot-plan-1")); err != nil {
		t.Fatal("food facility blocked during emergency", err)
	}
}
