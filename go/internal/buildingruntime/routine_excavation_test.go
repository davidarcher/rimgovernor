package buildingruntime

import (
	"context"
	"fmt"
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
	blocked map[domain.Cell]bool
	// hazard cells unfogged into something that is not rock and not open
	// ground (an ancient wall, deep water): no definition, not walkable.
	hazard map[domain.Cell]bool
	// sealed access cells have no visible walkable neighbour.
	sealed   map[domain.Cell]bool
	support  policy.ExcavationSupport
	collapse bool
	worker   bool
	reads    int
	last     []domain.Cell
}

func (n *excavationNative) ReadExcavationSite(ctx context.Context, _ *c.Identity, cells []domain.Cell, access domain.Cell) (bridge.ExcavationSite, bridge.Result, error) {
	n.reads++
	n.last = append([]domain.Cell(nil), cells...)
	site := bridge.ExcavationSite{Context: proto.Clone(n.reply.GetObserved().Context).(*c.ObservationContext), Support: n.support, CollapsePending: n.collapse, WorkerAvailable: n.worker, AccessReachable: n.worker}
	if n.sealed[access] {
		site.WorkerAvailable, site.AccessReachable = false, false
	}
	if site.WorkerAvailable {
		site.Workers = []string{"miner"}
	}
	for _, cell := range cells {
		row := bridge.ExcavationSiteCell{Cell: cell}
		if n.fogged[cell] {
			row.Fogged = true
		} else if n.hazard[cell] {
			row.Roof, row.HoldsRoof, row.Blocker = "RoofRockThick", true, "No native rock at cell"
		} else if def := n.rock[cell]; def != "" {
			row.Definition, row.Roof, row.HoldsRoof, row.Eligible = def, "RoofRockThick", true, !n.blocked[cell]
		} else {
			row.Walkable, row.Roof = true, "RoofRockThick"
		}
		site.Cells = append(site.Cells, row)
	}
	return site, bridge.Result{}, ctx.Err()
}

