package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func butcherSpotPlan(t *testing.T, id domain.PlanID) domain.PlanSpec {
	t.Helper()
	b, err := domain.NewBuilding(butcherSpotDefinition, domain.Cell{X: 3, Z: 3}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction(domain.ActionID(string(id)+"-0"), b)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan(id, 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

// The butcher spot is committed under EnsureFoodSupply and, while it is
// still being built, blocks neither a foraging acquisition nor a field
// batch (#260); those in turn do not block a spot, a second spot waits for
// the first, and any other building keeps the ordinary rule.
func TestCommitButcherSpotExemptFromFieldAndAcquisitionOpenWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoutineRequest()
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	g, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "butcher-spot", butcherSpotPlan(t, "spot-plan-1"))
	if err != nil {
		t.Fatal(err)
	}
	if g, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "RawBerries")); err != nil {
		t.Fatal("pending butcher spot blocked an acquisition", err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	if _, err = s.PrepareAcquisition(ctx, "acquire-plan-1", "acquire-plan-1-a", AcquisitionAdmission{Snapshot: target, Tick: tick, Thing: "acq-RawBerries", SnapshotToken: "acq-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	if g, err = s.LoadGoal(ctx, g.Goal.ID); err != nil {
		t.Fatal(err)
	}
	if g, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "field-1", growingPlan(t, "field-plan-1", []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}})); err != nil {
		t.Fatal("pending butcher spot blocked a field batch", err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "butcher-spot-2", butcherSpotPlan(t, "spot-plan-2")); err == nil {
		t.Fatal("a pending butcher spot did not block a second one")
	}
	other, err := domain.NewBuilding("Campfire", domain.Cell{X: 6, Z: 6}, domain.North, "")
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewBuildingAction("campfire-plan-0", other)
	if err != nil {
		t.Fatal(err)
	}
	campfire, err := domain.NewPlan("campfire-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "campfire", campfire); err == nil {
		t.Fatal("open field work did not block an unrelated building")
	}
}

// A fresh spot is admitted over open field and foraging work alone.
func TestCommitButcherSpotOverOpenFieldWork(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoutineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	g, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "field-1", growingPlan(t, "field-plan-1", []domain.Cell{{X: 0, Z: 0}}))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "butcher-spot", butcherSpotPlan(t, "spot-plan-1")); err != nil {
		t.Fatal("open field work blocked the butcher spot", err)
	}
}

// A hunt-only plan is admitted over the food goal's open forage (#260); a
// second forage waits for the first, and an open hunt blocks the next hunt.
func TestCommitHuntOverOpenForage(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := foodDeficitRoutineRequest()
	tick := r.Tick
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.EnsureFoodSupply)
	g, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-1", acquisitionPlan(t, "acquire-plan-1", "RawBerries"))
	if err != nil {
		t.Fatal(err)
	}
	target := r.Current
	target.Plan, target.Revision = "acquire-plan-1", 1
	if _, err = s.PrepareAcquisition(ctx, "acquire-plan-1", "acquire-plan-1-a", AcquisitionAdmission{Snapshot: target, Tick: tick, Thing: "acq-RawBerries", SnapshotToken: "acq-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "acquire-plan-1", "acquire-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	if g, err = s.LoadGoal(ctx, g.Goal.ID); err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "acquire-2", acquisitionPlan(t, "acquire-plan-2", "RawAgave")); err == nil {
		t.Fatal("open forage did not block a second forage")
	}
	if g, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "hunt-1", acquisitionPlan(t, "hunt-plan-1", "Corpse_Hare")); err != nil {
		t.Fatal("open forage blocked a hunt", err)
	}
	target.Plan = "hunt-plan-1"
	if _, err = s.PrepareAcquisition(ctx, "hunt-plan-1", "hunt-plan-1-a", AcquisitionAdmission{Snapshot: target, Tick: tick, Thing: "acq-Corpse_Hare", SnapshotToken: "hunt-cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "hunt-plan-1", "hunt-plan-1-a", target, tick); err != nil {
		t.Fatal(err)
	}
	if g, err = s.LoadGoal(ctx, g.Goal.ID); err != nil {
		t.Fatal(err)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "hunt-2", acquisitionPlan(t, "hunt-plan-2", "Corpse_Deer")); err == nil {
		t.Fatal("an open hunt did not block the next hunt")
	}
}
