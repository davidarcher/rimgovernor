package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// fabricationArmorNative is a fabrication-tier colony with a bench hosting
// the marine, recon and flak body armor, and the plasteel, components and
// steel to fund any of them.
type fabricationArmorNative struct {
	*gearTestNative
}

func (n *fabricationArmorNative) ReadResearch(context.Context, *c.Identity) (bridge.ResearchRead, bridge.Result, error) {
	return bridge.ResearchRead{Context: n.reply.GetObserved().Context, Finished: []string{"Smithing", "Machining", "Fabrication"}}, bridge.Result{}, nil
}

func (n *fabricationArmorNative) ReadRoutineFrame(ctx context.Context, id *c.Identity) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id)
}

// AnimalRaceCatalog is the empty race catalog (the observation source requires one).
func (n *fabricationArmorNative) AnimalRaceCatalog(context.Context, *c.Identity) (*bridge.AnimalRaces, error) {
	return &bridge.AnimalRaces{}, nil
}

func (n *fabricationArmorNative) ReadGearBenches(context.Context, *c.Identity) ([]bridge.GearBenchRead, bridge.Result, error) {
	recipe := func(def string, product policy.Resource, slots [][]policy.Amount) policy.GearRecipe {
		return policy.GearRecipe{Definition: def, Products: []policy.Resource{product}, Available: domain.Known(true), AvailableOn: domain.Known(true), Ingredients: domain.Known(slots), RequiredWork: domain.Known([]policy.WorkRequirement{})}
	}
	recipes := []policy.GearRecipe{
		recipe("Make_Apparel_PowerArmor", "Apparel_PowerArmor", [][]policy.Amount{{{Resource: "Plasteel", Count: 100}}, {{Resource: "ComponentSpacer", Count: 2}}}),
		recipe("Make_Apparel_ArmorRecon", "Apparel_ArmorRecon", [][]policy.Amount{{{Resource: "Plasteel", Count: 60}}}),
		recipe("Make_Apparel_FlakVest", "Apparel_FlakVest", [][]policy.Amount{{{Resource: "Steel", Count: 60}}}),
	}
	return []bridge.GearBenchRead{{Token: "fab-cas", Bench: policy.GearBench{ID: "fab", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known(recipes)}}}, bridge.Result{}, nil
}

func (n *fabricationArmorNative) ReadSupplyStock(context.Context, *c.Identity, []string) ([]policy.Stock, bridge.Result, error) {
	return []policy.Stock{{Resource: "Plasteel", Available: domain.Known(int64(400))}, {Resource: "ComponentSpacer", Available: domain.Known(int64(4))}, {Resource: "Steel", Available: domain.Known(int64(500))}}, bridge.Result{}, nil
}

// armoryArmorRecipe runs one armory step for a colonist wanting marine
// armor under the given MaintainResource plasteel floor and returns the
// billed recipe.
func armoryArmorRecipe(t *testing.T, plasteelFloor int64) string {
	t.Helper()
	reviewer, db, _, _, native := routineFixture(t)
	reviewer.policy.Stage.Floor = policy.StageDevelopment
	if plasteelFloor > 0 {
		reviewer.policy.ResourceTargets = map[policy.Resource]int64{"Plasteel": plasteelFloor}
	}
	observed := native.reply.GetObserved()
	setGearProductionNeed(observed)
	observed.GetPlanning().GetObserved().Gear.Pawns[0].ReplacementNeeds = []*o.GearReplacementNeed{{DefName: proto.String("Apparel_PowerArmor"), Reason: proto.String("loadout")}}
	observed.Threat = &o.ThreatSection{Outcome: &o.ThreatSection_Observed{Observed: &o.ThreatFacts{RaidPoints: proto.Float64(3000)}}}
	n := &fabricationArmorNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native, ids: []string{"a", "b"}, weapons: []bridge.EquipCandidate{}, editPawn: func(row *o.PawnState) {
		gun := row.GetPawn().GetId() + "-gun"
		row.Equipment = &o.PawnEquipment{Armed: proto.Bool(true), PrimaryId: proto.String(gun), Equipped: []*o.GearItem{{Thing: native.entity(&o.EntityRef{Id: proto.String(gun), DefName: proto.String("Gun_ChargeRifle")}), Weapon: proto.Bool(true), Ranged: proto.Bool(true), Quality: o.Quality_QUALITY_GOOD.Enum()}}}
	}}}}
	reviewer.native = n
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainEquipment})
	ctx := context.Background()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	armory, err := NewRoutineArmoryPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	result, err := armory.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal("armory held the armor bill", result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil {
		t.Fatal(err)
	}
	bill, ok := plan.Spec.Actions()[0].ProductionBill()
	if !ok {
		t.Fatal("armory plan is not a bill", plan.Spec.Actions()[0])
	}
	return bill.Recipe()
}

// A plasteel stock MaintainResource holds is not spent by an armor bill
// (#1230): the marine need falls to flak, which spends only steel.
func TestArmoryArmorBillLeavesHeldPlasteel(t *testing.T) {
	t.Parallel()
	if got := armoryArmorRecipe(t, 0); got != "Make_Apparel_PowerArmor" {
		t.Fatal("unheld plasteel did not fund marine armor", got)
	}
	if got := armoryArmorRecipe(t, 350); got != "Make_Apparel_FlakVest" {
		t.Fatal("armor bill spent held plasteel", got)
	}
}
