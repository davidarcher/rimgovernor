package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// shellBenchNative is a colony with a machining table hosting the shell
// recipes.
type shellBenchNative struct {
	*gearTestNative
}

func (n *shellBenchNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := func(def string, product policy.Resource) policy.GearRecipe {
		return policy.GearRecipe{Definition: def, Products: []policy.Resource{product}, Available: domain.Known(true), AvailableOn: domain.Known(true)}
	}
	recipes := []policy.GearRecipe{recipe("Make_Shell_HighExplosive", "Shell_HighExplosive"), recipe("Make_Shell_Incendiary", "Shell_Incendiary")}
	return []bridge.GearBenchRead{{Token: "machining-cas", Bench: policy.GearBench{ID: "machining", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known(recipes)}}}, bridge.Result{}, nil
}

// The armory issues a shell bill once a mortar stands, and none before
// (#1207).
func TestArmoryStocksShellsAfterMortarBuilt(t *testing.T) {
	t.Parallel()
	reviewer, db, session, _, native := routineFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	observed := native.reply.GetObserved()
	setGearProductionNeed(observed)
	observed.Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Observed{Observed: &o.ThreatFacts{RaidPoints: proto.Float64(1500)}}}
	n := &shellBenchNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}, weapons: []bridge.EquipCandidate{}}}}
	settleGearPolicies(t, native)
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	ctx := context.Background()
	armory, err := NewRoutineArmoryPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if result, err := armory.Step(ctx); err != nil || result.Verdict == BuildingReasonAdmitted {
		t.Fatal("armory billed without a mortar", result, err)
	}
	snapshot := session.State().Snapshot
	world := store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
	layout := store.DefenseLayoutRecord{World: world, Project: "layout-goal", Toward: domain.North, Chokepoint: domain.Cell{X: 9, Z: 10}, Entry: domain.Cell{X: 9, Z: 10}, Firing: []domain.Cell{{X: 9, Z: 23}}, Tiers: []store.DefenseTierRecord{{Name: policy.TierMortars, Built: true, Buildings: []store.DefenseBuilding{{Definition: "Turret_Mortar", Cell: domain.Cell{X: 5, Z: 5}, Rotation: domain.North}}}}}
	if err := db.SaveDefenseLayout(ctx, layout); err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	result, err := armory.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal("armory held the shell bill", result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	bill, ok := plan.Spec.Actions()[0].ProductionBill()
	if !ok || bill.Recipe() != "Make_Shell_HighExplosive" || bill.Mode() != domain.StockTarget || bill.Target() != 10 {
		t.Fatal("armory bill is not the HE shell stock", plan.Spec.Actions()[0])
	}
}