// excavationFixture extends the 9×9 open shelter site with a visible granite
// face two cells deep at x=9..10 whose interior beyond is fogged; the whole
// 30×20 window is the observed region. The anchor sits near the face so the
// excavated room is within reach and beats the wooden shell.
func excavationFixture(t *testing.T) (*RoutineBuildingPlanner, *store.Store, *excavationNative) {
	t.Helper()
	planner, db, n := shelterSiteFixture(t)
	x := &excavationNative{sleepingNative: n, rock: map[domain.Cell]string{}, fogged: map[domain.Cell]bool{}, blocked: map[domain.Cell]bool{}, hazard: map[domain.Cell]bool{}, sealed: map[domain.Cell]bool{}, support: policy.ExcavationSupportSupported, worker: true}
	planning := n.reply.GetObserved().Planning.GetObserved()
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(29), Z: proto.Int32(19)}
	for gx := int32(9); gx <= 10; gx++ {
		for z := int32(0); z < 9; z++ {
			planning.Cells.Cells = append(planning.Cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(gx), Z: proto.Int32(z)}, Roof: proto.String("RoofRockThick"), Indoors: proto.Bool(false), Fogged: proto.Bool(false), Walkable: proto.Bool(false), Occupied: proto.Bool(true), SupportsLight: proto.Bool(false), Issues: []*o.ReadIssue{
				{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
			}})
			x.rock[domain.Cell{X: gx, Z: z}] = "Granite"
		}
	}
	fogRest(planning.Cells)
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
		if matchesMethod(m.Method, method) {
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
			if _, err := db.Prepare(ctx, planID, action.ID(), snapshot, 7); err != nil {
				t.Fatal(err)
			}
			delete(x.rock, excavation.Cell())
		} else {
			if _, err := db.Prepare(ctx, planID, action.ID(), snapshot, 7); err != nil {
				t.Fatal(err)
			}
		}
		if _, err := db.Dispatch(ctx, planID, action.ID(), snapshot, 7); err != nil {
			t.Fatal(err)
		}
		if _, err := db.RecordReceipt(ctx, planID, action.ID(), 1, domain.ReceiptAccepted); err != nil {
			t.Fatal(err)
		}
	}
	markBuilt(t, db, x.routineNative)
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
	// wooden shell lost on anchor distance before anything was previewed.
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	planID, cells := excavationCells(t, db, result.Decision, stageStub(0))
	if !excavationPlanFor(db, planID, excavationTestTarget, "0") || len(cells) != 2 || cells[0] != (domain.Cell{X: 9, Z: 4}) || cells[1] != (domain.Cell{X: 10, Z: 4}) {
		t.Fatal(planID, cells)
	}
	if !matchesMethod(result.Decision.Goal.Methods[0].Method, stageStub(0)) || x.sleepingNative.previews != 0 || x.reads < 1 {
		t.Fatal(result.Decision.Goal.Methods, x.sleepingNative.previews, x.reads)
	}
	if again, err := r.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	// Corridor cleared; the first interior column is now visible rock.
	completeExcavation(t, db, result.Decision, stageStub(0), x)
	for z := int32(1); z <= 7; z++ {
		delete(x.fogged, domain.Cell{X: 11, Z: z})
		x.rock[domain.Cell{X: 11, Z: z}] = "Granite"
	}
	result, err = r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	planID, cells = excavationCells(t, db, result.Decision, stageStub(1))
	if !excavationPlanFor(db, planID, excavationTestTarget, "1") || len(cells) != 7 || cells[0] != (domain.Cell{X: 11, Z: 4}) {
		t.Fatal(planID, cells)
	}
	if x.sleepingNative.previews != 0 {
		t.Fatal("stage previewed the shell", x.sleepingNative.previews)
	}
	// Every remaining interior cell becomes visible; stages are capped at 8.
	completeExcavation(t, db, result.Decision, stageStub(1), x)
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
		planID, cells = excavationCells(t, db, result.Decision, stageStub(stage))
		if !excavationPlanFor(db, planID, excavationTestTarget, strconv.Itoa(stage)) || len(cells) == 0 || len(cells) > excavationStageLimit {
			t.Fatal(planID, cells)
		}
		completeExcavation(t, db, result.Decision, stageStub(stage), x)
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
	doorPlan := methodPlan(t, result.Decision, doorStub)
	plan, err := db.LoadPlan(ctx, doorPlan)
	if err != nil || !excavationPlanFor(db, doorPlan, excavationTestTarget, "door") || len(plan.Spec.Actions()) != 1 {
		t.Fatal(doorPlan, plan, err)
	}
	door, _ := plan.Spec.Actions()[0].Building()
	if door.Definition() != "Door" || door.Cell() != excavationTestTarget.Door || door.Stuff() != "WoodLog" {
		t.Fatal(door)
	}
	if again, err := r.Step(ctx); err != nil || again.Reason != BuildingMethodExistingWork {
		t.Fatal(again, err)
	}
	completeExcavation(t, db, result.Decision, doorStub, x)
	// Census-aware retirement closes the applied door (#856).
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	if done, err := r.Step(ctx); err != nil || done.Reason != BuildingMethodUsed {
		t.Fatal(done, err)
	}
	plans, err := db.LoadPlans(ctx, 256)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range plans {
		if IsShellMethod(p.Method) {
			t.Fatal("shell admitted beside excavation", p.Spec.ID())
		}
	}
}

