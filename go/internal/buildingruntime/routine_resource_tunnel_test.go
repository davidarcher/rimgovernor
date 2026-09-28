package buildingruntime

import (
	"context"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

var buriedOreCell = domain.Cell{X: 14, Z: 2}

// buriedOreNative offers steel only from one compacted steel deposit inside
// a granite face east of the open colony window (x=5..16), buried until the
// cells in front of it are dug, and answers excavation site reads from the
// same rock model.
type buriedOreNative struct {
	*starvingResourceNative
	rock    map[domain.Cell]string
	support policy.ExcavationSupport
}

func (n *buriedOreNative) buried() bool {
	for _, d := range []domain.Cell{{X: -1}, {X: 1}, {Z: -1}, {Z: 1}} {
		if n.rock[domain.Cell{X: buriedOreCell.X + d.X, Z: buriedOreCell.Z + d.Z}] == "" {
			return false
		}
	}
	return true
}

func (n *buriedOreNative) ReadResourceSources(_ context.Context, _ *c.Identity, resource string) ([]bridge.ResourceSourceRow, policy.ResourceStorage, bridge.Result, error) {
	storage := policy.ResourceStorage{Resource: policy.Resource(resource), Capacity: 1000, StackLimit: 75, Haulers: 2}
	if resource != "Steel" {
		return nil, storage, bridge.Result{}, nil
	}
	buried := n.buried()
	return []bridge.ResourceSourceRow{{ThingID: "ore", Yield: 160, Distance: 6, Method: policy.ResourceSourceMine, Safety: "supported_roof", Buried: buried, Reachable: domain.Known(!buried), Cell: buriedOreCell, Token: "ore-cas"}}, storage, bridge.Result{}, nil
}

func (n *buriedOreNative) ReadExcavationSite(ctx context.Context, _ *c.Identity, cells []domain.Cell, _ domain.Cell) (bridge.ExcavationSite, bridge.Result, error) {
	site := bridge.ExcavationSite{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Support: n.support, WorkerAvailable: true, AccessReachable: true, Workers: []string{"miner"}}
	for _, cell := range cells {
		row := bridge.ExcavationSiteCell{Cell: cell}
		if def := n.rock[cell]; def != "" {
			row.Definition, row.Roof, row.HoldsRoof, row.Eligible = def, "RoofRockThick", true, true
		} else {
			row.Walkable = true
		}
		site.Cells = append(site.Cells, row)
	}
	return site, bridge.Result{}, ctx.Err()
}

// buriedOreFixture is remoteOreStep's two-worker 200-steel colony with the
// granite face in its planning window.
func buriedOreFixture(t *testing.T) (*RoutineResourcePlanner, *store.Store, *buriedOreNative) {
	t.Helper()
	base, db, _, _, sleeping := sleepingFixture(t)
	base.reviewer.policy.ResourceTargets = map[policy.Resource]int64{"Steel": 200}
	v := sleeping.reply.GetObserved()
	v.Resources = []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(0)}}
	native := &buriedOreNative{starvingResourceNative: &starvingResourceNative{resourceNative: &resourceNative{
		workshopNative: &workshopNative{sleepingNative: sleeping},
	}}, rock: map[domain.Cell]string{}, support: policy.ExcavationSupportSupported}
	v.ColonistCount = proto.Uint32(2)
	v.WorkerCount = proto.Uint32(2)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	worker := func(id string) *o.PawnState {
		return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(true)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	}
	sleeping.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{worker("crafter"), worker("builder")}, Completeness: &o.Completeness{Filtered: proto.Uint64(0)}}}}
	planning := v.Planning.GetObserved()
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(16), Z: proto.Int32(4)}
	for x := int32(5); x <= 16; x++ {
		for z := int32(0); z <= 4; z++ {
			def := "Granite"
			if (domain.Cell{X: x, Z: z}) == buriedOreCell {
				def = "MineableSteel"
			}
			native.rock[domain.Cell{X: x, Z: z}] = def
			planning.Cells.Cells = append(planning.Cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Roof: proto.String("RoofRockThick"), Indoors: proto.Bool(false), Fogged: proto.Bool(false), Walkable: proto.Bool(false), Occupied: proto.Bool(true), SupportsLight: proto.Bool(false), Issues: []*o.ReadIssue{missing("zone_id")}})
		}
	}
	base.reviewer.native = native
	if _, err := base.reviewer.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	wall, err := domain.NewBuilding("Wall", domain.Cell{X: 2, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	cells := []domain.Cell{{X: 2, Z: 2}, buriedOreCell}
	facts := &base.reviewer.census.latest.reading.Projection.Facts
	facts.CurrentConstruction = domain.Known(policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{{ID: "wall", Building: wall, Cells: cells}}})
	facts.HomeCoverage = domain.Known(policy.HomeCoverageObservation{Targets: []policy.HomeCoverageTarget{{ID: "wall", Shape: domain.Known("shape"), Cells: cells, Missing: domain.Known(int64(0)), Excluded: domain.Known(int64(0)), ExtentGeometry: domain.Known(policy.HomeExtentGeometry{})}}})
	facts.MapBounds = domain.Known(policy.Bounds{Width: 100, Height: 100})
	planner, err := NewRoutineResourcePlanner(base.reviewer, native)
	if err != nil {
		t.Fatal(err)
	}
	return planner, db, native
}

