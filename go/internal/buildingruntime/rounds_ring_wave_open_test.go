package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// An owner's open ring wave of a planned room (the shelter's 23 walls) must
// not hold the furniture raised on that room's interior (#2303): the cooking
// step reaches its own placement (here it then waits on the missing layout plan), and the ring's reconcile reads the open
// wave as already tried.
func TestOpenRingWaveDoesNotHoldTheFurnitureSlot(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	p, db, _ := cookingFixture(t)
	ctx := context.Background()
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	goal, workable, err := db.WorkableOwner(ctx, review, policy.EnsureCooking)
	if err != nil || !workable {
		t.Fatal(workable, err)
	}
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 1}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	id := domain.MintPlanID()
	action, err := domain.NewBuildingAction(domain.ActionID(string(id)+"-0"), wall)
	if err != nil {
		t.Fatal(err)
	}
	player := p.reviewer.player
	player.mu.Lock()
	epoch := player.epoch
	player.mu.Unlock()
	state := player.session.State()
	const room = "shelter-shell-1-1"
	if _, err := p.commitOwnerActions(ctx, epoch, state, goal, domain.MethodID(room+"-build-ab"), id, []domain.Action{action}); err != nil {
		t.Fatal(err)
	}
	goal, _, err = db.WorkableOwner(ctx, review, policy.EnsureCooking)
	if err != nil {
		t.Fatal(err)
	}
	if open, err := p.ringWaveOpen(ctx, goal); err != nil || !open {
		t.Fatalf("the open wave is not seen: open=%v err=%v", open, err)
	}
	result, err := p.Step(ctx)
	if err != nil || result.Verdict == BuildingReasonExistingWork {
		t.Fatalf("the campfire waited behind the ring wave: %+v err=%v", result.Verdict, err)
	}
	// The store admits the slot's method beside the open ring wave, but not on
	// a cell the wave builds on, and not a second ring wave.
	commit := func(method string, cell domain.Cell) error {
		b, err := domain.NewBuilding("Campfire", cell, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		pid := domain.MintPlanID()
		a, err := domain.NewBuildingAction(domain.ActionID(string(pid)+"-0"), b)
		if err != nil {
			t.Fatal(err)
		}
		_, err = p.commitOwnerActions(ctx, epoch, state, goal, domain.MethodID(method), pid, []domain.Action{a})
		return err
	}
	if err := commit("shelter-shell-1-1-build-cd", domain.Cell{X: 3, Z: 3}); err == nil {
		t.Fatal("a second ring wave was admitted beside the open one")
	}
	if err := commit("campfire", domain.Cell{X: 1, Z: 1}); err == nil {
		t.Fatal("furniture was admitted on a cell the open wave builds on")
	}
	if err := commit("campfire", domain.Cell{X: 3, Z: 3}); err != nil {
		t.Fatalf("furniture waited behind the ring wave: %v", err)
	}
}

// A cold map's cooking campfire stands on the shelter's template slot, not the
// planned kitchen's interior; on a normal map the kitchen hosts it (#2303).
func TestColdCookingCampfireIsNotHostedByTheKitchen(t *testing.T) {
	t.Parallel()
	r := &RoundsBuildingPlanner{concern: policy.EnsureCooking, definition: "Campfire"}
	for _, cold := range []bool{true, false} {
		facts := observation.ColonyProjection{LayoutPlan: domain.Known(policy.LayoutPlan{Cold: cold})}
		if got := r.hostsInPlannedRoom(policy.PlannedKitchen, facts); got == cold {
			t.Fatalf("cold=%v: kitchen hosts the campfire = %v", cold, got)
		}
	}
}
