package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

type fieldTestNative struct {
	*roundsNative
}

func (n *fieldTestNative) PreviewZone(ctx context.Context, id *c.Identity, target domain.ZoneCreate) (*op.ZonePreviewReply, bridge.Result, error) {
	return &op.ZonePreviewReply{Outcome: &op.ZonePreviewReply_Evaluated{Evaluated: &op.ZonePreview{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Accepted: proto.Bool(true)}}}, bridge.Result{}, nil
}
func TestFieldPlannerReservationsAndGrowthBudget(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	ctx := context.Background()
	base, db, session, _, n := sleepingFixture(t)
	reviewer := base.reviewer
	reviewer.methods = domain.Known([]policy.ConcernID{policy.EnsureFoodSupply})
	v := n.reply.GetObserved()
	foodPlanFixture(v)
	// A new field opens only if its lead (3 grow days) fits the food
	// runway; with no stock the runway is zero and the plan holds it.
	stock := &o.FoodStock{Item: n.thing(&o.Thing{Thing: &o.EntityRef{Id: proto.String("pemmican"), DefName: proto.String("Pemmican")}, StackCount: proto.Int64(9)}), Nutrition: proto.Float64(9), Eaters: bridge.NewRefs([]string{"food-pawn"})}
	v.FoodSupply.GetObserved().Stocks = []*o.FoodStock{stock}
	v.Forecast.GetObserved().CombinedFoodSupply.Stocks = []*o.FoodStock{proto.Clone(stock).(*o.FoodStock)}
	v.Farms = nil
	v.Biome = proto.String("TemperateForest")
	v.FoodClimate = &o.FoodClimate{GrowingDays: proto.Float64(60), GrowingDaysRemaining: proto.Float64(60), GrowingDaysUntil: proto.Float64(0), NonGrowingDays: proto.Float64(0), SowingNow: proto.Bool(true)}
	issues := v.Issues[:0]
	for _, i := range v.Issues {
		if i.GetField() != "farms" && i.GetField() != "food_climate" {
			issues = append(issues, i)
		}
	}
	v.Issues = issues
	planning := v.Planning.GetObserved()
	n.rice()
	planning.Crops = []*o.EdibleCrop{{DefName: proto.String("Plant_Rice"), NutritionDemandPerDay: proto.Float64(5), DietAllowed: proto.Bool(true)}}
	for i := range n.cells.Cells {
		cell := &n.cells.Cells[i]
		cell.Roof, cell.Roofed, cell.Fertility = domain.Unknown[string](), domain.Known(false), domain.Known(1.0)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	// Every census cell is one plan field block.
	var runs []policy.RowRun
	for _, cell := range n.cells.Cells {
		runs = append(runs, policy.RowRun{Z: cell.Cell.Z, X: cell.Cell.X, Length: 1})
	}
	reviewer.census.rememberLayout(reviewer.census.layoutScope, domain.Known(policy.LayoutPlan{Zones: []policy.LayoutZone{{Kind: policy.ZoneField, Runs: runs}}}))
	planner, err := NewRoundsFieldPlanner(reviewer, &fieldTestNative{roundsNative: n.roundsNative})
	if err != nil {
		t.Fatal(err)
	}
	result, err := planner.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(ctx, result.Plan)
	if err != nil || len(plan.Admissions) != len(plan.Progress) || len(plan.Progress) == 0 {
		t.Fatal(plan, err)
	}
	snapshot := session.State().Snapshot
	snapshot.Plan = result.Plan
	snapshot.Revision = 1
	if err := db.AuthorizeRoundsPlan(ctx, session.State().Snapshot, snapshot); err != nil {
		t.Fatal(err)
	}
	if work, _, err := clockSchedulerWork(plan, snapshot); err != nil || work {
		t.Fatal("creation needs no ticks", work, err)
	}
	a := plan.Spec.Actions()[0]
	tick := domain.Tick(v.Context.GetTick())
	// A zone_create is an intent: it prepares untyped under the footprint
	// admission and its applied receipt records the created zone.
	if _, err := db.Prepare(ctx, result.Plan, a.ID(), snapshot, tick); err != nil {
		t.Fatal(err)
	}
	dispatched, err := db.Dispatch(ctx, result.Plan, a.ID(), snapshot, tick)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := db.RecordZoneReceipt(ctx, result.Plan, a.ID(), dispatched.View().Attempt, "field"); err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal domain.Standard
	for _, binding := range review.Standards {
		if binding.Concern == policy.EnsureFoodSupply {
			g, err := db.LoadStandard(ctx, binding.Standard)
			if err != nil {
				t.Fatal(err)
			}
			goal = g.Standard
		}
	}
	facts := observation.ColonyProjection{Identity: observation.Identity{Tick: tick + 1}, Definitions: []observation.PlanningDefinition{{Name: "Plant_Rice", GrowDays: domain.Known(3.0)}}, Farms: []observation.FarmZoneFact{{ID: "field", Crop: "Plant_Rice"}}}
	allowance, err := planner.fieldAllowance(ctx, goal, session.State().Snapshot, facts)
	if err != nil || allowance == 0 {
		t.Fatal("growth budget", allowance, err)
	}
	facts.Identity.Tick += domain.Tick(allowance)
	if wait, err := planner.fieldAllowance(ctx, goal, session.State().Snapshot, facts); err != nil || wait != 0 {
		t.Fatal("deadline renewed", wait, err)
	}
	facts.Identity.Tick = tick + 1
	facts.Farms[0].Crop = "Plant_Potato"
	if wait, err := planner.fieldAllowance(ctx, goal, session.State().Snapshot, facts); err != nil || wait != 0 {
		t.Fatal("changed field granted time", wait, err)
	}
}

// Open hunting or foraging under EnsureFoodSupply must not starve the field
// planner; only open zone work does.
func TestFieldBlockingWorkIgnoresAcquisition(t *testing.T) {
	t.Parallel()
	hunt := dispatchedHunt(t, "hunt-deer", "deer", 100)
	if fieldBlockingWork([]domain.Progress{hunt}) {
		t.Fatal("open hunt blocked fields")
	}
	zone, err := domain.NewZoneCreate(domain.GrowingZone, "Plant_Rice", []domain.Cell{{X: 1, Z: 1}, {X: 2, Z: 1}})
	if err != nil {
		t.Fatal(err)
	}
	a, err := domain.NewZoneCreateAction("field-1", zone)
	if err != nil {
		t.Fatal(err)
	}
	spec, err := domain.NewPlan("field-plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	p, err := domain.NewProgress(spec, a.ID())
	if err != nil {
		t.Fatal(err)
	}
	if !fieldBlockingWork([]domain.Progress{hunt, p}) {
		t.Fatal("pending zone did not block fields")
	}
	projection := observation.ColonyProjection{Definitions: []observation.PlanningDefinition{{Name: "Plant_Rice", HarvestNutrition: domain.Known(1.0), GrowDays: domain.Known(2.0)}}}
	plans := []store.PlanState{{Progress: []domain.Progress{hunt, p}}}
	for _, gap := range []float64{0, 1, 2} {
		open := policy.FoodPlanEntry{Channel: policy.SupplyCandidate{Kind: policy.CandidateCrop, ID: policy.NewFieldPrefix + "Plant_Rice"}, Decision: policy.FoodPlanOpen}
		projection.Facts.FoodPlan = domain.Known(policy.FoodPlan{GapPerDay: gap, Portfolio: []policy.FoodPlanEntry{open}})
		if got := foodPlanFieldRoom(projection, plans); got != (gap > 1) {
			t.Fatalf("gap %v: additional field = %v", gap, got)
		}
	}
	projection.Definitions[0].GrowDays = domain.Unknown[float64]()
	if foodPlanFieldRoom(projection, plans) {
		t.Fatal("unknown pending yield admitted another field")
	}
	if p, err = p.Cancel(); err != nil {
		t.Fatal(err)
	}
	if fieldBlockingWork([]domain.Progress{p}) {
		t.Fatal("cancelled zone blocked fields")
	}
}

// An infrastructure batch is trimmed to what the preview's own stock scan can
// pay for once earlier placements are counted; an unknown availability is
// left to admission.
func TestFieldAffordableStopsAtObservedStock(t *testing.T) {
	steel := policy.Resource("Steel")
	basin := policy.Preview{Costs: domain.Known([]policy.Amount{{Resource: steel, Count: 100}, {Resource: "ComponentIndustrial", Count: 1}})}
	stock := policy.StockObservation{Values: []policy.Stock{{Resource: steel, Available: domain.Known(int64(300))}, {Resource: "ComponentIndustrial", Available: domain.Known(int64(10))}}}
	spent := map[policy.Resource]int64{}
	for i := 0; i < 3; i++ {
		if !fieldAffordable(spent, basin, stock) {
			t.Fatalf("basin %d should fit 300 steel", i)
		}
		for _, cost := range fieldCosts(basin) {
			spent[cost.Resource] += cost.Count
		}
	}
	if fieldAffordable(spent, basin, stock) {
		t.Fatal("a fourth basin exceeds 300 steel")
	}
	unknown := policy.StockObservation{Values: []policy.Stock{{Resource: steel}}}
	if !fieldAffordable(spent, basin, unknown) {
		t.Fatal("unknown availability is admission's call")
	}
}
