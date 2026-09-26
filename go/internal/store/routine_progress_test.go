package store

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func progressRecord(t *testing.T, r RoutineReview, id domain.GoalID) policy.GoalProgress {
	t.Helper()
	p, ok := r.GoalProgress(id)
	if !ok {
		t.Fatal("missing progress record", id, r.Progress)
	}
	return p
}

// The food goal's record surfaces a known-missing cooking bench as its
// prerequisite and withholds the one builder from optional development
// until it stands (#629); the record persists with the review.
func TestRoutineProgressFoodPrerequisiteWithholdsBuilder(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	r.Policy.MaxDevelopmentProjects = 4
	r.Facts.Workers = domain.Known(3)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkPlantCutting: 1})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(2)), domain.Known(int64(3))
	r.Facts.FoodDays = domain.Known(1.0)
	r.Facts.Cooking = domain.Known(false)
	first := reviewRoutine(t, s, &r)
	food := progressRecord(t, first.Review, policy.EnsureFoodSupply)
	// A fresh colony reviews at Foothold, where StageGoalStallScale cuts the
	// deadline to one in-game hour so a stuck rung rotates quickly, not
	// after a full day.
	if food.Method != "acquire" || food.Blocked != policy.BlockedPrerequisite(policy.EnsureCooking) || food.LastProgress != 10 || food.NextReview != 10+policy.DevelopmentStallTicks/24 || food.Expected == "" {
		t.Fatalf("food record %+v", food)
	}
	expansion := developmentRow(t, first.Review, policy.EnsureExpansion)
	if expansion.Selected || expansion.Reason != policy.DevelopmentLabor || expansion.Bottleneck != policy.WorkConstruction {
		t.Fatalf("builder diverted while the bench is owed: %+v", expansion)
	}
	if !developmentRow(t, first.Review, policy.MaintainWood).Selected {
		t.Fatal(first.Review.Development.Rows)
	}
	for _, binding := range first.Review.Goals {
		if _, ok := first.Review.GoalProgress(binding.Need); !ok && routineGoal(t, first, binding.Need).Goal.Status == domain.GoalActive {
			t.Fatal("active goal without a progress record", binding.Need)
		}
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	// The bench stands: the prerequisite clears and the builder is free.
	r.Facts.Cooking = domain.Known(true)
	second := reviewRoutine(t, s, &r)
	if food = progressRecord(t, second.Review, policy.EnsureFoodSupply); food.Blocked != policy.BlockedNoMethod || food.LastProgress != 10 {
		t.Fatalf("food record %+v", food)
	}
	if !developmentRow(t, second.Review, policy.EnsureExpansion).Selected {
		t.Fatal(second.Review.Development.Rows)
	}
	// A shrinking deficit is native progress and resets the clock; a
	// disabled review keeps the records.
	r.Facts.FoodDays = domain.Known(2.0)
	third := reviewRoutine(t, s, &r)
	if food = progressRecord(t, third.Review, policy.EnsureFoodSupply); food.LastProgress != third.Review.Tick {
		t.Fatalf("food record %+v", food)
	}
	r.Enabled = false
	stopped := reviewRoutine(t, s, &r)
	if !reflect.DeepEqual(stopped.Review.Progress, third.Review.Progress) {
		t.Fatal(stopped.Review.Progress)
	}
	// A planner keys a failed target out for a bounded cooldown under the
	// review's revision; a stale revision conflicts.
	r.Enabled = true
	fourth := reviewRoutine(t, s, &r)
	until := fourth.Review.Tick + 10*policy.ProgressCooldownMax
	cooled, err := s.RecordProgressCooldown(ctx, fourth.Review.Revision, policy.EnsureFoodSupply, policy.CooldownKey("harvest", "Plant_Berry1"), until)
	if err != nil {
		t.Fatal(err)
	}
	food = progressRecord(t, cooled, policy.EnsureFoodSupply)
	if !food.Cooled(policy.CooldownKey("harvest", "Plant_Berry1"), fourth.Review.Tick) || food.Cooldowns[0].Until != fourth.Review.Tick+policy.ProgressCooldownMax {
		t.Fatalf("cooldown %+v", food.Cooldowns)
	}
	if _, err = s.RecordProgressCooldown(ctx, fourth.Review.Revision-1, policy.EnsureFoodSupply, "k", until); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	fifth := reviewRoutine(t, s, &r)
	if food = progressRecord(t, fifth.Review, policy.EnsureFoodSupply); !food.Cooled(policy.CooldownKey("harvest", "Plant_Berry1"), fifth.Review.Tick) {
		t.Fatalf("cooldown lost across the review: %+v", food)
	}
}

// A goal whose only open order is a dispatched designation no available
// pawn is capable of reads blocked:no_worker, and dispatch alone never
// advances its clock.
func TestRoutineProgressDesignationWithoutWorkerIsBlocked(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	first := reviewRoutine(t, s, &r)
	wood := progressRecord(t, first.Review, policy.MaintainWood)
	if wood.Blocked != policy.BlockedNoMethod || wood.Method != "assess" {
		t.Fatalf("wood record %+v", wood)
	}
	g := routineGoal(t, first, policy.MaintainWood)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "cut-0123456789abcdef", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	target := scope()
	target.Plan = "wood"
	if _, err := s.ReserveAndPrepare(ctx, "wood", "wood-action", Admission{Snapshot: target, Tick: r.Tick, Costs: []MaterialCost{{Definition: "Steel", Count: 1}}, Footprint: []domain.Cell{{X: 3, Z: 7}}}); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "wood", "wood-action", target, r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.RecordReceipt(ctx, "wood", "wood-action", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	r.Tick += 3000
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1})
	second := reviewRoutine(t, s, &r)
	wood = progressRecord(t, second.Review, policy.MaintainWood)
	// Still Foothold: the one-hour deadline applies here too.
	if wood.Blocked != policy.BlockedNoWorker || wood.Method != "cut" || wood.LastProgress != second.Review.Tick || wood.NextReview != second.Review.Tick+policy.DevelopmentStallTicks/24 {
		t.Fatalf("issued cut with no plant cutter must be blocked: %+v", wood)
	}
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkPlantCutting: 1})
	third := reviewRoutine(t, s, &r)
	if wood = progressRecord(t, third.Review, policy.MaintainWood); wood.Blocked != "" || wood.LastProgress != second.Review.Tick {
		t.Fatalf("a cutter arrived, the order is not yet progress: %+v", wood)
	}
}