func TestRoutineExcavationPrefersNearerShell(t *testing.T) {
	t.Parallel()
	r, db, x := excavationFixture(t)
	// The colony sits far down the map: the dig is out of reach and the
	// shell next to the colonists wins.
	x.reply.GetObserved().Center = &c.Cell{X: proto.Int32(2), Z: proto.Int32(40)}
	planning := x.reply.GetObserved().Planning.GetObserved()
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(29), Z: proto.Int32(50)}
	for gx := int32(0); gx < 9; gx++ {
		for z := int32(36); z < 45; z++ {
			planning.Cells.Cells = append(planning.Cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(gx), Z: proto.Int32(z)}, Indoors: proto.Bool(false), Fogged: proto.Bool(false), Walkable: proto.Bool(true), Occupied: proto.Bool(false), SupportsLight: proto.Bool(true), Issues: []*o.ReadIssue{
				{Field: proto.String("zone_id"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
				{Field: proto.String("roof"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}},
			}})
		}
	}
	fogRest(planning.Cells)
	// The surface site takes the bunks first, then the ring around them.
	stageShelterBunks(t, r, db, x.sleepingNative)
	result, err := r.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || x.sleepingNative.previews != 32 {
		t.Fatal(result, err, x.sleepingNative.previews)
	}
	plan, err := db.LoadPlan(context.Background(), shellMethod(result.Decision.Goal).Plan)
	if err != nil || !IsShellMethod(plan.Method) || len(plan.Progress) != 32 {
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
				// Native refuses the inner column (faction-owned rock, a
				// frame, a map-edge neighbour): no candidate through it is
				// clean.
				for z := int32(0); z < 9; z++ {
					x.blocked[domain.Cell{X: 10, Z: z}] = true
				}
			}
			result, err := r.Step(context.Background())
			if err != nil {
				t.Fatal(err)
			}
			plan, loadErr := loadMethodPlan(db, result.Decision.Goal.Goal.ID, stageStub(0))
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
	completeExcavation(t, db, result.Decision, stageStub(0), x)
	// The interior stays fogged: nothing can be designated and the project
	// waits for the next observation rather than falling back to a shell.
	next, err := r.Step(context.Background())
	if err != nil || next.Reason != BuildingMethodUnknown || x.sleepingNative.previews != 0 {
		t.Fatal(next, err, x.sleepingNative.previews)
	}
}

func TestExcavationMethodTargetRoundTrip(t *testing.T) {
	t.Parallel()
	// The target key rides on the method id (#987).
	for _, method := range []domain.MethodID{excavationStageMethod(0, excavationTestTarget), excavationStageMethod(17, excavationTestTarget), excavationDoorMethod(excavationTestTarget)} {
		stage, target, ok := ExcavationMethod(method)
		if !ok || target.Key() != excavationTestTarget.Key() || target.Door != excavationTestTarget.Door || (method == excavationDoorMethod(excavationTestTarget)) != (stage < 0) {
			t.Fatal(method, stage, target, ok)
		}
	}
	west, err := policy.ParseExcavationKey("20.15.-1.0.3.10.12.7.7")
	if err != nil {
		t.Fatal(err)
	}
	if stage, back, ok := ExcavationMethod(excavationStageMethod(4, west)); !ok || stage != 4 || back.Key() != west.Key() || back.Corridor[0] != (domain.Cell{X: 19, Z: 15}) {
		t.Fatal(back, ok)
	}
	for _, bad := range []domain.MethodID{"starter-shell", "excavation-stage-0", "excavation-stage-x@" + domain.MethodID(west.Key()), "excavation-stage-01@" + domain.MethodID(west.Key()), "excavation-door@x"} {
		if IsExcavationMethod(bad) {
			t.Fatal("accepted", bad)
		}
	}
}

func TestRoutineExcavationReadoptsHalfDugTarget(t *testing.T) {
	t.Parallel()
	// The corridor was dug under an earlier, since invalidated, goal: the
	// observation shows it open under rock roof and native reports it
	// cleared. The planner re-adopts the same target and designates only
	// the next frontier cell, never the cleared corridor.
	r, db, x := excavationFixture(t)
	planning := x.reply.GetObserved().Planning.GetObserved()
	for _, row := range planning.Cells.Cells {
		if row.Cell.GetZ() == 4 && (row.Cell.GetX() == 9 || row.Cell.GetX() == 10) {
			row.Walkable, row.Occupied = proto.Bool(true), proto.Bool(false)
			delete(x.rock, domain.Cell{X: row.Cell.GetX(), Z: 4})
		}
	}
	delete(x.fogged, domain.Cell{X: 11, Z: 4})
	x.rock[domain.Cell{X: 11, Z: 4}] = "Granite"
	result, err := r.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if result.Reason != BuildingMethodAdmitted {
		t.Fatal(result)
	}
	planID, cells := excavationCells(t, db, result.Decision, stageStub(0))
	if !excavationPlanFor(db, planID, excavationTestTarget, "0") {
		t.Fatal("re-planned a different target", planID)
	}
	if len(cells) != 1 || cells[0] != (domain.Cell{X: 11, Z: 4}) {
		t.Fatal("stage should hold only the frontier cell", cells)
	}
}

