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
	pp "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
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
func refrigerationFixture(t *testing.T, cooler bool) (*RoundsBuildingPlanner, *store.Store, *refrigerationNative, store.ControlRequest) {
	t.Helper()
	base, db, temperature, request := temperatureFixture(t, false)
	n := &refrigerationNative{temperatureNative: temperature}
	v := n.reply.GetObserved()
	count := func(n uint64) *o.Completeness {
		return &o.Completeness{Filtered: proto.Uint64(0)}
	}
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	cells := n.cells
	cells.Cells = nil
	for x := int32(0); x < 5; x++ {
		for z := int32(0); z < 5; z++ {
			inside := x >= 1 && x <= 2 && z >= 1 && z <= 2
			wall := !inside && x <= 3 && z <= 3
			row := openCell(x, z)
			row.Indoors, row.Walkable, row.Occupied = domain.Known(inside), domain.Known(!wall), domain.Known(wall)
			if inside || wall {
				row = roofed(row)
			}
			cells.Cells = append(cells.Cells, row)
		}
	}
	bridge.SortSiteCells(cells.Cells)
	room := n.rooms.GetObserved().Rooms[0]
	room.Extents = &o.Rectangle{Minimum: cell(1, 1), Maximum: cell(2, 2)}
	room.Center = cell(1, 1)
	room.TemperatureC = proto.Float64(25)
	v.FoodSupply = &o.FoodSupplySection{Outcome: &o.FoodSupplySection_Observed{Observed: &o.FoodSupplyFacts{
		Consumers: []*o.FoodConsumer{{PawnId: proto.String("builder"), NutritionPerDay: proto.Float64(1.6)}},
		Stocks: []*o.FoodStock{{Item: n.thing(&o.Thing{Thing: &o.EntityRef{Id: proto.String("meat"), DefName: proto.String("Meat_Muffalo")}, StackCount: proto.Int64(400), RotTicks: proto.Int64(2 * 60000), TemperatureC: proto.Float64(25), Roofed: proto.Bool(true), Room: &c.Ref{Id: proto.String("42")}}),
			Nutrition: proto.Float64(20), Eaters: bridge.NewRefs([]string{"builder"})}}}}}
	cooler200 := 200.0
	n.putCatalog(bridge.FixtureDef{Name: "Cooler", ConstructionSkill: 4, Width: 1, Height: 1, PowerW: &cooler200})
	n.buildings = &o.ListBuildingsReply{Outcome: &o.ListBuildingsReply_Observed{Observed: &o.BuildingsSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Completeness: count(0)}}}
	if cooler {
		development := v.Development.GetObserved()
		position := cell(3, 1)
		development.Power = append(development.Power, &o.DevelopmentPower{Building: bridge.NewRef(n.building(&o.BuildingState{Building: &o.EntityRef{Id: proto.String("cooler"), DefName: proto.String("Cooler"), MapId: proto.Int32(0), Position: position}, Occupied: &o.Rectangle{Minimum: position, Maximum: position}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(true), PowerOutputW: proto.Float64(-200), SwitchedOn: proto.Bool(true)}, Settings: &o.BuildingSettings{Forbidden: proto.Bool(false)}}).GetId())})
		snapshot := n.buildings.GetObserved()
		snapshot.Buildings = []*o.BuildingState{{Building: &o.EntityRef{Id: proto.String("cooler"), DefName: proto.String("Cooler"), MapId: proto.Int32(0), Position: position}, Status: o.BuildingStatus_BUILDING_STATUS_BUILT.Enum(), Rotation: pp.Rotation_ROTATION_EAST.Enum(), Settings: &o.BuildingSettings{TargetTemperatureC: proto.Float64(21), Snapshot: &o.SnapshotRef{EntityId: proto.String("cooler"), Token: proto.String("tok-1")}}}}
		snapshot.Completeness = count(1)
	}
	n.onPreview = func(ctx context.Context, preview *bridge.BuildingPreview) {
		b, _ := preview.Preview.Action.Building()
		preview.Preview.Footprint = domain.Known([]domain.Cell{b.Cell()})
	}
	base.reviewer.native = n
	base.reviewer.methods = domain.Known([]policy.ConcernID{policy.MaintainRefrigeration})
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoundsRefrigerationPlanner(base.reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, n, request
}

