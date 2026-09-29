package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
)

// clubBenchNative is the gear colony with a crafting spot hosting the club
// recipe and wood to fund it.
type clubBenchNative struct {
	*gearTestNative
}

func (n *clubBenchNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := policy.GearRecipe{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]policy.Amount{{{Resource: "WoodLog", Count: 40}}}), RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Crafting", Skill: "Crafting"}})}
	return []bridge.GearBenchRead{{Token: "spot-cas", Bench: policy.GearBench{ID: "spot", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{recipe})}}}, bridge.Result{}, nil
}
func (n *clubBenchNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	return []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(500))}}, bridge.Result{}, nil
}

// The armory, not gear, crafts weapons for unarmed colonists (#1203), still
// under MaintainEquipment. Its exact-ID combat census counts every other pawn
// on the map as filtered; treating that as incomplete failed the step with
// ErrControl and MaintainEquipment never recovered (#660).
func TestArmoryPlannerCraftsWeaponsPastFilteredCensus(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	setGearProductionNeed(native.reply.GetObserved())
	n := &clubBenchNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}, weapons: []bridge.EquipCandidate{}, filtered: 4}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	ctx := context.Background()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	gear, err := NewRoutineGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	if result, err := gear.Step(ctx); err != nil || result.Reason == BuildingMethodAdmitted {
		t.Fatal("gear planned weapon work", result, err)
	}
	armory, err := NewRoutineArmoryPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := armory.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal("armory held the weapon bill", result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	bill, ok := plan.Spec.Actions()[0].ProductionBill()
	if !ok || bill.Recipe() != "Make_MeleeWeapon_Club" || bill.Target() != 2 || bill.Mode() != domain.GearBatch {
		t.Fatal("armory bill is not the colony club batch", plan.Spec.Actions()[0])
	}
}