func TestRoutineExcavationResumesProjectOutsideColonyWindow(t *testing.T) {
	t.Parallel()
	// Stage 0 was planned; then authority was re-acquired while the pawns
	// wandered so far that the colony window no longer shows the block at
	// all. The goal (or, after a world-changing review, its successor)
	// resumes the same target from the durable stage plan, verified by the
	// native site read, instead of proposing a fresh face or the wooden
	// shell.
	r, db, x := excavationFixture(t)
	ctx := context.Background()
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	first := result.Decision.Goal.Goal.ID
	completeExcavation(t, db, result.Decision, stageStub(0), x)
	for z := int32(1); z <= 7; z++ {
		delete(x.fogged, domain.Cell{X: 11, Z: z})
		x.rock[domain.Cell{X: 11, Z: z}] = "Granite"
	}
	session := r.reviewer.player.session.(*playerFakeSession)
	session.mu.Lock()
	session.state.Snapshot.Native++
	generation := uint64(session.state.Snapshot.Native)
	session.mu.Unlock()
	observedReply := x.sleepingNative.reply.GetObserved()
	observedReply.Context.NativeGeneration = proto.Uint64(generation)
	observedReply.Planning.GetObserved().Cells.Context.NativeGeneration = proto.Uint64(generation)
	observed := x.sleepingNative.reply.GetObserved()
	planning := observed.Planning.GetObserved()
	planning.Cells.Region.Minimum = &c.Cell{X: proto.Int32(40), Z: proto.Int32(40)}
	planning.Cells.Region.Maximum = &c.Cell{X: proto.Int32(60), Z: proto.Int32(59)}
	planning.Cells.Cells = nil
	fogRest(planning.Cells)
	observed.Center = &c.Cell{X: proto.Int32(50), Z: proto.Int32(50)}
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	result, err = r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	// A re-acquire alone keeps the goal, so the project continues at the
	// next stage under the same target key.
	if result.Decision.Goal.Goal.ID != first {
		t.Fatal("goal was replaced by a re-acquire")
	}
	planID, cells := excavationCells(t, db, result.Decision, stageStub(1))
	if !excavationPlanFor(db, planID, excavationTestTarget, "1") || len(cells) != 7 || cells[0] != (domain.Cell{X: 11, Z: 4}) {
		t.Fatal(planID, cells)
	}
}

func TestRoutineExcavationResumedProjectStillOwesDoor(t *testing.T) {
	t.Parallel()
	// Every cell was cleared under earlier goals; the successor goal owes the
	// door even though the target now holds no rock and the geometry search
	// would never propose an open pocket.
	r, db, x := excavationFixture(t)
	ctx := context.Background()
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	completeExcavation(t, db, result.Decision, stageStub(0), x)
	for gx := int32(11); gx <= 17; gx++ {
		for z := int32(1); z <= 7; z++ {
			delete(x.fogged, domain.Cell{X: gx, Z: z})
		}
	}
	session := r.reviewer.player.session.(*playerFakeSession)
	session.mu.Lock()
	session.state.Snapshot.Native++
	generation := uint64(session.state.Snapshot.Native)
	session.mu.Unlock()
	observedReply := x.sleepingNative.reply.GetObserved()
	observedReply.Context.NativeGeneration = proto.Uint64(generation)
	observedReply.Planning.GetObserved().Cells.Context.NativeGeneration = proto.Uint64(generation)
	if _, err := r.reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	result, err = r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	if plan := methodPlan(t, result.Decision, doorStub); !excavationPlanFor(db, plan, excavationTestTarget, "door") {
		t.Fatal(plan)
	}
	// Once the door plan completed, a later shelter need starts afresh.
	completeExcavation(t, db, result.Decision, doorStub, x)
	if previous, err := r.previousExcavation(ctx); err != nil || previous != nil {
		t.Fatal(previous, err)
	}
}