func TestRefrigerationReviewLatchesAndBuildsCoolerOnVentedWall(t *testing.T) {
	t.Parallel()
	p, db, n, _ := refrigerationFixture(t, false)
	review, err := db.LoadRounds(context.Background())
	if err != nil || !review.Latches.Refrigeration {
		t.Fatal(review.Latches, err)
	}
	result, err := p.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
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
	if n.previews != 1 || n.buildingReads != 1 {
		t.Fatal(n.previews, n.buildingReads)
	}
	if next, err := p.Step(context.Background()); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
}

func TestRefrigerationPatchesExistingCoolerTargetThenWaits(t *testing.T) {
	t.Parallel()
	p, db, n, _ := refrigerationFixture(t, true)
	result, err := p.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted || n.buildingReads != 2 || n.previews != 0 {
		t.Fatal(result, err, n.buildingReads, n.previews)
	}
	goal, err := db.LoadStandard(context.Background(), result.Decision.Goal.Standard.ID)
	if err != nil {
		review, loadErr := db.LoadRounds(context.Background())
		if loadErr != nil {
			t.Fatal(loadErr)
		}
		for _, binding := range review.Goals {
			if binding.Need == policy.MaintainRefrigeration {
				goal, err = db.LoadStandard(context.Background(), binding.Goal)
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
	if !ok || patch.Thing() != "cooler" || patch.Celsius() != -5 {
		t.Fatal(patch, ok)
	}
	if next, err := p.Step(context.Background()); err != nil || next.Verdict != BuildingReasonExistingWork {
		t.Fatal(next, err)
	}
	// The routine worker must dispatch the patch (it is not a player plan)
	// and the clock must not wait on it: a target change is immediate.
	if !roundsExecutableKind(plan.Progress[0].Action().Kind()) {
		t.Fatal("building temperature patch is not routine-executable")
	}
	root := p.reviewer.player.State().Snapshot
	target := root
	target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
	if err := db.AuthorizeRoundsPlan(context.Background(), root, target); err != nil {
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
	if err != nil || result.Verdict != awaitingMethod(policy.RefrigerationWait) || result.Decision.Admitted || n.previews != 0 {
		t.Fatal(result, err)
	}
}

func TestRefrigerationDefersUnpoweredCoolerToPowerFamily(t *testing.T) {
	t.Parallel()
	p, _, n, _ := refrigerationFixture(t, true)
	power := n.reply.GetObserved().Development.GetObserved().Power
	n.roundsNative.buildings.At(power[len(power)-1].Building.GetId()).Service.PowerOn = proto.Bool(false)
	// Planners plan from the review's census (#75): refresh it first.
	if _, err := p.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	result, err := p.Step(context.Background())
	if err != nil || result.Verdict != awaitingMethod(policy.RefrigerationPowerNeeded) || result.Decision.Admitted {
		t.Fatal(result, err)
	}
}

// A cooler method that just completed reads powerOn=false until the power
// net ticks once (the supervisor latches the window on the completing tick),
// so cooler_power_needed lends the cooling allowance instead of parking the
// clock on no_work (#66).
func TestRefrigerationPowerNeededAfterCompletedMethodLendsCoolingAllowance(t *testing.T) {
	t.Parallel()
	p, db, n, _ := refrigerationFixture(t, true)
	result, err := p.Step(context.Background())
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	review, err := db.LoadRounds(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	var goal store.StandardState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainRefrigeration {
			goal, err = db.LoadStandard(context.Background(), binding.Goal)
		}
	}
	if err != nil || len(goal.Methods) != 1 {
		t.Fatal(goal.Methods, err)
	}
	plan, err := db.LoadPlan(context.Background(), goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	root := p.reviewer.player.State().Snapshot
	scope := root
	scope.Plan, scope.Revision = plan.Spec.ID(), plan.Spec.Revision()
	action := plan.Progress[0].Action().ID()
	tick := goal.Standard.Tick
	if _, err := db.Prepare(context.Background(), plan.Spec.ID(), action, scope, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Dispatch(context.Background(), plan.Spec.ID(), action, scope, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := db.RecordReceipt(context.Background(), plan.Spec.ID(), action, 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	power := n.reply.GetObserved().Development.GetObserved().Power
	n.roundsNative.buildings.At(power[len(power)-1].Building.GetId()).Service.PowerOn = proto.Bool(false)
	if _, err := p.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	result, err = p.Step(context.Background())
	if err != nil || result.Verdict != awaitingMethod(policy.RefrigerationPowerNeeded) || result.Decision.Admitted {
		t.Fatal(result, err)
	}
	if result.NativeWorkTicks == 0 {
		t.Fatal("completed cooler method left the unpowered cooler with no native work ticks")
	}
}

// The native generation moves with every window the supervisor stops; a
// cooler completed under an earlier generation keeps its cooling allowance
// (same rule as the temperature budget), a reloaded world does not (#66).
func TestRefrigerationNativeWorkTicksSurviveGenerationMoves(t *testing.T) {
	t.Parallel()
	building, _ := domain.NewBuilding("Cooler", domain.Cell{X: 1, Z: 3}, domain.North, "")
	action, _ := domain.NewBuildingAction("cooler", building)
	spec, _ := domain.NewPlan("cooler-plan", 1, []domain.Action{action})
	progress, _ := domain.NewProgress(spec, action.ID())
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Native: 10, Plan: spec.ID(), Revision: 1}
	var err error
	for _, step := range []func() (domain.Progress, error){
		func() (domain.Progress, error) { return progress.Prepare(snapshot, 100) },
		func() (domain.Progress, error) { return progress.MarkDispatched(snapshot, 100) },
		func() (domain.Progress, error) { return progress.RecordReceipt(1, domain.ReceiptAccepted) },
	} {
		if progress, err = step(); err != nil {
			t.Fatal(err)
		}
	}
	state := store.PlanState{Spec: spec, Progress: []domain.Progress{progress}}
	if got, completed := refrigerationNativeWorkTicks(state, snapshot, 100); got != refrigerationCoolingWindowTicks || !completed {
		t.Fatal(got, completed)
	}
	snapshot.Native = 12
	if got, completed := refrigerationNativeWorkTicks(state, snapshot, 100); got != refrigerationCoolingWindowTicks || !completed {
		t.Fatal("moved native generation lost the cooling allowance", got, completed)
	}
	snapshot.Load = "reload"
	if got, _ := refrigerationNativeWorkTicks(state, snapshot, 100); got != 0 {
		t.Fatal("reloaded world inherited the cooling allowance", got)
	}
}

// A setpoint patch that lands on a paused game leaves the census reading
// the old target until the game ticks: the same patch is proposed again
// and found used, and that outcome must still lend native cooling time or
// the clock never runs to refresh the census.
func TestRefrigerationUsedSetpointPatchLendsCoolingTime(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	p, db, _, _ := refrigerationFixture(t, true)
	result, err := p.Step(ctx)
	if err != nil || result.Verdict != BuildingReasonAdmitted {
		t.Fatal(result, err)
	}
	review, err := db.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var goal store.StandardState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainRefrigeration {
			if goal, err = db.LoadStandard(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	if len(goal.Methods) != 1 {
		t.Fatal(goal.Methods)
	}
	plan, err := db.LoadPlan(ctx, goal.Methods[0].Plan)
	if err != nil || len(plan.Progress) != 1 {
		t.Fatal(plan, err)
	}
	action := plan.Progress[0].Action().ID()
	snapshot := goal.Standard.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	tick := goal.Standard.Tick
	if _, err := db.Prepare(ctx, plan.Spec.ID(), action, snapshot, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Dispatch(ctx, plan.Spec.ID(), action, snapshot, tick); err != nil {
		t.Fatal(err)
	}
	if _, err := db.RecordReceipt(ctx, plan.Spec.ID(), action, 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	// The census still reads the warm target: the same patch is used.
	next, err := p.Step(ctx)
	if err != nil || !next.Verdict.Is(WaitMethodUsed) || next.NativeWorkTicks == 0 {
		t.Fatal(next, err)
	}
}

// A freezer that settled in an earlier epoch has no cooler method in the
// epoch that re-latched when the season warmed, so the allowance is lent
// from the tick the latch engaged and a second cooler becomes proposable
// once it elapses (#202).
func TestRefrigerationEpochWithoutMethodLendsAllowanceFromLatch(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	p, db, n, _ := refrigerationFixture(t, true)
	n.buildings.GetObserved().Buildings[0].Settings.TargetTemperatureC = proto.Float64(-5)
	review, err := db.LoadRounds(ctx)
	if err != nil || !review.Latches.Refrigeration || review.Latches.RefrigerationSince != review.Tick {
		t.Fatal(review.Latches, review.Tick, err)
	}
	// Re-reviewing keeps the tick the latch first engaged.
	if _, err := p.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if again, err := db.LoadRounds(ctx); err != nil || again.Latches.RefrigerationSince != review.Latches.RefrigerationSince {
		t.Fatal(again.Latches, err)
	}
	result, err := p.Step(ctx)
	if err != nil || result.Verdict != awaitingMethod(policy.RefrigerationWait) || result.Decision.Admitted || n.previews != 0 {
		t.Fatal(result, err)
	}
	if result.NativeWorkTicks == 0 {
		t.Fatal("epoch without a cooler method lent no native cooling time")
	}
	var goal store.StandardState
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainRefrigeration {
			if goal, err = db.LoadStandard(ctx, binding.Goal); err != nil {
				t.Fatal(err)
			}
		}
	}
	if len(goal.Methods) != 0 {
		t.Fatal(goal.Methods)
	}
	since := review.Latches.RefrigerationSince
	snapshot := p.reviewer.player.State().Snapshot
	for _, row := range []struct {
		tick      domain.Tick
		allowance uint32
		exhausted bool
	}{
		{since, refrigerationCoolingWindowTicks, false},
		{since + refrigerationCoolingTicks - 60, 60, false},
		{since + refrigerationCoolingTicks, 0, true},
		{since + 3*refrigerationCoolingTicks, 0, true},
	} {
		allowance, exhausted, lent, err := refrigerationOutputAllowance(ctx, db, goal, snapshot, row.tick, since)
		if err != nil || !lent || allowance != row.allowance || exhausted != row.exhausted {
			t.Fatal(row, allowance, exhausted, lent, err)
		}
	}
	// A latch without a tick (a review written before the field existed)
	// or a rewound clock lends nothing and never exhausts.
	for _, row := range []struct{ since, tick domain.Tick }{{0, since + 3*refrigerationCoolingTicks}, {since, since - 1}} {
		if allowance, exhausted, lent, err := refrigerationOutputAllowance(ctx, db, goal, snapshot, row.tick, row.since); err != nil || !lent || allowance != 0 || exhausted {
			t.Fatal(row, allowance, exhausted, lent, err)
		}
	}
}

func (n *refrigerationNative) ReadRoundsFrame(ctx context.Context, id *c.Identity) (bridge.RoundsFrame, error) {
	return fakeFrame(ctx, n, id)
}
