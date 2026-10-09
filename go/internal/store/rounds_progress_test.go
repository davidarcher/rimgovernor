package store

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func progressRecord(t *testing.T, r Rounds, id domain.ConcernID) policy.ConcernProgress {
	t.Helper()
	p, ok := r.ConcernProgress(id)
	if !ok {
		t.Fatal("missing progress record", id, r.Progress)
	}
	return p
}

// The food goal's record surfaces a known-missing cooking bench as its
// prerequisite; the record persists with the review. With no
// cooking method in flight nobody is building the bench, so the one builder
// is not withheld from optional development (a withheld builder waiting on
// work nobody proposed deadlocked Foothold).
func TestRoundsProgressFoodPrerequisiteWithholdsBuilder(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	r.Policy.Stage.Floor = policy.StageReserves
	r.Facts.Workers = domain.Known(3)
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkPlantCutting: 1})
	r.Facts.Colonists, r.Facts.IndoorCapacity, r.Facts.BedCapacity = domain.Known(int64(3)), domain.Known(int64(3)), domain.Known(int64(3))
	r.Facts.FoodDays = domain.Known(1.0)
	r.Facts.Cooking = domain.Known(false)
	first := reviewRounds(t, s, &r)
	food := progressRecord(t, first.Review, policy.EnsureFoodSupply)
	// The Reserves floor raises expansion and the resource floors and keeps
	// the full-day stall deadline (StageGoalStallScale cuts only Foothold's).
	if food.Method != "acquire" || food.Blocked != policy.BlockedPrerequisite(policy.EnsureCooking) || food.LastProgress != 10 || food.NextReview != 10+domain.TicksPerDay || food.Expected == "" {
		t.Fatalf("food record %+v", food)
	}
	for _, binding := range first.Review.Standards {
		if _, ok := first.Review.ConcernProgress(binding.Concern); !ok && roundsGoal(t, first, binding.Concern).Standard.Status == domain.StandardOpen {
			t.Fatal("active goal without a progress record", binding.Concern)
		}
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(loaded, first.Review) {
		t.Fatal(loaded, err)
	}
	// The bench stands: the prerequisite clears and the builder is free.
	r.Facts.Cooking = domain.Known(true)
	second := reviewRounds(t, s, &r)
	if food = progressRecord(t, second.Review, policy.EnsureFoodSupply); food.Blocked != policy.BlockedNoMethod || food.LastProgress != 10 {
		t.Fatalf("food record %+v", food)
	}
	// A shrinking deficit is native progress and resets the clock; a
	// disabled review keeps the records.
	r.Facts.FoodDays = domain.Known(2.0)
	third := reviewRounds(t, s, &r)
	if food = progressRecord(t, third.Review, policy.EnsureFoodSupply); food.LastProgress != third.Review.Tick {
		t.Fatalf("food record %+v", food)
	}
	r.Enabled = false
	stopped := reviewRounds(t, s, &r)
	if !reflect.DeepEqual(stopped.Review.Progress, third.Review.Progress) {
		t.Fatal(stopped.Review.Progress)
	}
	// A planner keys a failed target out for a bounded cooldown under the
	// review's revision; a stale revision conflicts.
	r.Enabled = true
	fourth := reviewRounds(t, s, &r)
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
	fifth := reviewRounds(t, s, &r)
	if food = progressRecord(t, fifth.Review, policy.EnsureFoodSupply); !food.Cooled(policy.CooldownKey("harvest", "Plant_Berry1"), fifth.Review.Tick) {
		t.Fatalf("cooldown lost across the review: %+v", food)
	}
}

// A goal whose only open order is a dispatched designation no available
// pawn is capable of reads blocked:no_worker, and dispatch alone never
// advances its clock.
func TestRoundsProgressDesignationWithoutWorkerIsBlocked(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	first := reviewRounds(t, s, &r)
	wood := progressRecord(t, first.Review, policy.MaintainResource)
	if wood.Blocked != policy.BlockedNoMethod || wood.Method != "assess" {
		t.Fatalf("wood record %+v", wood)
	}
	g := roundsGoal(t, first, policy.MaintainResource)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "cut-0123456789abcdef", plan(t, "wood", "wood-action")); err != nil {
		t.Fatal(err)
	}
	target := scope()
	target.Plan = "wood"
	if _, err := s.Prepare(ctx, "wood", "wood-action", target, r.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "wood", "wood-action", target, r.Tick); err != nil {
		t.Fatal(err)
	}
	// The applied intent is the order's settlement: the review that
	// sees it counts it as progress once, and the building stays open work
	// until the census shows it built.
	if _, err := s.RecordReceipt(ctx, "wood", "wood-action", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1})
	settled := reviewRounds(t, s, &r)
	r.Tick += 3000
	second := reviewRounds(t, s, &r)
	wood = progressRecord(t, second.Review, policy.MaintainResource)
	// Still Foothold: the six-hour deadline applies here too, so 3000 ticks
	// later the settlement's progress still stands.
	if wood.Blocked != policy.BlockedNoWorker || wood.Method != "cut" || wood.LastProgress != settled.Review.Tick || wood.NextReview != settled.Review.Tick+domain.TicksPerDay/4 {
		t.Fatalf("issued cut with no plant cutter must be blocked: %+v", wood)
	}
	r.Facts.Labor = domain.Known(map[policy.WorkType]int{policy.WorkConstruction: 1, policy.WorkPlantCutting: 1})
	third := reviewRounds(t, s, &r)
	if wood = progressRecord(t, third.Review, policy.MaintainResource); wood.Blocked != "" || wood.LastProgress != settled.Review.Tick {
		t.Fatalf("a cutter arrived, the order is not yet progress: %+v", wood)
	}
}