// digExcavation runs the planner until the stage plans stop, completing
// each admitted stage the way the executor does, and returns the last
// result plus every cell any stage designated.
func digExcavation(t *testing.T, r *RoutineBuildingPlanner, db *store.Store, x *excavationNative, reveal func(stage int)) (RoutineBuildingResult, map[domain.Cell]int) {
	t.Helper()
	ctx := context.Background()
	dug := map[domain.Cell]int{}
	for stage := 0; stage < 16; stage++ {
		result, err := r.Step(ctx)
		if err != nil {
			t.Fatal(stage, err)
		}
		if result.Reason != BuildingMethodAdmitted {
			return result, dug
		}
		method := stageStub(stage)
		found := false
		for _, m := range result.Decision.Goal.Methods {
			found = found || matchesMethod(m.Method, method)
		}
		if !found {
			return result, dug
		}
		_, cells := excavationCells(t, db, result.Decision, method)
		for _, c := range cells {
			if prev, seen := dug[c]; seen {
				t.Fatal("cell designated twice", c, prev, stage)
			}
			dug[c] = stage
		}
		completeExcavation(t, db, result.Decision, method, x)
		reveal(stage)
	}
	t.Fatal("stages never ended")
	return RoutineBuildingResult{}, nil
}

func revealInterior(x *excavationNative) {
	for gx := int32(11); gx <= 17; gx++ {
		for z := int32(1); z <= 7; z++ {
			if !x.hazard[domain.Cell{X: gx, Z: z}] {
				x.rock[domain.Cell{X: gx, Z: z}] = "Granite"
			}
			delete(x.fogged, domain.Cell{X: gx, Z: z})
		}
	}
}

func TestRoutineExcavationDigsAroundRevealedHazard(t *testing.T) {
	t.Parallel()
	// Two interior cells unfog into an ancient wall (no rock, not
	// walkable, never eligible). They are never designated; the room is
	// dug around them and closed with the door as usual.
	r, db, x := excavationFixture(t)
	hazards := []domain.Cell{{X: 15, Z: 2}, {X: 15, Z: 3}}
	for _, h := range hazards {
		x.hazard[h] = true
	}
	result, dug := digExcavation(t, r, db, x, func(stage int) {
		if stage == 0 {
			revealInterior(x)
		}
	})
	if result.Reason != BuildingMethodAdmitted {
		t.Fatal(result)
	}
	doorPlan := methodPlan(t, result.Decision, doorStub)
	if !excavationPlanFor(db, doorPlan, excavationTestTarget, "door") {
		t.Fatal(doorPlan)
	}
	for _, h := range hazards {
		if stage, ok := dug[h]; ok {
			t.Fatal("hazard designated", h, stage)
		}
	}
	if len(dug) != 51-len(hazards) {
		t.Fatal("dug cells", len(dug))
	}
	for _, cell := range excavationTestTarget.Cells() {
		if x.rock[cell] != "" {
			t.Fatal("rock left standing", cell)
		}
	}
}

func TestRoutineExcavationBlockedEntranceIsResited(t *testing.T) {
	t.Parallel()
	// The cell past the door unfogs into a structure: the corridor can
	// never open into the room. The project is dropped and the shelter
	// re-sited; here no other face verifies, so the shell wins.
	r, db, x := excavationFixture(t)
	x.hazard[domain.Cell{X: 11, Z: 4}] = true
	result, dug := digExcavation(t, r, db, x, func(stage int) {
		if stage == 0 {
			revealInterior(x)
			for z := int32(0); z < 9; z++ {
				if z != 4 {
					x.blocked[domain.Cell{X: 9, Z: z}], x.blocked[domain.Cell{X: 10, Z: z}] = true, true
				}
			}
		}
	})
	if len(dug) != 2 || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, dug)
	}
	result = finishShelterBunks(t, r, db, result)
	plan, err := db.LoadPlan(context.Background(), result.Decision.Goal.Methods[len(result.Decision.Goal.Methods)-1].Plan)
	if err != nil || !IsShellMethod(plan.Method) {
		t.Fatal(plan.Spec.ID(), err)
	}
}

