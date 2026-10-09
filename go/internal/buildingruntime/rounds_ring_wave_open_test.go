package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// An owner's open ring wave of a planned room (the shelter's 23 walls) must
// not hold the furniture raised on that room's interior: the cooking
// step reaches its own placement (here it then waits on the missing layout plan), and the ring's reconcile reads the open
// wave as already tried.
func TestOpenRingWaveDoesNotHoldTheFurnitureSlot(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	// No planned room: the step has nothing to place, so the test commits the
	// furniture itself.
	p, db, _ := cookingFixtureAt(t, centreOn)
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
	// The store admits the slot's method and a second ring wave beside the open
	// one, but nothing on a cell the open wave builds on.
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
	if err := commit("shelter-shell-1-1-build-cd", domain.Cell{X: 5, Z: 5}); err != nil {
		t.Fatalf("a disjoint ring wave waited behind the open one: %v", err)
	}
	if err := commit("shelter-shell-1-1-build-ef", domain.Cell{X: 1, Z: 1}); err == nil {
		t.Fatal("a ring wave was admitted on a cell the open wave builds on")
	}
	if err := commit("campfire", domain.Cell{X: 1, Z: 1}); err == nil {
		t.Fatal("furniture was admitted on a cell the open wave builds on")
	}
	if goal, _, err = db.WorkableOwner(ctx, review, policy.EnsureCooking); err != nil {
		t.Fatal(err)
	}
	if err := commit("campfire", domain.Cell{X: 3, Z: 3}); err != nil {
		t.Fatalf("furniture waited behind the ring wave: %v", err)
	}
}

// The slot is admitted first and the ring wave after it: the store
// admits a ring wave beside the owner's open furniture on cells the furniture
// is not on, and still not a second open furnishing method or a ring wave on the
// slot's own cell.
func TestRingWaveIsAdmittedBesideOpenFurnitureSlot(t *testing.T) {
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
	player := p.reviewer.player
	player.mu.Lock()
	epoch := player.epoch
	player.mu.Unlock()
	state := player.session.State()
	commit := func(goal store.WorkOwner, method, def string, cell domain.Cell) error {
		b, err := domain.NewBuilding(def, cell, domain.North, "")
		if err != nil {
			t.Fatal(err)
		}
		id := domain.MintPlanID()
		a, err := domain.NewBuildingAction(domain.ActionID(string(id)+"-0"), b)
		if err != nil {
			t.Fatal(err)
		}
		_, err = p.commitOwnerActions(ctx, epoch, state, goal, domain.MethodID(method), id, []domain.Action{a})
		return err
	}
	if err := commit(goal, "campfire", "Campfire", domain.Cell{X: 3, Z: 3}); err != nil {
		t.Fatal(err)
	}
	goal, _, err = db.WorkableOwner(ctx, review, policy.EnsureCooking)
	if err != nil {
		t.Fatal(err)
	}
	if err := commit(goal, "shelter-shell-1-1-build-ab", "Wall", domain.Cell{X: 3, Z: 3}); err == nil {
		t.Fatal("a ring wave was admitted on the open furniture's cell")
	}
	if err := commit(goal, "shelter-shell-1-1-build-ab", "Wall", domain.Cell{X: 1, Z: 1}); err != nil {
		t.Fatalf("the ring wave waited behind the furniture slot: %v", err)
	}
}

// A cold map's cooking campfire stands on the shelter's template slot, not the
// planned kitchen's interior; on a normal map the kitchen hosts it.
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
