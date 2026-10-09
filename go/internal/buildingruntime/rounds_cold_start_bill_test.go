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

// coldStartNative serves a steel ore cell for the steel a bill needs (#2487).
type coldStartNative struct{ *resourceNative }

func (n *coldStartNative) ReadResourceSources(_ context.Context, _ *c.Identity, resource string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error) {
	storage := policy.ResourceStorage{Resource: policy.Resource(resource), Capacity: 1000, StackLimit: 75, Haulers: 2}
	if resource == "Steel" {
		return []bridge.ResourceSourceRow{{ThingID: "ore1", Yield: 160, Distance: 6, Method: policy.ResourceSourceMine, Safety: "open_surface", Reachable: domain.Known(true), Cell: domain.Cell{X: 2, Z: 2}, Token: "ore-cas"}}, storage, bridge.Result{}, nil
	}
	return nil, storage, bridge.Result{}, nil
}

// coldStartStep runs one resource step for a colony with one floor, a bench
// holding recipe, no steel and no recorded steel spend.
func coldStartStep(t *testing.T, floor policy.Resource, recipe policy.GearRecipe) RoundsResourceResult {
	t.Helper()
	base, _, _, _, sleeping := sleepingFixture(t)
	sleeping.setFloors(map[policy.Resource]int64{floor: 20})
	sleeping.reply.GetObserved().Resources = []*o.Quantity{{DefName: proto.String(string(floor)), Units: proto.Int64(0)}, {DefName: proto.String("Steel"), Units: proto.Int64(0)}}
	native := &coldStartNative{&resourceNative{
		workshopNative: &workshopNative{sleepingNative: sleeping, benches: []bridge.GearBenchRead{{Token: "bench-cas", Bench: policy.GearBench{ID: "Thing_FabricationBench1", Bills: domain.Known([]policy.GearBill{}), Recipes: domain.Known([]policy.GearRecipe{recipe})}}}},
		stock:          []policy.Stock{{Resource: "Steel", Available: domain.Known(int64(0))}},
	}}
	v := sleeping.reply.GetObserved()
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	worker := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{worker("crafter"), worker("builder")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	base.reviewer.native = native
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 2, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	facts := &base.reviewer.census.latest.reading.Projection.Facts
	facts.FoodPlan = domain.Known(policy.FoodPlan{})
	facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: wall, Cells: []domain.Cell{{X: 2, Z: 2}}}}})
	facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "wall", Shape: domain.Known("shape"), Cells: []domain.Cell{{X: 2, Z: 2}}, Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}})
	facts.MapBounds = domain.Known(policy.Bounds{Width: 100, Height: 100})
	planner, err := NewRoundsResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return result
}

func steelRecipe(def string, product policy.Resource, available bool) policy.GearRecipe {
	return policy.GearRecipe{
		Definition: def, Products: []policy.Resource{product},
		Available: domain.Known(available), AvailableOn: domain.Known(available),
		Ingredients:  domain.Known([][]policy.Amount{{{Resource: "Steel", Count: 12}}}),
		RequiredWork: domain.Known([]policy.WorkRequirement{{Work: "Crafting", Skill: "Crafting"}}),
	}
}

// Cold start (#2487): a component floor stands, the colony holds no steel and
// the consumption ring has recorded no steel spend, so steel has no runway
// row. The component bill's steel need must become supply demand on the real
// planner path: the Round mines steel rather than holding the bill as
// ingredient_unavailable forever.
func TestColdStartComponentBillInducesSteelSupply(t *testing.T) {
	t.Parallel()
	result := coldStartStep(t, policy.ComponentResource, steelRecipe("MakeComponent", policy.ComponentResource, true))
	if result.Verdict != BuildingReasonAdmitted || result.Plan == "" || !minedSource(result, "ore-cas") {
		t.Fatalf("the component bill's steel was not supplied: %+v", result)
	}
}

// A drug runway row (DrugRunwayReserves keys every catalog social drug) whose
// recipe the colony cannot make (unresearched, no usable bench) plans no bill
// and induces no ingredient demand: the planner's producibility refusal
// covers it without a gate (#2487).
func TestUnproducibleDrugPlansNoBill(t *testing.T) {
	t.Parallel()
	result := coldStartStep(t, "SmokeleafJoint", steelRecipe("Make_SmokeleafJoint", "SmokeleafJoint", false))
	if result.Verdict == BuildingReasonAdmitted || result.Plan != "" || minedSource(result, "") {
		t.Fatalf("an unproducible drug admitted work: %+v", result)
	}
}
