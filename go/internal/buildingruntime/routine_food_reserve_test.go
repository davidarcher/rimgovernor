package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestReserveUsesLiveDeliveryAndConfiguredDays(t *testing.T) {
	r := &RoutineReviewer{policy: policy.DefaultRoutinePolicy()}
	r.policy.FoodReserveDays = 2
	supply := policy.FoodSupply{Complete: domain.Known(true), Consumers: []policy.FoodConsumer{{ID: "pawn", NutritionPerDay: domain.Known(1.0)}}, Stocks: []policy.FoodStock{
		{ID: "ordinary", DefName: "MealSimple", Holder: domain.Known(policy.PawnID("")), Nutrition: domain.Known(1.0), Eaters: []policy.PawnID{"pawn"}, Perishable: domain.Known(false)},
		{ID: "reserve", DefName: "Pemmican", Reserve: true, Roofed: domain.Known(true), Holder: domain.Known(policy.PawnID("")), Nutrition: domain.Known(2.0), Eaters: []policy.PawnID{"pawn"}, Perishable: domain.Known(false)},
	}}
	for _, tc := range []struct {
		name    string
		plan    domain.Fact[policy.FoodPlan]
		release bool
	}{
		{"no delivery", domain.Known(policy.FoodPlan{}), true},
		{"timely hunt", domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Decision: policy.FoodPlanOpen, DeliveredPerDay: 1, Channel: policy.FoodChannel{Kind: policy.FoodHunt, LeadDays: domain.Known(0.0)}}}}), false},
		{"late crop", domain.Known(policy.FoodPlan{Portfolio: []policy.FoodPlanEntry{{Decision: policy.FoodPlanHold, DeliveredPerDay: 1, Channel: policy.FoodChannel{Kind: policy.FoodCrop, LeadDays: domain.Known(4.0)}}}}), true},
		{"unknown plan", domain.Unknown[policy.FoodPlan](), false},
		{"unknown source", domain.Known(policy.FoodPlan{Unknown: []policy.FoodPlanEntry{{}}}), false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			p := observation.ColonyProjection{FoodSupply: domain.Known(supply), Facts: policy.RoutineFacts{FoodPlan: tc.plan}}
			r.reviewReserve(&p)
			reserve, known := p.Facts.FoodReserve.Value()
			if !known || reserve.TargetNutrition != 2 || (len(reserve.Release) > 0) != tc.release {
				t.Fatal(reserve, known)
			}
		})
	}
}

func TestReserveRefillUsesStorageGoal(t *testing.T) {
	r, _, _, _, _ := routineFixture(t)
	planner, err := NewRoutineBillPlanner(r, &gearProductionNative{}, policy.PreserveFood)
	if err != nil || planner.need != policy.MaintainFoodStorage {
		t.Fatal(planner, err)
	}
}

func TestReserveAccessCommitsSupplyActions(t *testing.T) {
	ctx := context.Background()
	r, db, _, _, native := routineFixture(t)
	if _, err := r.Step(ctx); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	_, err = db.ReviewRoutine(ctx, store.RoutineReviewRequest{Revision: review.Revision, Current: review.Snapshot, Tick: review.Tick, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{FoodStorageUpkeep: policy.FoodStorageStocks(policy.FoodSupply{Stocks: []policy.FoodStock{{ID: "hold", DefName: "Pemmican"}, {ID: "release", DefName: "MealSurvivalPack"}}}), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false), FoodReserve: domain.Known(policy.FoodReserveReview{Emergency: true, Hold: []string{"hold"}, Release: []string{"release"}})}})
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainFoodStorage {
			continue
		}
		goal, err := db.LoadGoal(ctx, binding.Goal)
		if err != nil {
			t.Fatal(err)
		}
		bill, _ := domain.NewProductionBill("stove", "MakePemmican", "token", domain.FoodTarget, 100)
		billAction, _ := domain.NewProductionBillAction("refill", bill)
		billPlan, _ := domain.NewPlan("refill-plan", 1, []domain.Action{billAction})
		goal, err = db.CommitGoalMethod(ctx, goal.Goal.ID, goal.Revision, "refill", billPlan)
		if err != nil {
			t.Fatal(err)
		}
		v := proto.Clone(native.reply.GetObserved()).(*o.ColonyFactsSnapshot)
		v.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: &o.FoodSupplyFacts{Stocks: []*o.FoodStock{
			{Item: &o.EntityRef{Id: proto.String("hold"), DefName: proto.String("Pemmican"), Position: &c.Cell{X: proto.Int32(1), Z: proto.Int32(2)}}},
			{Item: &o.EntityRef{Id: proto.String("release"), DefName: proto.String("MealSurvivalPack"), Position: &c.Cell{X: proto.Int32(3), Z: proto.Int32(4)}}},
		}}}}
		planner := &RoutineFoodStorageUpkeepPlanner{reviewer: r, native: &reserveSupplyNative{context: v.Context}}
		call, epoch, done, err := r.player.enter(ctx, false)
		if err != nil {
			t.Fatal(err)
		}
		defer done()
		result, err := planner.admitReserve(call, epoch, goal, v, policy.FoodReserveReview{Hold: []string{"hold"}, Release: []string{"release"}})
		if err != nil {
			t.Fatal(err)
		}
		plan, err := db.LoadPlan(ctx, result.Plan)
		if err != nil || len(plan.Progress) != 2 {
			t.Fatal(plan, err)
		}
		for i, progress := range plan.Progress {
			supply, ok := progress.Action().SupplyAllow()
			if !ok || supply.Forbidden() != (i == 1) || progress.View().Stage != domain.Pending {
				t.Fatal(progress)
			}
		}
		return
	}
	t.Fatal("storage goal absent")
}

type reserveSupplyNative struct {
	RoutineFoodStorageUpkeepSource
	context *c.ObservationContext
}

func (n *reserveSupplyNative) ReadFoodReserveSupplies(_ context.Context, _ *c.Identity, forbid bool) (bridge.SupplyRead, bridge.Result, error) {
	supply, _ := domain.NewSupplyAllow("release", "MealSurvivalPack", domain.Cell{X: 3, Z: 4})
	if forbid {
		supply, _ = domain.NewSupplyForbid("hold", "Pemmican", domain.Cell{X: 1, Z: 2})
	}
	return bridge.SupplyRead{Context: n.context, Targets: []bridge.SupplyTarget{{Supply: supply}}}, bridge.Result{}, nil
}