func TestRoutineExcavationBreachedRoofAbandonsTheDig(t *testing.T) {
	t.Parallel()
	// After the corridor, the rock around the room is gone (the player
	// levelled it): removing the rest would leave the roof unsupported and
	// no collapse is pending, so nothing sound can be finished here. No
	// further stage is admitted under the target and the shell is sited.
	r, db, x := excavationFixture(t)
	result, dug := digExcavation(t, r, db, x, func(stage int) {
		if stage == 0 {
			revealInterior(x)
			x.support = policy.ExcavationSupportUnsupported
		}
	})
	if len(dug) != 2 || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, dug)
	}
	result = finishShelterBunks(t, r, db, result)
	var shell bool
	for _, m := range result.Decision.Goal.Methods {
		if IsShellMethod(m.Method) {
			shell = true
		}
		if matchesMethod(m.Method, stageStub(1)) {
			t.Fatal("stage admitted under a breached target", m)
		}
	}
	if !shell {
		t.Fatal("shell not sited", result.Decision.Goal.Methods)
	}
	// A pending collapse is transient: the project waits instead.
	r2, db2, x2 := excavationFixture(t)
	first, err := r2.Step(context.Background())
	if err != nil || first.Reason != BuildingMethodAdmitted {
		t.Fatal(first, err)
	}
	completeExcavation(t, db2, first.Decision, stageStub(0), x2)
	revealInterior(x2)
	x2.support = policy.ExcavationSupportUnsupported
	x2.collapse = true
	held, err := r2.Step(context.Background())
	if err != nil || held.Reason != BuildingMethodUnknown {
		t.Fatal(held, err)
	}
}

func TestRoutineExcavationSealedAccessIsResited(t *testing.T) {
	t.Parallel()
	// The access cell is walled off after the corridor is dug: the site
	// read reports it unreachable. The project continues from another
	// face: the next stage is admitted under a new target key with a
	// reachable access cell, and the old corridor is never designated
	// again.
	r, db, x := excavationFixture(t)
	result, dug := digExcavation(t, r, db, x, func(stage int) {
		if stage == 0 {
			x.sealed[excavationTestTarget.Access] = true
			// The old corridor is enclosed now; the planning window shows
			// its cells as walls of the sealed pocket.
			planning := x.reply.GetObserved().Planning.GetObserved()
			for _, row := range planning.Cells.Cells {
				if row.Cell.GetZ() == 4 && (row.Cell.GetX() == 8 || row.Cell.GetX() == 9 || row.Cell.GetX() == 10) {
					row.Walkable, row.Occupied = proto.Bool(false), proto.Bool(true)
				}
			}
		}
	})
	if len(dug) != 4 || result.Reason != BuildingMethodUnknown {
		t.Fatal(result, dug)
	}
	goalID := goalIDFor(t, db)
	goal, err := db.LoadGoal(context.Background(), goalID)
	if err != nil {
		t.Fatal(err)
	}
	stage1, err := loadMethodPlan(db, goal.Goal.ID, stageStub(1))
	if err != nil {
		t.Fatal(err)
	}
	_, target, ok := ExcavationMethod(stage1.Method)
	if !ok || target.Key() == excavationTestTarget.Key() || target.Access == excavationTestTarget.Access {
		t.Fatal("stage 1 not re-sited", stage1.Method)
	}
	for _, c := range target.Corridor {
		if _, ok := dug[c]; !ok {
			t.Fatal("new corridor not designated", c, dug)
		}
	}
	for _, m := range goal.Methods {
		if IsShellMethod(m.Method) {
			t.Fatal("shell sited while another face verified", m)
		}
	}
}

