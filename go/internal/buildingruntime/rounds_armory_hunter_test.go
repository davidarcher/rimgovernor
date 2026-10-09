package buildingruntime

import (
	"context"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// bowBenchNative is the gear colony with a crafting spot hosting the bow
// recipe and wood to fund it.
type bowBenchNative struct{ *clubBenchNative }

func (n *bowBenchNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := policy.GearRecipe{Definition: "Make_Bow_Short", Products: []policy.Resource{"Bow_Short"}, Armory: policy.ArmoryTierNeolithic, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known([][]policy.Amount{{{Resource: "WoodLog", Count: 40}}}), RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Crafting", Skill: "Crafting"}})}
	return []bridge.GearBenchRead{{Token: "spot-cas", Bench: policy.GearBench{ID: "spot", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{recipe})}}}, bridge.Result{}, nil
}

// An unarmed colonist working Hunting wants a bow, not a club: the hunter rule
// (ScoreWeapon) picks the bench's ranged recipe. With food's need not standing
// the bill is MaintainEquipment's.
func TestArmoryCraftsHunterBow(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	reviewer, _, _, _, native := roundsFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	setGearProductionNeed(native.reply.GetObserved())
	n := &bowBenchNative{&clubBenchNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{roundsNative: native, ids: []string{"a", "b"}, weapons: []bridge.EquipCandidate{}, editPawn: func(row *o.PawnState) {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}}}}}
	settleGearPolicies(t, native)
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainEquipment})
	armory, err := NewRoundsArmoryPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	declared := declaredOrders(t, reviewer, armory)
	if order, ok := orderFor(declared, "Make_Bow_Short"); !ok || order.Target != 2 || order.Mode != domain.GearBatch {
		t.Fatal("armory declared no batch of two bows", declared)
	}
}
