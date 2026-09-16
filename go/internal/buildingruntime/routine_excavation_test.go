package buildingruntime

import (
	"context"
	"strconv"
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

// excavationNative answers site reads from a tiny rock model: visible rock
// cells carry a definition, fogged cells are unknown, anything else is clear.
type excavationNative struct {
	*sleepingNative
	rock    map[domain.Cell]string
	fogged  map[domain.Cell]bool
	support policy.ExcavationSupport
	worker  bool
	reads   int
	last    []domain.Cell
}

func (n *excavationNative) ReadExcavationSite(ctx context.Context, _ *c.Identity, cells []domain.Cell, access domain.Cell) (bridge.ExcavationSite, bridge.Result, error) {
	n.reads++
	n.last = append([]domain.Cell(nil), cells...)
	site := bridge.ExcavationSite{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Support: n.support, WorkerAvailable: n.worker, AccessReachable: n.worker}
	if n.worker {
		site.Workers = []string{"miner"}
	}
	for _, cell := range cells {
		row := bridge.ExcavationSiteCell{Cell: cell}
		if n.fogged[cell] {
			row.Fogged = true
		} else if def := n.rock[cell]; def != "" {
			row.Definition, row.Roof, row.HoldsRoof, row.Eligible, row.Token = def, "RoofRockThick", true, true, "tok-"+def
		} else {
			row.Walkable, row.Roof = true, "RoofRockThick"
		}
		site.Cells = append(site.Cells, row)
	}
	return site, bridge.Result{}, ctx.Err()
}

// excavationFixture extends the 9×9 open shelter site with a visible granite
// face two cells deep at x=9..10 whose interior beyond is fogged. The anchor
// sits inside the mountain so the excavated room beats the wooden shell.
func excavationFixture(t *testing.T) (*RoutineBuildingPlanner, *store.Store, *excavationNative) {
	t.Helper()
	planner, db, n := shelterFixture(t)
	x := &excavationNative{sleepingNative: n, rock: map[domain.Cell]string{}, fogged: map[domain.Cell]bool{}, support: policy.ExcavationSupportSupported, worker: true}
	planning := n.reply.GetObserved().Planning.GetObserved()
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(10), Z: proto.Int32(8)}
	for gx := int32(9); gx <= 10; gx++ {
		for z := int32(0); z < 9; z++ {
			planning.Cells.Cells = append(planning.Cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(gx), Z: proto.Int32(z)}, Roof: proto.String("RoofRockThick"), Indoors: proto.Bool(false), Fogged: proto.Bool(false), Walkable: proto.Bool(false), Occupied: proto.Bool(true), SupportsLight: proto.Bool(false), Issues: []*o.ReadIssue{
				{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
			}})
			x.rock[domain.Cell{X: gx, Z: z}] = "Granite"
		}
	}
	planning.Cells.Completeness.Matched, planning.Cells.Completeness.Returned = proto.Uint64(99), proto.Uint64(99)
	for gx := int32(11); gx < 30; gx++ {
		for z := int32(0); z < 20; z++ {
			x.fogged[domain.Cell{X: gx, Z: z}] = true
		}
	}
	n.reply.GetObserved().Center = &c.Cell{X: proto.Int32(12), Z: proto.Int32(4)}
	planner.excavation = x
	return planner, db, x
}

var excavationTestTarget = policy.ExcavationTarget{Access: domain.Cell{X: 8, Z: 4}, Direction: domain.Cell{X: 1, Z: 0}, Corridor: []domain.Cell{{X: 9, Z: 4}, {X: 10, Z: 4}}, Door: domain.Cell{X: 10, Z: 4}, Interior: policy.Rectangle{X: 11, Z: 1, Width: 7, Height: 7}}

// completeExcavation journals every action of a stage plan as dispatched and
// then observed complete, the way the executor does after pawns dig.
func methodPlan(t *testing.T, decision store.BuildingMethodDecision, method domain.MethodID) domain.PlanID {
	t.Helper()
	for _, m := range decision.Goal.Methods {
		if m.Method == method {
			return m.Plan
		}
	}
	t.Fatal("method not admitted", method, decision.Goal.Methods)
	return ""
}