func goalIDFor(t *testing.T, db *store.Store) domain.GoalID {
	t.Helper()
	review, err := db.LoadRoutineReview(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	for _, binding := range review.Goals {
		if binding.Need == policy.MaintainHousing {
			return binding.Goal
		}
	}
	t.Fatal("no shelter goal", review.Goals)
	return ""
}

func TestCancelStalledExcavation(t *testing.T) {
	t.Parallel()
	r, db, _ := excavationFixture(t)
	ctx := context.Background()
	result, err := r.Step(ctx)
	if err != nil || result.Reason != BuildingMethodAdmitted {
		t.Fatal(result, err)
	}
	planID, _ := excavationCells(t, db, result.Decision, stageStub(0))
	action := domain.ActionID(fmt.Sprintf("%s-%d", planID, 0))
	if _, err := db.Hold(ctx, planID, action, []domain.HeldReason{domain.HeldNotReady}, 7); err != nil {
		t.Fatal(err)
	}
	goal := result.Decision.Goal
	// Within the grace the hold stands and the stage stays open.
	if err := cancelStalledExcavation(ctx, db, goal, 7+excavationStallTicks-1); err != nil {
		t.Fatal(err)
	}
	plan, _ := db.LoadPlan(ctx, planID)
	if !store.PlanOpen(plan) {
		t.Fatal("stage cancelled within the grace")
	}
	// Past it, only the held action is cancelled; the other stays pending.
	if err := cancelStalledExcavation(ctx, db, goal, 7+excavationStallTicks); err != nil {
		t.Fatal(err)
	}
	plan, _ = db.LoadPlan(ctx, planID)
	for _, p := range plan.Progress {
		v := p.View()
		if v.Action == action && v.Stage != domain.Cancelled || v.Action != action && v.Stage != domain.Pending {
			t.Fatal(v.Action, v.Stage)
		}
	}
}

// finishShelterBunks completes the bunk rungs a re-sited shelter admits
// once its dig is dropped (#612) and returns the Step that follows them,
// which sites the ring.
func finishShelterBunks(t *testing.T, r *RoutineBuildingPlanner, db *store.Store, result RoutineBuildingResult) RoutineBuildingResult {
	t.Helper()
	for rung := 0; rung < 2; rung++ {
		methodPlan(t, result.Decision, []domain.MethodID{shelterSpotsMethod, shelterBedsMethod}[rung])
		completeRoutineBuildingMethod(t, db, result)
		var err error
		if result, err = r.Step(context.Background()); err != nil || result.Reason != BuildingMethodAdmitted {
			t.Fatal(result, err)
		}
	}
	return result
}

// fogRest lists every region cell the fixture left out as fogged, as the
// native lists every cell of a planning window.
func fogRest(cells *o.CellsSnapshot) {
	listed := map[[2]int32]bool{}
	for _, row := range cells.Cells {
		listed[[2]int32{row.GetCell().GetX(), row.GetCell().GetZ()}] = true
	}
	for x := cells.Region.Minimum.GetX(); x <= cells.Region.Maximum.GetX(); x++ {
		for z := cells.Region.Minimum.GetZ(); z <= cells.Region.Maximum.GetZ(); z++ {
			if !listed[[2]int32{x, z}] {
				cells.Cells = append(cells.Cells, &o.CellState{Cell: &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)}, Fogged: proto.Bool(true)})
			}
		}
	}
}

// excavationPlanFor reports whether id is a plan admitted under the stage
// (or "door") method for target: the key rides on the stored method (#987).
func excavationPlanFor(db *store.Store, id domain.PlanID, target policy.ExcavationTarget, suffix string) bool {
	plan, err := db.LoadPlan(context.Background(), id)
	if err != nil {
		return false
	}
	stage, back, ok := ExcavationMethod(plan.Method)
	return ok && back.Key() == target.Key() && (suffix == "door" && stage < 0 || strconv.Itoa(stage) == suffix)
}

// stageStub and doorStub name an excavation method without its target key;
// matchesMethod accepts the stub for any target.
func stageStub(stage int) domain.MethodID {
	return domain.MethodID(fmt.Sprintf("%s%d", excavationStagePrefix, stage))
}

const doorStub domain.MethodID = excavationDoorPrefix

func matchesMethod(method, want domain.MethodID) bool {
	return method == want || strings.HasPrefix(string(method), string(want)+"@")
}

// loadMethodPlan loads the plan goal's current epoch bound to method.
func loadMethodPlan(db *store.Store, goal domain.GoalID, method domain.MethodID) (store.PlanState, error) {
	ctx := context.Background()
	state, err := db.LoadGoal(ctx, goal)
	if err != nil {
		return store.PlanState{}, err
	}
	methods, err := db.LoadGoalMethods(ctx, goal, state.Goal.Epoch)
	if err != nil {
		return store.PlanState{}, err
	}
	for _, m := range methods {
		if matchesMethod(m.Method, method) {
			return db.LoadPlan(ctx, m.Plan)
		}
	}
	return store.PlanState{}, store.ErrNotFound
}
