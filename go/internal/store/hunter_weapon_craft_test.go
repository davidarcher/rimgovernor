package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// While an emergency stands, EnsureFoodSupply (priority 2, so no emergency
// need) is suspended like any routine work, except for the hunters' weapon
// craft: a gear-batch bill under it. Its other methods stay refused.
func TestHunterWeaponCraftSurvivesEmergencySuspension(t *testing.T) {
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
	if _, workable, err := s.Workable(ctx, out.Review, policy.EnsureFoodSupply); err != nil || workable {
		t.Fatal("food is workable under an emergency", workable, err)
	}
	if _, workable, err := s.WorkableHunterWeapons(ctx, out.Review); err != nil || !workable {
		t.Fatal("the hunters' weapon craft is suspended", workable, err)
	}
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "butcher-spot", butcherSpotPlan(t, "spot-plan-1")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("another food method was admitted under the emergency", err)
	}
	bill, err := domain.NewProductionBill("bench", "Make_Bow_Short", domain.GearBatch, 1)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("bow-plan-0", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("bow-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "bow", plan); err != nil {
		t.Fatal("the hunters' weapon craft was vetoed", err)
	}
}
