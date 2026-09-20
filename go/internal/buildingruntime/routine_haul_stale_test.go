package buildingruntime

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func staleHaulGoal(t *testing.T, journal *store.Store, thing string) store.GoalState {
	t.Helper()
	ctx := context.Background()
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "p", Revision: domain.PlanRevision(^uint64(0))}
	g, err := domain.NewGoal("supplies", domain.AutopilotGoal, 3, snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreateGoal(ctx, g); err != nil {
		t.Fatal(err)
	}
	state, err := journal.ReviewGoal(ctx, g.ID, 0, snapshot, 10, domain.NeedDeficit, false)
	if err != nil {
		t.Fatal(err)
	}
	haul, err := domain.NewHaul("Thing_Human1", thing, "MedicineHerbal", domain.Cell{X: 1, Z: 1})
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewHaulAction("haul-plan-0", haul)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := domain.NewPlan("haul-plan", 1, []domain.Action{action})
	if err != nil {
		t.Fatal(err)
	}
	if state, err = journal.CommitGoalMethod(ctx, g.ID, state.Revision, "secure-supplies-0", plan); err != nil {
		t.Fatal(err)
	}
	return state
}

// A pending haul whose thing left the deficit's target list (ordinary work
// stored it) is cancelled so the goal can propose a fresh target; one whose
// thing is still targeted keeps blocking new proposals.
func TestCancelStaleHaulMethodsFreesTheGoalWhenTheThingIsNoLongerTargeted(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "stale.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	goal := staleHaulGoal(t, journal, "Thing_MedicineHerbal1")

	open, err := cancelStaleHaulMethods(ctx, journal, goal, []string{"Thing_MedicineHerbal1", "Thing_Other"}, 20, haulContract(6000))
	if err != nil || !open {
		t.Fatalf("still-targeted haul must stay open: open=%v err=%v", open, err)
	}
	open, err = cancelStaleHaulMethods(ctx, journal, goal, []string{"Thing_Other"}, 20, haulContract(6000))
	if err != nil || open {
		t.Fatalf("stale haul must be cancelled: open=%v err=%v", open, err)
	}
	plan, err := journal.LoadPlan(ctx, "haul-plan")
	if err != nil {
		t.Fatal(err)
	}
	if v := plan.Progress[0].View(); v.Stage != domain.Cancelled {
		t.Fatalf("stage = %s, want cancelled", v.Stage)
	}
}

// A haul that native keeps refusing (no storage accepts the thing) is
// cancelled once the same hold has persisted for the stall grace, but not
// before, so the goal's attempt count can reach its storage fallback.
func TestCancelStaleHaulMethodsCancelsLongNativeIneligibleHolds(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	journal, err := store.Open(ctx, filepath.Join(t.TempDir(), "stalled.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	goal := staleHaulGoal(t, journal, "Thing_MedicineHerbal1")
	targets := []string{"Thing_MedicineHerbal1"}
	for _, tick := range []domain.Tick{100, 3000, 5000} {
		if _, err = journal.Hold(ctx, "haul-plan", "haul-plan-0", []domain.HeldReason{domain.HeldNativeIneligible}, tick); err != nil {
			t.Fatal(err)
		}
	}
	open, err := cancelStaleHaulMethods(ctx, journal, goal, targets, 6000, haulContract(6000))
	if err != nil || !open {
		t.Fatalf("hold younger than the grace must stay open: open=%v err=%v", open, err)
	}
	open, err = cancelStaleHaulMethods(ctx, journal, goal, targets, 6100, haulContract(6000))
	if err != nil || open {
		t.Fatalf("hold older than the grace must be cancelled: open=%v err=%v", open, err)
	}
}

// The stall contracts under test, with the deadline the case names.
func haulContract(deadline int64) policy.ProgressContract {
	p := policy.DefaultRoutinePolicy()
	p.HaulStallTicks = deadline
	return p.HaulProgress()
}
func huntContract(deadline int64) policy.ProgressContract {
	p := policy.DefaultRoutinePolicy()
	p.HuntStallTicks = deadline
	return p.HuntProgress()
}
func harvestContract(deadline int64) policy.ProgressContract {
	p := policy.DefaultRoutinePolicy()
	p.AcquisitionStallTicks = deadline
	return p.AcquisitionProgress()
}
