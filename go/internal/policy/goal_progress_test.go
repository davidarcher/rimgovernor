package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGoalProgressDesignationWithoutWorkerIsBlocked(t *testing.T) {
	c := DefaultRoutinePolicy().AcquisitionProgress()
	p := ReviewGoalProgress(GoalProgress{}, EnsureFoodSupply, c, ProgressEvidence{Open: true, Dispatched: true, WorkerAvailable: domain.Known(true)}, 1000)
	if p.LastProgress != 1000 || p.NextReview != 1000+c.Deadline || p.Blocked != "" || p.Method != "harvest" {
		t.Fatalf("fresh record %+v", p)
	}
	// The designation stays issued for a day; nobody capable is available.
	p = ReviewGoalProgress(p, EnsureFoodSupply, c, ProgressEvidence{Open: true, Dispatched: true, WorkerAvailable: domain.Known(false)}, 30000)
	if p.Blocked != BlockedNoWorker || p.LastProgress != 1000 || p.NextReview != 1000+c.Deadline {
		t.Fatalf("issued order with no capable worker must be blocked, not progressing: %+v", p)
	}
	// Dispatch alone is not progress either: an unknown census leaves the clock running.
	p = ReviewGoalProgress(p, EnsureFoodSupply, c, ProgressEvidence{Open: true, Dispatched: true}, 40000)
	if p.Blocked != "" || p.LastProgress != 1000 {
		t.Fatalf("dispatch is not progress: %+v", p)
	}
	// A settled native outcome is.
	p = ReviewGoalProgress(p, EnsureFoodSupply, c, ProgressEvidence{Open: true, Dispatched: true, Advanced: true, WorkerAvailable: domain.Known(false)}, 50000)
	if p.Blocked != "" || p.LastProgress != 50000 || p.NextReview != 50000+c.Deadline {
		t.Fatalf("native outcome resets the deadline: %+v", p)
	}
	// A deficit that shrank since the last review is native progress too.
	p = ReviewGoalProgress(p, EnsureFoodSupply, c, ProgressEvidence{Observed: domain.Known(0.8)}, 60000)
	p = ReviewGoalProgress(p, EnsureFoodSupply, c, ProgressEvidence{Observed: domain.Known(0.5)}, 70000)
	if p.LastProgress != 70000 || p.Blocked != "" {
		t.Fatalf("shrinking deficit is progress: %+v", p)
	}
	p = ReviewGoalProgress(p, EnsureFoodSupply, c, ProgressEvidence{Observed: domain.Known(0.5)}, 80000)
	if p.LastProgress != 70000 || p.Blocked != BlockedNoMethod {
		t.Fatalf("no method and no movement: %+v", p)
	}
	if err := ValidateGoalProgress(p, 80000); err != nil {
		t.Fatal(err)
	}
}

func TestGoalProgressDeadlineRotatesMethodWithCooldown(t *testing.T) {
	policy := DefaultRoutinePolicy()
	hunt := policy.HuntProgress()
	forage := policy.AcquisitionProgress()
	p := ReviewGoalProgress(GoalProgress{}, EnsureFoodSupply, hunt, ProgressEvidence{Open: true, Dispatched: true}, 0)
	if _, rotated := ExpireGoalProgress(p, hunt, hunt.Deadline-1, "hunt/Deer1/no_worker", []ProgressContract{forage}); rotated {
		t.Fatal("rotated before the deadline")
	}
	next, rotated := ExpireGoalProgress(p, hunt, hunt.Deadline, "hunt/Deer1/no_worker", []ProgressContract{hunt, forage})
	if !rotated || next.Method != "harvest" || next.Expected != forage.Expected || next.LastProgress != hunt.Deadline || next.NextReview != hunt.Deadline+forage.Deadline || next.Blocked != "" {
		t.Fatalf("rotation %+v", next)
	}
	if !next.Cooled("hunt/Deer1/no_worker", hunt.Deadline) || len(next.Cooldowns) != 1 || next.Cooldowns[0].Until != hunt.Deadline+hunt.Deadline {
		t.Fatalf("cooldown %+v", next.Cooldowns)
	}
	// The cooldown is bounded: it lifts once Until passes.
	if next.Cooled("hunt/Deer1/no_worker", next.Cooldowns[0].Until) {
		t.Fatal("cooldown outlived its bound")
	}
	// The review drops the expired cooldown and keeps the rotated method.
	p = ReviewGoalProgress(next, EnsureFoodSupply, forage, ProgressEvidence{Open: true}, next.Cooldowns[0].Until)
	if len(p.Cooldowns) != 0 || p.Method != "harvest" || p.LastProgress != hunt.Deadline {
		t.Fatalf("expired cooldown kept: %+v", p)
	}
	// Every alternative cooled: the method stays, blocked on cooldown, with a fresh deadline.
	cooled := GoalProgress{Goal: EnsureFoodSupply, Method: hunt.Method, NextReview: 10, Cooldowns: []ProgressCooldown{{Key: "harvest", Until: 1000}}}
	stuck, rotated := ExpireGoalProgress(cooled, hunt, 10, "hunt/Deer1", []ProgressContract{forage})
	if !rotated || stuck.Method != "hunt" || stuck.Blocked != BlockedCooldown || stuck.NextReview != 10+hunt.Deadline {
		t.Fatalf("no alternative: %+v", stuck)
	}
	// An unknown write outcome is reconciled by its action identity first: no rotation.
	pending := ReviewGoalProgress(p, EnsureFoodSupply, forage, ProgressEvidence{Open: true, Dispatched: true, Unresolved: true}, p.NextReview)
	if pending.Blocked != BlockedReconciling {
		t.Fatalf("unresolved write %+v", pending)
	}
	if _, rotated := ExpireGoalProgress(pending, forage, pending.NextReview+1, "harvest/plant", []ProgressContract{hunt}); rotated {
		t.Fatal("rotated past an unreconciled write")
	}
	// The cooldown bound holds however long the contract asks.
	long := ProgressContract{Method: "hunt", Deadline: 10, Cooldown: 10 * ProgressCooldownMax}
	bounded, _ := ExpireGoalProgress(GoalProgress{Goal: EnsureFoodSupply, Method: "hunt", NextReview: 10}, long, 10, "k", nil)
	if bounded.Cooldowns[0].Until != 10+ProgressCooldownMax {
		t.Fatalf("unbounded cooldown %+v", bounded.Cooldowns)
	}
	if err := ValidateGoalProgress(bounded, 10); err != nil {
		t.Fatal(err)
	}
}

