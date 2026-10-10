package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func craftingSpotPlan(t *testing.T, id domain.PlanID) domain.PlanSpec {
	t.Helper()
	b, err := domain.NewBuilding(policy.CraftingSpotDefinition, domain.Cell{X: 3, Z: 3}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(string(id)+"-0"), b, domain.TierExpand)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// The crafting spot is committed under MaintainEquipment beside the goal's
// open dressing, which runs on its own; a second spot action waits for the
// first, and any other building keeps the ordinary rule.
func TestCommitCraftingSpotBesideOpenEquipmentWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db := open(t, memoryPath(t))
	r := roundsRequest()
	r.Policy.Stage.Floor = policy.StageDevelopment
	r.Facts.Gear = domain.Known(policy.GearObservation{Pawns: []policy.GearPawn{gearDeficitPawn("a")}})
	g := roundsGoal(t, reviewRounds(t, db, &r), policy.MaintainEquipment)
	gear, err := domain.NewGearReplace("a", "one", "Parka")
	if err != nil {
		t.Fatal(err)
	}
	wear, err := domain.NewGearReplaceAction("wear-0", gear)
	if err != nil {
		t.Fatal(err)
	}
	dress, err := domain.NewPlan("gear-1", 1, []domain.Action{wear})
	if err != nil {
		t.Fatal(err)
	}
	if g, err = db.CommitMethod(ctx, g.Standard.ID, g.Revision, "dress", dress); err != nil {
		t.Fatal(err)
	}
	if g, err = db.CommitMethod(ctx, g.Standard.ID, g.Revision, "crafting-spot", craftingSpotPlan(t, "spot-plan-1")); err != nil {
		t.Fatal("open dressing blocked the crafting spot", err)
	}
	if _, err = db.CommitMethod(ctx, g.Standard.ID, g.Revision, "crafting-spot-2", craftingSpotPlan(t, "spot-plan-2")); err == nil {
		t.Fatal("a pending crafting spot did not block a second one")
	}
}
