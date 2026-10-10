package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

func TestArmoryPrimary(t *testing.T) {
	id, def, q := "r1", "Gun_Revolver", o.Quality_QUALITY_GOOD
	catalog, err := sharedBaseCatalog("load")
	if err != nil {
		t.Fatal(err)
	}
	row := &o.PawnState{Equipment: &o.PawnEquipment{PrimaryId: &id, Equipped: []*o.GearItem{{Thing: &c.Ref{Id: &id}, Quality: &q}}}}
	got, ok, err := armoryPrimary(row, bridge.NewThings(&o.Thing{Thing: &o.EntityRef{Id: &id, DefName: &def}}), catalog)
	if err != nil || !ok || got.Definition != def || !got.Ranged || got.Quality != 3 || !got.Facts.Ranged {
		t.Fatal(got, ok, err)
	}
	if _, ok, _ := armoryPrimary(&o.PawnState{}, bridge.Things{}, catalog); ok {
		t.Fatal("unarmed pawn has a primary")
	}
}

// clubBenchNative is the gear colony with a crafting spot hosting the club
// recipe and wood to fund it.
type clubBenchNative struct {
	*gearTestNative
}

func (n *clubBenchNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := policy.GearRecipe{Definition: "Make_MeleeWeapon_Club", Products: []policy.Resource{"MeleeWeapon_Club"}, Armory: policy.ArmoryTierNeolithic, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]policy.Amount{{{Resource: "WoodLog", Count: 40}}}), RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Crafting", Skill: "Crafting"}})}
	return []bridge.GearBenchRead{{Token: "spot-cas", Bench: policy.GearBench{ID: "spot", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{recipe})}}}, bridge.Result{}, nil
}
func (n *clubBenchNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	return []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(500))}}, bridge.Result{}, nil
}

// The armory, not gear, crafts weapons for unarmed colonists, still
// under MaintainEquipment. Its exact-ID combat census counts every other pawn
// on the map as filtered; treating that as incomplete failed the step with
// ErrControl and MaintainEquipment never recovered.
func TestArmoryPlannerCraftsWeaponsPastFilteredCensus(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	reviewer, _, _, _, native := roundsFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	setGearProductionNeed(native.reply.GetObserved())
	n := &clubBenchNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{roundsNative: native, ids: []string{"a", "b"}, weapons: []bridge.EquipCandidate{}, filtered: 4}}}
	settleGearPolicies(t, native)
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	ctx := context.Background()
	gear, err := NewRoundsGearPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	armory, err := NewRoundsArmoryPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	gearSpy := &spyDeclarer{inner: gear}
	reviewer.AddOrderDeclarer(gearSpy)
	armoryDeclared := declaredOrders(t, reviewer, armory)
	if _, planned := orderFor(gearSpy.got, "Make_MeleeWeapon_Club"); planned {
		t.Fatal("gear declared weapon work", gearSpy.got)
	}
	order, ok := orderFor(armoryDeclared, "Make_MeleeWeapon_Club")
	if !ok || order.Target != 2 || order.Mode != domain.GearBatch {
		t.Fatal("armory declared no colony club batch", armoryDeclared)
	}
	if result, err := gear.Step(ctx); err != nil || result.Verdict == BuildingReasonAdmitted {
		t.Fatal("gear committed weapon work", result, err)
	}
}
