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

type refrigerationNative struct {
	*temperatureNative
	buildings     *o.ListBuildingsReply
	buildingReads int
}

func (n *refrigerationNative) ReadConstructionBuildings(ctx context.Context, _ *c.Identity, ids []string) (*o.ListBuildingsReply, bridge.Result, error) {
	n.buildingReads++
	return n.buildings, bridge.Result{}, ctx.Err()
}

// refrigerationFixture is the temperature fixture reshaped into a 2x2
// enclosed stockpile room at (1..2, 1..2) walled at 0 and 3, with the x=4 and
// z=4 columns outdoors, holding 20 nutrition of warm meat two days from rot.
func refrigerationFixture(t *testing.T, cooler bool) (*RoutineBuildingPlanner, *store.Store, *refrigerationNative, store.ControlRequest) {
	t.Helper()
	base, db, temperature, request := temperatureFixture(t, false)
	n := &refrigerationNative{temperatureNative: temperature}
	v := n.reply.GetObserved()
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(n), Returned: proto.Uint64(n), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	cells := v.Planning.GetObserved().Cells
	cells.Cells = nil
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 5; z++ {
			inside := x >= 1 && x <= 2 && z >= 1 && z <= 2
			wall := !inside && x <= 3 && z <= 3
			row := &o.CellState{Cell: cell(x, z), Indoors: proto.Bool(inside), Fogged: proto.Bool(false), Walkable: proto.Bool(!wall), Occupied: proto.Bool(wall), SupportsLight: proto.Bool(true), Issues: []*o.ReadIssue{{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}}}
			if inside || wall {
				row.Roof = proto.String("RoofConstructed")
			} else {
				row.Issues = append(row.Issues, &o.ReadIssue{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}})
			}
			cells.Cells = append(cells.Cells, row)
		}
	}
	room := n.rooms.GetObserved().Rooms[0]
	room.Cells = []*c.Cell{cell(1, 1), cell(1, 2), cell(2, 1), cell(2, 2)}
	room.Extents = &o.Rectangle{Minimum: cell(1, 1), Maximum: cell(2, 2)}
	room.Center = cell(1, 1)
	room.TemperatureC = proto.Float64(25)
	n.rooms.GetObserved().Rooms[0].Beds[0].Building.Position = cell(1, 1)
	v.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: &o.FoodSupplyFacts{
		Consumers:    []*o.FoodConsumer{{PawnId: proto.String("builder"), NutritionPerDay: proto.Float64(1.6)}},
		Stocks:       []*o.FoodStock{{Item: &o.EntityRef{Id: proto.String("meat"), DefName: proto.String("Meat_Muffalo")}, Count: proto.Int64(400), Nutrition: proto.Float64(20), EaterIds: []string{"builder"}, Perishable: proto.Bool(true), RotTicks: proto.Int64(2 * 60000), TemperatureC: proto.Float64(25), Roofed: proto.Bool(true), RoomId: proto.String("42")}},
		Completeness: count(2)}}}
	v.Planning.GetObserved().Definitions = append(v.Planning.GetObserved().Definitions, &o.PlanningDefinition{Definition: &o.DefinitionRef{DefName: proto.String("Cooler")}, Available: proto.Bool(true), ConstructionSkill: proto.Int32(4), Size: &o.MapSize{Width: proto.Uint32(1), Height: proto.Uint32(1)}})
	v.Planning.GetObserved().Completeness = count(uint64(len(v.Planning.GetObserved().Definitions)))
	n.buildings = &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: &o.BuildingsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: count(0)}}}
	if cooler {
		development := v.Development.GetObserved()
		position := cell(3, 1)
		development.Power = append(development.Power, &o.DevelopmentPower{BaseW: proto.Float64(-200), Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String("cooler"), DefName: proto.String("Cooler"), MapId: proto.Int32(0), Position: position}, OccupiedCells: []*c.Cell{position}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(true), PowerOutputW: proto.Float64(-200), SwitchedOn: proto.Bool(true)}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}})
		development.Completeness = count(uint64(len(development.Power)))
		snapshot := n.buildings.GetObserved()
		snapshot.Buildings = []*o.BuildingState{{Building: &o.EntityRef{Id: proto.String("cooler"), DefName: proto.String("Cooler"), MapId: proto.Int32(0), Position: position}, Status: proto.String("built"), Rotation: proto.String("East"), Settings: &o.BuildingSettings{TargetTemperatureC: proto.Float64(21), Snapshot: &o.SnapshotRef{EntityId: proto.String("cooler"), Token: proto.String("tok-1")}}}}
		snapshot.Completeness = count(1)
	}
	n.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
		b, _ := preview.Preview.Action.Building()
		preview.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
	}
	base.reviewer.native = n
	base.reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainRefrigeration})
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineRefrigerationPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, n, request
}