func completeExcavation(t *testing.T, db *store.Store, decision store.BuildingMethodDecision, method domain.MethodID, x *excavationNative) {
	t.Helper()
	ctx := context.Background()
	planID := methodPlan(t, decision, method)
	plan, err := db.LoadPlan(ctx, planID)
	if err != nil {
		t.Fatal(err)
	}
	snapshot := decision.Goal.Goal.Snapshot
	snapshot.Plan, snapshot.Revision = plan.Spec.ID(), plan.Spec.Revision()
	for _, action := range plan.Spec.Actions() {
		if action.Kind() == domain.ExcavationAction {
			excavation, _ := action.Excavation()
			if _, err := db.PrepareExcavation(ctx, planID, action.ID(), store.ExcavationAdmission{Snapshot: snapshot, Tick: 7, Cell: excavation.Cell(), Definition: excavation.Definition(), SnapshotToken: "tok-" + excavation.Definition()}); err != nil {
				t.Fatal(err)
			}
			delete(x.rock, excavation.Cell())
		} else {
			b, _ := action.Building()
			if _, err := db.ReserveAndPrepare(ctx, planID, action.ID(), store.Admission{Snapshot: snapshot, Tick: 7, Costs: []store.MaterialCost{{Definition: "WoodLog", Count: 25}}, Footprint: []domain.Cell{b.Cell()}}); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := db.Dispatch(ctx, planID, action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.RecordReceipt(ctx, planID, action.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
		if _, err := db.Observe(ctx, planID, domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 7, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot); err != nil {
			t.Fatal(err)
		}
	}
}

func excavationCells(t *testing.T, db *store.Store, decision store.BuildingMethodDecision, method domain.MethodID) (domain.PlanID, []domain.Cell) {
	t.Helper()
	planID := methodPlan(t, decision, method)
	plan, err := db.LoadPlan(context.Background(), planID)
	if err != nil {
		t.Fatal(err)
	}
	var cells []domain.Cell
	for _, action := range plan.Spec.Actions() {
		excavation, ok := action.Excavation()
		if !ok {
			t.Fatal("non-excavation action in stage", action)
		}
		cells = append(cells, excavation.Cell())
	}
	return planID, cells
}

func TestRoutineExcavationDigsStagesThenDoorThenRests(t *testing.T) {
	t.Parallel()
	r, db, x := excavationFixture(t)
	ctx := context.Background()
	// Stage 0: only the two visible corridor cells can be designated; the
	// wooden shell was previewed but lost on anchor distance.
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	planID, cells := excavationCells(t, db, result.Decision, excavationStageMethod(0))
	if planID != excavationPlanID(excavationTestTarget, "0") || len(cells) != 2 || cells[0] != (domain.Cell{X: 9, Z: 4}) || cells[1] != (domain.Cell{X: 10, Z: 4}) {
		t.Fatal(planID, cells)
	}
	if result.Decision.Goal.Methods[0].Method != excavationStageMethod(0) || x.sleepingNative.previews != 32 || x.reads < 1 {
		t.Fatal(result.Decision.Goal.Methods, x.sleepingNative.previews, x.reads)
	}
	if again, err := r.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	// Corridor cleared; the first interior column is now visible rock.
	completeExcavation(t, db, result.Decision, excavationStageMethod(0), x)
	for z := int32(1); z <= 7; z++ {
		delete(x.fogged, domain.Cell{X: 11, Z: z})
		x.rock[domain.Cell{X: 11, Z: z}] = "Granite"
	}
	result, err = r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	planID, cells = excavationCells(t, db, result.Decision, excavationStageMethod(1))
	if planID != excavationPlanID(excavationTestTarget, "1") || len(cells) != 7 || cells[0] != (domain.Cell{X: 11, Z: 4}) {
		t.Fatal(planID, cells)
	}
	if x.sleepingNative.previews != 32 {
		t.Fatal("stage re-previewed the shell", x.sleepingNative.previews)
	}
	// Every remaining interior cell becomes visible; stages are capped at 8.
	completeExcavation(t, db, result.Decision, excavationStageMethod(1), x)
	for gx := int32(12); gx <= 17; gx++ {
		for z := int32(1); z <= 7; z++ {
			delete(x.fogged, domain.Cell{X: gx, Z: z})
			x.rock[domain.Cell{X: gx, Z: z}] = "Granite"
		}
	}
	stage := 2
	for ; stage < 12; stage++ {
		result, err = r.Step(ctx)
		if err != nil || result.Reason != BuildingMethodAdmitted {
			t.Fatal(stage, result, err)
		}
		planID, cells = excavationCells(t, db, result.Decision, excavationStageMethod(stage))
		if planID != excavationPlanID(excavationTestTarget, strconv.Itoa(stage)) || len(cells) == 0 || len(cells) > excavationStageLimit {
			t.Fatal(planID, cells)
		}
		completeExcavation(t, db, result.Decision, excavationStageMethod(stage), x)
		remaining := 0
		for _, cell := range excavationTestTarget.Cells() {
			if x.rock[cell] != "" {
				remaining++
			}
		}
		if remaining == 0 {
			break
		}
	}
	if stage != 7 { // 42 remaining cells over stages 2..7, frontier-limited
		t.Fatal("unexpected stage count", stage)
	}
	// Target clear: one wooden door at the corridor's end closes the room.
	result, err = r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	doorPlan := methodPlan(t, result.Decision, excavationDoorMethod)
	plan, err := db.LoadPlan(ctx, doorPlan)
	if err != nil || doorPlan != excavationPlanID(excavationTestTarget, "door") || len(plan.Spec.Actions()) != 1 {
		t.Fatal(doorPlan, plan, err)
	}
	door, _ := plan.Spec.Actions()[0].Building()
	if door.Definition() != "Door" || door.Cell() != excavationTestTarget.Door || door.Stuff() != "WoodLog" {
		t.Fatal(door)
	}
	if again, err := r.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	completeExcavation(t, db, result.Decision, excavationDoorMethod, x)
	if done, err := r.Step(ctx); err != nil || done.Reason != BuildingMethodUsed {
		t.Fatal(done, err)
	}
	plans, err := db.LoadPlans(ctx, 256)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range plans {
		if strings.HasPrefix(string(p.Spec.ID()), "routine-shell") {
			t.Fatal("shell admitted beside excavation", p.Spec.ID())
		}
	}
}

func TestRoutineExcavationPrefersNearerShell(t *testing.T) {
	t.Parallel()
	r, db, x := excavationFixture(t)
	x.reply.GetObserved().Center = &c.Cell{X: proto.Int32(2), Z: proto.Int32(2)}
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || x.sleepingNative.previews != 32 {
		t.Fatal(result, err, x.sleepingNative.previews)
	}
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[0].Plan)
	if err != nil || !strings.HasPrefix(string(plan.Spec.ID()), "routine-shell") || len(plan.Progress) != 32 {
		t.Fatal(plan, err)
	}
}

func TestRoutineExcavationNeverStartsWithoutNativeSupportOrMiner(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"unsupported", "no-miner", "ineligible", "unknown-support"} {
		t.Run(change, func(t *testing.T) {
			r, db, x := excavationFixture(t)
			switch change {
			case "unsupported":
				x.support = policy.ExcavationSupportUnsupported
			case "unknown-support":
				x.support = policy.ExcavationSupportUnknown
			case "no-miner":
				x.worker = false
			case "ineligible":
				// Native sees the inner column already cleared: no candidate
				// through it is clean rock.
				for z := int32(0); z < 9; z++ {
					delete(x.rock, domain.Cell{X: 10, Z: z})
				}
			}
			result, err := r.Step(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			plan, loadErr := db.LoadPlan(context.Background(), excavationPlanID(excavationTestTarget, "0"))
			switch change {
			case "unknown-support":
				// Unknown support is not a refusal for the site choice; the
				// stage read decides, and it must not admit.
				if result.Reason == BuildingMethodAdmitted && loadErr == nil && len(plan.Progress) > 0 {
					t.Fatal("unknown support admitted", result)
				}
			default:
				if result.Reason != BuildingMethodAdmitted || loadErr == nil {
					t.Fatal("expected shell fallback", change, result, loadErr)
				}
			}
		})
	}
}

func TestRoutineExcavationHoldsWhenFrontierUnknown(t *testing.T) {
	t.Parallel()
	r, db, x := excavationFixture(t)
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	completeExcavation(t, db, result.Decision, excavationStageMethod(0), x)
	// The interior stays fogged: nothing can be designated and the project
	// waits for the next observation rather than falling back to a shell.
	next, err := r.Step(context.Background())
	if err != nil || next.Reason != BuildingMethodUnknown || x.sleepingNative.previews != 32 {
		t.Fatal(next, err, x.sleepingNative.previews)
	}
}

func TestExcavationPlanTargetRoundTrip(t *testing.T) {
	t.Parallel()
	for _, suffix := range []string{"0", "17", "door"} {
		target, err := excavationPlanTarget(excavationPlanID(excavationTestTarget, suffix))
		if err != nil || target.Key() != excavationTestTarget.Key() || target.Door != excavationTestTarget.Door {
			t.Fatal(suffix, target, err)
		}
	}
	for _, bad := range []domain.PlanID{"routine-shell-abc", "routine-excavation-", "routine-excavation-x-0"} {
		if _, err := excavationPlanTarget(bad); err == nil {
			t.Fatal("accepted", bad)
		}
	}
}