func TestGoalProgressCooldownLiftsWhenConditionChanges(t *testing.T) {
	hunt := DefaultRoutinePolicy().HuntProgress()
	p := GoalProgress{Goal: EnsureFoodSupply, Method: "hunt", NextReview: 100}
	p, _ = ExpireGoalProgress(p, hunt, 100, CooldownKey("hunt", "Deer1", string(BlockedNoWorker)), nil)
	if !p.Cooled(CooldownKey("hunt", "Deer1", string(BlockedNoWorker)), 200) {
		t.Fatal("failed situation not cooled")
	}
	// A hunter arrived: the same prey under a different condition is a different key.
	if p.Cooled(CooldownKey("hunt", "Deer1", ""), 200) {
		t.Fatal("cooldown outlived the condition it was keyed to")
	}
	// Another prey was never cooled.
	if p.Cooled(CooldownKey("hunt", "Deer2", string(BlockedNoWorker)), 200) {
		t.Fatal("cooldown leaked to another situation")
	}
}

func TestFoodProgressSurfacesCookingPrerequisiteAndWithholdsBuilder(t *testing.T) {
	gates := FootholdGates{Food: domain.Known(true), Cooking: domain.Known(false), Storage: domain.Known(true)}
	c, prerequisite, observed := FoodProgress(gates, RoutineFacts{FoodDays: domain.Known(3.5)}, DefaultRoutinePolicy())
	if v, known := observed.Value(); c.Method != "cook" || prerequisite != EnsureCooking || !known || v != 0.5 {
		t.Fatalf("cook rung %+v %s %v", c, prerequisite, observed)
	}
	gates.Food = domain.Known(false)
	c, prerequisite, _ = FoodProgress(gates, RoutineFacts{}, DefaultRoutinePolicy())
	if c.Method != "acquire" || prerequisite != EnsureCooking {
		t.Fatalf("acquire rung keeps the bench prerequisite %+v %s", c, prerequisite)
	}
	gates.Food, gates.Cooking, gates.Storage = domain.Known(true), domain.Known(true), domain.Known(false)
	if c, prerequisite, _ = FoodProgress(gates, RoutineFacts{}, DefaultRoutinePolicy()); c.Method != "store" || prerequisite != EnsureFoodStorage {
		t.Fatalf("store rung %+v %s", c, prerequisite)
	}
	gates.Storage = domain.Known(true)
	if c, prerequisite, _ = FoodProgress(gates, RoutineFacts{}, DefaultRoutinePolicy()); c.Method != "grow" || prerequisite != "" {
		t.Fatalf("grow rung %+v %s", c, prerequisite)
	}
	food := ReviewGoalProgress(GoalProgress{}, EnsureFoodSupply, ProgressContract{Method: "cook", Deadline: 10}, ProgressEvidence{Prerequisite: EnsureCooking}, 5)
	if food.Blocked != BlockedPrerequisite(EnsureCooking) || food.Blocked.Prerequisite() != EnsureCooking {
		t.Fatalf("prerequisite not surfaced: %+v", food)
	}
	withheld := WithheldLabor([]GoalProgress{food, {Goal: MaintainWood}})
	if len(withheld) != 1 || withheld[0] != WorkConstruction {
		t.Fatalf("withheld %v", withheld)
	}
	// One builder, withheld for the bench: the ranked construction goal
	// reads labor_unavailable while the plant-cutting goal is selected.
	r := developmentFixture()
	r.Limit = 2
	r.Labor = domain.Known(map[WorkType]int{WorkConstruction: 1, WorkPlantCutting: 1})
	r.Goals = []DevelopmentGoal{
		{ID: EnsureBasicDefense, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(1.0), Labor: LaborProfile{WorkConstruction}},
		{ID: MaintainWood, Source: AutopilotGoal, Priority: 3, Deficit: domain.Known(.5), Labor: LaborProfile{WorkPlantCutting}},
	}
	r.Withheld = withheld
	s := rank(t, r)
	for _, row := range s.Rows {
		switch row.Goal {
		case EnsureBasicDefense:
			if row.Selected || row.Reason != DevelopmentLabor || row.Bottleneck != WorkConstruction {
				t.Fatalf("builder diverted: %+v", row)
			}
		case MaintainWood:
			if !row.Selected {
				t.Fatalf("wood not selected: %+v", row)
			}
		}
	}
	r.Withheld = nil
	if s = rank(t, r); len(selected(s)) != 2 {
		t.Fatalf("without the hold both select: %+v", s.Rows)
	}
}