func TestRefrigerationReviewLatchesAndBuildsCoolerOnVentedWall(t *testing.T) {
	t.Parallel()
	p, db, n, _ := refrigerationFixture(t, false)
	review, err := db.LoadRoutineReview(context.Background())
	if err != nil || !review.Latches.Refrigeration {
		t.Fatal(review.Latches, err)
	}
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	b, _ := plan.Progress[0].Action().Building()
	// Lowest-sorted (x, then z) wall cell with a straight inside->wall->outside
	// line: (1,3) facing north (front outward, cold side at (1,2) inside).
	if b.Definition() != "Cooler" || b.Cell() != (domain.Cell{X: 1, Z: 3}) || b.Rotation() != domain.North {
		t.Fatal(b)
	}
	if n.previews != 1 || n.buildingReads != 0 {
		t.Fatal(n.previews, n.buildingReads)
	}
	if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
}

func TestRefrigerationPatchesExistingCoolerTargetThenWaits(t *testing.T) {
	t.Parallel()
	p, db, n, _ := refrigerationFixture(t, true)
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || n.buildingReads != 1 || n.previews != 0 {
		t.Fatal(result, err, n.buildingReads, n.previews)
	}
	goal, err := db.LoadGoal(context.Background(), result.Decision.Goal.Goal.ID)
	if err != nil {
		review, loadErr := db.LoadRoutineReview(context.Background())
		if loadErr != nil {
			t.Fatal(loadErr)
		}
		for _, binding := range review.Goals {
			if binding.Need == policy.MaintainRefrigeration {
				goal, err = db.LoadGoal(context.Background(), binding.Goal)
			}
		}
		if err != nil {
			t.Fatal(err)
		}
	}
	if len(goal.Methods) != 1 {
		t.Fatal(goal.Methods)
	}
	plan, err := db.LoadPlan(context.Background(), goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	patch, ok := plan.Progress[0].Action().BuildingTemperature()
	if !ok || patch.Thing() != "cooler" || patch.Celsius() != -5 || patch.BeforeToken() != "tok-1" {
		t.Fatal(patch, ok)
	}
	if next, err := p.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatal(next, err)
	}
	// The routine worker must dispatch the patch (it is not a player plan)
	// and the clock must not wait on it: a target change is immediate.
	if !routineExecutableKind(plan.Progress[0].Action().Kind()) {
		t.Fatal("building temperature patch is not routine-executable")
	}
	root := p.reviewer.player.State().Snapshot
	target := root
	target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if err := db.AuthorizeRoutinePlan(context.Background(), root, target); err != nil {
		t.Fatal("routine authorization refused the patch plan:", err)
	}
	if work, _, err := clockSchedulerWork(plan, target); err != nil || work {
		t.Fatal(work, err)
	}
}

func TestRefrigerationWaitsOnColdSetpointCooler(t *testing.T) {
	t.Parallel()
	p, _, n, _ := refrigerationFixture(t, true)
	n.buildings.GetObserved().Buildings[0].Settings.TargetTemperatureC = proto.Float64(-5)
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != RoutineBuildingReason(policy.RefrigerationWait) || result.Decision.Admitted || n.previews != 0 {
		t.Fatal(result, err)
	}
}

func TestRefrigerationDefersUnpoweredCoolerToPowerFamily(t *testing.T) {
	t.Parallel()
	p, _, n, _ := refrigerationFixture(t, true)
	power := n.reply.GetObserved().Development.GetObserved().Power
	power[len(power)-1].Building.Service.PowerOn = proto.Bool(false)
	// Planners plan from the review's census (#75): refresh it first.
	if _, err := p.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	result, err := p.Step(context.Background())
	if err != nil || result.Reason != RoutineBuildingReason(policy.RefrigerationPowerNeeded) || result.Decision.Admitted {
		t.Fatal(result, err)
	}
}