// tunnelStage returns the goal's open tunnel stage plan and its cells.
func tunnelStage(t *testing.T, db *store.Store) (domain.PlanID, []domain.Cell) {
	t.Helper()
	ctx := context.Background()
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Goals {
		if binding.Need != policy.MaintainResource {
			continue
		}
		goal, err := db.LoadGoal(ctx, binding.Goal)
		if err != nil {
			t.Fatal(err)
		}
		for _, m := range goal.Methods {
			plan, err := db.LoadPlan(ctx, m.Plan)
			if err != nil {
				t.Fatal(err)
			}
			if !IsExcavationMethod(m.Method) || !store.PlanOpen(plan) {
				continue
			}
			var cells []domain.Cell
			for _, action := range plan.Spec.Actions() {
				excavation, _ := action.Excavation()
				cells = append(cells, excavation.Cell())
			}
			return m.Plan, cells
		}
	}
	return "", nil
}

// finishTunnelStage journals the open stage as dug and clears its rock.
func finishTunnelStage(t *testing.T, db *store.Store, n *buriedOreNative, id domain.PlanID) {
	t.Helper()
	ctx := context.Background()
	plan, err := db.LoadPlan(ctx, id)
	if err != nil {
		t.Fatal(err)
	}
	review, err := db.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := review.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for _, action := range plan.Spec.Actions() {
		excavation, _ := action.Excavation()
		for _, step := range []func() error{
			func() error { _, err := db.Prepare(ctx, id, action.ID(), snapshot, 7); return err },
			func() error { _, err := db.Dispatch(ctx, id, action.ID(), snapshot, 7); return err },
			func() error { _, err := db.RecordReceipt(ctx, id, action.ID(), 1, domain.ReceiptAccepted); return err },
		} {
			if err := step(); err != nil {
				t.Fatal(err)
			}
		}
		delete(n.rock, excavation.Cell())
	}
	markBuilt(t, db, n.routineNative)
}

// Buried steel starts a corridor-only dig toward the deposit; once the
// corridor is open the deposit is mined.
func TestBuriedSteelTunnelsThenMines(t *testing.T) {
	t.Parallel()
	planner, db, native := buriedOreFixture(t)
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	id, cells := tunnelStage(t, db)
	if result.Reason != BuildingMethodAdmitted || id == "" || len(native.acquisitions) != 0 {
		t.Fatal(result, id, native.acquisitions)
	}
	stage, err := db.LoadPlan(context.Background(), id)
	if err != nil {
		t.Fatal(err)
	}
	_, target, ok := ExcavationMethod(stage.Method)
	if !ok || target.Shape.Kind != policy.ExcavationCorridor || !strings.HasSuffix(string(stage.Method), ".c") {
		t.Fatal(target, stage.Method)
	}
	if dx, dz := target.Door.X-buriedOreCell.X, target.Door.Z-buriedOreCell.Z; dx*dx+dz*dz != 1 || len(cells) == 0 {
		t.Fatal(target, cells)
	}
	for stage := 0; stage < 4 && id != ""; stage++ {
		finishTunnelStage(t, db, native, id)
		if result, err = planner.Step(context.Background()); err != nil {
			t.Fatal(err)
		}
		id, _ = tunnelStage(t, db)
	}
	if native.buried() || result.Reason != BuildingMethodAdmitted || len(native.acquisitions) != 1 || native.acquisitions[0].Token != "ore-cas" {
		t.Fatal(native.buried(), result, native.acquisitions)
	}
}

// Support lost under a corridor already begun holds its next stage.
func TestBuriedSteelLostSupportHoldsTheNextStage(t *testing.T) {
	t.Parallel()
	planner, db, native := buriedOreFixture(t)
	if _, err := planner.Step(context.Background()); err != nil {
		t.Fatal(err)
	}
	id, _ := tunnelStage(t, db)
	if id == "" {
		t.Fatal("no tunnel stage")
	}
	finishTunnelStage(t, db, native, id)
	native.support = policy.ExcavationSupportUnsupported
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if next, _ := tunnelStage(t, db); next != "" || result.Reason == BuildingMethodAdmitted || len(native.acquisitions) != 0 {
		t.Fatal(result, next, native.acquisitions)
	}
}

// A corridor whose removal native reports unsupported is never started.
func TestBuriedSteelUnsupportedCorridorHoldsTheDig(t *testing.T) {
	t.Parallel()
	planner, db, native := buriedOreFixture(t)
	native.support = policy.ExcavationSupportUnsupported
	result, err := planner.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if id, _ := tunnelStage(t, db); id != "" || result.Reason == BuildingMethodAdmitted || len(native.acquisitions) != 0 {
		t.Fatal(result, id, native.acquisitions)
	}
}
