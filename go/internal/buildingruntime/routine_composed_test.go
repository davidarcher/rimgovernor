package buildingruntime

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

// composedFamilyPlanners are the three routine planner families this file
// composes in one process: AllowStartingSupplies (supply), MaintainWood
// (acquisition) and EnsureWorkAssignments (work) all attach to the same
// RoutineReviewer/Player/journal, the way autonomous play
// runs every implemented family together instead of one at a time.
type composedFamilyPlanners struct {
	supply      *RoutineSupplyPlanner
	acquisition *RoutineAcquisitionPlanner
	work        *RoutineWorkPlanner
}

// composedRoutineFacts seeds the shared native fixture with the deficits each
// of the three families needs to have a method to admit: an allow-supplies
// target, a wood shortfall with harvestable trees, and a colonist whose work
// priorities no longer match policy.
func composedRoutineFacts(t *testing.T, n *routineNative) {
	t.Helper()
	v := n.reply.GetObserved()
	v.ForbiddenSupplies = []*c.Cell{{X: proto.Int32(1), Z: proto.Int32(2)}}
	v.PendingWoodUnits = proto.Float64(0)
	v.ColonistCount = proto.Uint32(1)
	v.WorkerCount = proto.Uint32(1)
	missing := func(field string) *o.ReadIssue {
		return &o.ReadIssue{Field: proto.String(field), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_APPLICABLE.Enum()}}
	}
	v.Issues = append(v.Issues, missing("naming"))
	row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("patient"), MapId: proto.Int32(v.Context.Identity.GetMapId())}, Colonist: proto.Bool(true), Dead: proto.Bool(false), Downed: proto.Bool(false), Drafted: proto.Bool(false), Equipment: &o.PawnEquipment{Armed: proto.Bool(false)}, Biography: &o.PawnBiography{}, Settings: &o.PawnSettings{WorkApplies: proto.Bool(true), ManualWorkPriorities: proto.Bool(true)}, Issues: []*o.ReadIssue{missing("pawn.snapshot"), missing("mental_state")}}
	for _, skill := range []string{"Construction", "Plants", "Cooking", "Medicine", "Shooting"} {
		row.Biography.Skills = append(row.Biography.Skills, &o.Skill{Definition: &o.DefinitionRef{DefName: proto.String(skill)}, Level: proto.Int32(10), Disabled: proto.Bool(false), Passion: proto.String("None")})
	}
	for _, work := range []string{"Construction", "Growing", "Cooking", "Doctor", "PlantCutting", "Firefighter"} {
		row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String(work), Priority: proto.Int32(1), Disabled: proto.Bool(false)})
	}
	row.Settings.Work = append(row.Settings.Work, &o.WorkSetting{DefName: proto.String("Hunting"), Priority: proto.Int32(0), Disabled: proto.Bool(false)})
	row.Settings.Snapshot = &o.SnapshotRef{Context: proto.Clone(v.Context).(*c.ObservationContext), EntityId: proto.String("patient"), Token: proto.String("before-work")}
	n.pawnReply = &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: &o.PawnSnapshot{Context: proto.Clone(v.Context).(*c.ObservationContext), Pawns: []*o.PawnState{row}, Completeness: &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(1), Returned: proto.Uint64(1), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}}}}
	for i := 0; i < 12; i++ {
		id := fmt.Sprint("plant", i)
		v.Acquisition = append(v.Acquisition, &o.AcquisitionFacts{Source: &o.EntityRef{Id: proto.String(id), DefName: proto.String("Oak"), MapId: v.Context.Identity.MapId, Position: proto.Clone(v.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String(id), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)})
	}
}

// composedRoutineFixture cancels the fixture's default building submission
// (so it cannot count as acquisition-blocking work), wires the reviewer with
// the shared work/emergency native, seeds one routine review that covers all
// three families, and returns their planners attached to it.
func composedRoutineFixture(t *testing.T) (*RoutineReviewer, *store.Store, *playerFakeSession, store.ControlRequest, *routineNative, composedFamilyPlanners) {
	t.Helper()
	ctx := context.Background()
	reviewer, db, session, request, native := routineFixture(t)
	rootPlan, err := db.LoadPlan(ctx, session.State().Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range rootPlan.Spec.Actions() {
		if _, err = db.Cancel(ctx, rootPlan.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	composedRoutineFacts(t, native)
	reviewer.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: native}}
	reviewer.methods = domain.Known([]policy.GoalID{policy.MaintainWood})
	snapshot := reviewer.player.State().Snapshot
	if _, err = reviewer.player.SetWorkPreferences(ctx, store.WorkPreferenceRequest{RequestID: "composed-disable-builder", Plan: snapshot.Plan, World: playerWorld(snapshot), ExpectedRevision: 0, Overrides: []policy.WorkOverride{{Pawn: "patient", Work: "Construction", Priority: 0}}}); err != nil {
		t.Fatal(err)
	}
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source := &routineSupplyNative{context: proto.Clone(native.reply.GetObserved().Context).(*c.ObservationContext)}
	supply, err := NewRoutineSupplyPlanner(reviewer, source)
	if err != nil {
		t.Fatal(err)
	}
	acquisition, err := NewRoutineAcquisitionPlanner(reviewer, policy.MaintainWood)
	if err != nil {
		t.Fatal(err)
	}
	work, err := NewRoutineWorkPlanner(reviewer)
	if err != nil {
		t.Fatal(err)
	}
	return reviewer, db, session, request, native, composedFamilyPlanners{supply: supply, acquisition: acquisition, work: work}
}

// TestComposedRoutineFamiliesManualCancelsWithoutCrossLeak exercises the
// G01.10 composed default across three simultaneously-active families
// (supply, wood acquisition, work assignment) sharing one reviewer/session in
// a single process: each admits its own held plan independently, and a
// single player Manual direction change cancels every family's held plan
// without one family's cancellation touching another's plan or actions.
//
// Known flaky under full-module `go test ./...` parallel load (passes
// reliably in isolation and even at the whole-package level); see issue #53.
func TestComposedRoutineFamiliesManualCancelsWithoutCrossLeak(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	reviewer, db, _, request, native, planners := composedRoutineFixture(t)

	supplyResult, err := planners.supply.Step(ctx)
	if err != nil || supplyResult.Reason != BuildingMethodAdmitted {
		t.Fatalf("supply admission: %+v %v", supplyResult, err)
	}
	acquisitionResult, err := planners.acquisition.Step(ctx)
	if err != nil || acquisitionResult.Reason != BuildingMethodAdmitted {
		t.Fatalf("acquisition admission: %+v %v", acquisitionResult, err)
	}
	workResult, err := planners.work.Step(ctx)
	if err != nil || workResult.Reason != BuildingMethodAdmitted {
		t.Fatalf("work admission: %+v %v", workResult, err)
	}
	if supplyResult.Plan == acquisitionResult.Plan || acquisitionResult.Plan == workResult.Plan || supplyResult.Plan == workResult.Plan {
		t.Fatal("families collapsed onto the same plan id")
	}

	// Each family recognizes its own held plan and neither admits a
	// duplicate nor is confused by the other two families' plans.
	if next, err := planners.supply.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatalf("supply hold: %+v %v", next, err)
	}
	if next, err := planners.acquisition.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatalf("acquisition hold: %+v %v", next, err)
	}
	if next, err := planners.work.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatalf("work hold: %+v %v", next, err)
	}
	if native.reads == 0 {
		t.Fatal("composed fixture never read colony facts")
	}

	// A single player Manual direction change should cancel every family's
	// in-flight hold, in the same way each family's individual test proves
	// for itself in isolation.
	request.Kind, request.RequestID, request.Plan, request.Revision = store.ManualControl, "manual-composed", "", 0
	if _, err := reviewer.player.Manual(ctx, request); err != nil {
		t.Fatal(err)
	}

	if next, err := planners.supply.Step(ctx); err != nil || next.Reason != BuildingMethodDisabled {
		t.Fatalf("supply after manual: %+v %v", next, err)
	}
	if next, err := planners.acquisition.Step(ctx); err != nil || next.Reason != BuildingMethodDisabled {
		t.Fatalf("acquisition after manual: %+v %v", next, err)
	}
	if next, err := planners.work.Step(ctx); err != nil || next.Reason != BuildingMethodDisabled {
		t.Fatalf("work after manual: %+v %v", next, err)
	}

	for name, id := range map[string]domain.PlanID{"supply": supplyResult.Plan, "acquisition": acquisitionResult.Plan, "work": workResult.Plan} {
		plan, err := db.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(name, err)
		}
		if len(plan.Progress) == 0 {
			t.Fatal(name, "no progress recorded")
		}
		for _, p := range plan.Progress {
			if p.View().Stage != domain.Cancelled {
				t.Fatal(name, "not cancelled by shared manual", p)
			}
		}
	}
}

// TestComposedRoutineFamiliesWorldChangeRejectsAllWithoutCrossLeak simulates
// a native world/tick-rewind change (the session's authoritative snapshot
// moving out from under an already-committed routine review, e.g. a save
// reload) landing while three families each have a held plan. Every
// family must independently detect the stale review and refuse to progress
// without further native reads, and none of their already-durable holds may
// be corrupted or leak into one another while the world is unreconciled.
func TestComposedRoutineFamiliesWorldChangeRejectsAllWithoutCrossLeak(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	_, db, session, _, native, planners := composedRoutineFixture(t)

	supplyResult, err := planners.supply.Step(ctx)
	if err != nil || supplyResult.Reason != BuildingMethodAdmitted {
		t.Fatalf("supply admission: %+v %v", supplyResult, err)
	}
	acquisitionResult, err := planners.acquisition.Step(ctx)
	if err != nil || acquisitionResult.Reason != BuildingMethodAdmitted {
		t.Fatalf("acquisition admission: %+v %v", acquisitionResult, err)
	}
	workResult, err := planners.work.Step(ctx)
	if err != nil || workResult.Reason != BuildingMethodAdmitted {
		t.Fatalf("work admission: %+v %v", workResult, err)
	}

	readsBefore := native.reads
	before := map[string]domain.PlanID{"supply": supplyResult.Plan, "acquisition": acquisitionResult.Plan, "work": workResult.Plan}
	snapshotsBefore := map[string][]domain.Progress{}
	for name, id := range before {
		plan, err := db.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(name, err)
		}
		snapshotsBefore[name] = plan.Progress
	}

	// A world replacement or tick rewind changes the session's native
	// generation out from under the last committed routine review, without
	// going through the player Manual path.
	session.mu.Lock()
	session.state.Snapshot.Native++
	session.mu.Unlock()

	if next, err := planners.supply.Step(ctx); err != nil || next.Reason != BuildingMethodNoReview {
		t.Fatalf("supply after world change: %+v %v", next, err)
	}
	if next, err := planners.acquisition.Step(ctx); err != nil || next.Reason != BuildingMethodNoReview {
		t.Fatalf("acquisition after world change: %+v %v", next, err)
	}
	if next, err := planners.work.Step(ctx); err != nil || next.Reason != BuildingMethodNoReview {
		t.Fatalf("work after world change: %+v %v", next, err)
	}
	if native.reads != readsBefore {
		t.Fatal("stale-world planners performed native reads before refusing")
	}

	// Each family's durable hold survives the refusal untouched and
	// independently of the other two families' holds.
	for name, id := range before {
		plan, err := db.LoadPlan(ctx, id)
		if err != nil {
			t.Fatal(name, err)
		}
		if len(plan.Progress) != len(snapshotsBefore[name]) {
			t.Fatal(name, "progress count changed across world change", plan.Progress, snapshotsBefore[name])
		}
		for i, p := range plan.Progress {
			if p.View().Stage != snapshotsBefore[name][i].View().Stage {
				t.Fatal(name, "stale-world refusal corrupted a held plan", p)
			}
		}
	}
}

// composedAcquire submits a distinct building and acquires control under a
// unique request id, mirroring playerAcquire but safe to call twice against
// the same durable store across a simulated process restart.
func composedAcquire(t *testing.T, p *Player, requestID string, x, z int32) store.ControlRequest {
	t.Helper()
	building, err := domain.NewBuilding("Wall", domain.Cell{X: x, Z: z}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	submission, created, err := p.Submit(context.Background(), store.SubmissionRequest{RequestID: requestID, World: store.World{Colony: "colony", Load: "load", Map: 0}, Building: building})
	if err != nil || !created {
		t.Fatal(submission, created, err)
	}
	return store.ControlRequest{RequestID: requestID + "-acquire", Kind: store.AcquireControl, World: submission.Request.World, Plan: submission.Plan, Revision: submission.Revision}
}

func composedNativeFixture(t *testing.T) *routineNative {
	t.Helper()
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	n := &routineNative{reply: &o.ColonyFactsReply{}}
	if err = protojson.Unmarshal(data, n.reply); err != nil {
		t.Fatal(err)
	}
	return n
}

// TestComposedRoutineFamiliesFreshStartReconciliationRecoversIndependently
// seeds durable supply and acquisition holds as if from a prior composed
// run, then starts a fresh Player/RoutineReviewer/planner set (a new
// process) against that same durable store and confirms each family
// reconciles its own already-held plan correctly, without either family's
// recovery interfering with the other's.
func TestComposedRoutineFamiliesFreshStartReconciliationRecoversIndependently(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	path := filepath.Join(dir, "composed-restart.sqlite")

	// --- prior run: admit a supply hold and an acquisition hold ---
	db1, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	session1 := &playerFakeSession{}
	worlds1 := &playerWorldSource{world: store.World{Colony: "colony", Load: "load", Map: 0}}
	p1, err := newPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db1, session1, worlds1)
	if err != nil {
		t.Fatal(err)
	}
	request1 := composedAcquire(t, p1, "composed-restart-first", 1, 1)
	if _, err = p1.Acquire(ctx, request1); err != nil {
		t.Fatal(err)
	}
	rootPlan1, err := db1.LoadPlan(ctx, session1.State().Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range rootPlan1.Spec.Actions() {
		if _, err = db1.Cancel(ctx, rootPlan1.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	n1 := composedNativeFixture(t)
	composedRoutineFacts(t, n1)
	r1, err := NewRoutineReviewer(p1, n1, testkit.NewManualClock(time.Now()), policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	r1.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: n1}}
	r1.methods = domain.Known([]policy.GoalID{policy.MaintainWood})
	if _, err = r1.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source1 := &routineSupplyNative{context: proto.Clone(n1.reply.GetObserved().Context).(*c.ObservationContext)}
	supplyPlanner1, err := NewRoutineSupplyPlanner(r1, source1)
	if err != nil {
		t.Fatal(err)
	}
	acquisitionPlanner1, err := NewRoutineAcquisitionPlanner(r1, policy.MaintainWood)
	if err != nil {
		t.Fatal(err)
	}
	supplyResult1, err := supplyPlanner1.Step(ctx)
	if err != nil || supplyResult1.Reason != BuildingMethodAdmitted {
		t.Fatalf("prior-run supply admission: %+v %v", supplyResult1, err)
	}
	acquisitionResult1, err := acquisitionPlanner1.Step(ctx)
	if err != nil || acquisitionResult1.Reason != BuildingMethodAdmitted {
		t.Fatalf("prior-run acquisition admission: %+v %v", acquisitionResult1, err)
	}
	if supplyResult1.Plan == acquisitionResult1.Plan {
		t.Fatal("supply and acquisition collapsed onto the same plan id")
	}
	if err = p1.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if err = db1.Close(); err != nil {
		t.Fatal(err)
	}

	// --- fresh composed process against the same durable store ---
	db2, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { db2.Close() }()
	session2 := &playerFakeSession{}
	worlds2 := &playerWorldSource{world: store.World{Colony: "colony", Load: "load", Map: 0}}
	p2, err := newPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db2, session2, worlds2)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { p2.Close(ctx) }()
	// The prior process's grant is still durably "Granted" (its own local
	// session died with the process, but nothing released the record). A
	// fresh process cannot replay that exact acquire request to resume it
	// (Player.Acquire short-circuits an exact replay as an idempotent no-op
	// against a brand new, never-enabled local session), so it first issues
	// its own Manual to clear the orphaned grant before acquiring fresh
	// authority, the way a real restarted controller reconciles a stale world.
	_, err = p2.Manual(ctx, store.ControlRequest{RequestID: "composed-restart-release", Kind: store.ManualControl, World: request1.World})
	if err != nil {
		t.Fatal(err)
	}
	request2 := composedAcquire(t, p2, "composed-restart-second", 2, 2)
	if _, err = p2.Acquire(ctx, request2); err != nil {
		t.Fatal(err)
	}
	rootPlan2, err := db2.LoadPlan(ctx, session2.State().Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	for _, action := range rootPlan2.Spec.Actions() {
		if _, err = db2.Cancel(ctx, rootPlan2.Spec.ID(), action.ID()); err != nil {
			t.Fatal(err)
		}
	}
	n2 := composedNativeFixture(t)
	composedRoutineFacts(t, n2)
	r2, err := NewRoutineReviewer(p2, n2, testkit.NewManualClock(time.Now()), policy.DefaultRoutinePolicy(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	r2.native = &healthyWorkNative{routineMedicalNative: &routineMedicalNative{routineNative: n2}}
	r2.methods = domain.Known([]policy.GoalID{policy.MaintainWood})
	if _, err = r2.Step(ctx); err != nil {
		t.Fatal(err)
	}
	source2 := &routineSupplyNative{context: proto.Clone(n2.reply.GetObserved().Context).(*c.ObservationContext)}
	supplyPlanner2, err := NewRoutineSupplyPlanner(r2, source2)
	if err != nil {
		t.Fatal(err)
	}
	acquisitionPlanner2, err := NewRoutineAcquisitionPlanner(r2, policy.MaintainWood)
	if err != nil {
		t.Fatal(err)
	}

	// A restart reconnects with a new player direction (a fresh Acquire
	// chain), which correctly invalidates the prior run's goal bindings and
	// cancels their now-ownerless held plans rather than leaving them
	// dangling (see reviewRoutineTx's "changed" handling in
	// internal/store/routine.go) — the same rule an in-process Manual or
	// world change already enforces. Each family must independently
	// re-derive that its need is still outstanding and admit its own fresh
	// method, without either family's recovery interfering with the other's.
	supplyResult2, err := supplyPlanner2.Step(ctx)
	if err != nil || supplyResult2.Reason != BuildingMethodAdmitted {
		t.Fatalf("restart supply reconciliation: %+v %v", supplyResult2, err)
	}
	acquisitionResult2, err := acquisitionPlanner2.Step(ctx)
	if err != nil || acquisitionResult2.Reason != BuildingMethodAdmitted {
		t.Fatalf("restart acquisition reconciliation: %+v %v", acquisitionResult2, err)
	}
	if supplyResult2.Plan == acquisitionResult2.Plan {
		t.Fatal("restarted families collapsed onto the same re-admitted plan id")
	}

	// The prior run's holds were cancelled cleanly, not left dangling, and
	// each family's own prior hold was cancelled independently of the
	// other's.
	priorSupplyPlan, err := db2.LoadPlan(ctx, supplyResult1.Plan)
	if err != nil || len(priorSupplyPlan.Progress) == 0 {
		t.Fatal("prior supply hold lost across restart", err)
	}
	priorAcquisitionPlan, err := db2.LoadPlan(ctx, acquisitionResult1.Plan)
	if err != nil || len(priorAcquisitionPlan.Progress) == 0 {
		t.Fatal("prior acquisition hold lost across restart", err)
	}
	for _, p := range priorSupplyPlan.Progress {
		if p.View().Stage != domain.Cancelled {
			t.Fatal("restart left the orphaned prior-run supply hold open", p)
		}
	}
	for _, p := range priorAcquisitionPlan.Progress {
		if p.View().Stage != domain.Cancelled {
			t.Fatal("restart left the orphaned prior-run acquisition hold open", p)
		}
	}

	// The freshly re-admitted plans are held, not dispatched, and each
	// family recognizes only its own new hold as existing work.
	newSupplyPlan, err := db2.LoadPlan(ctx, supplyResult2.Plan)
	if err != nil || len(newSupplyPlan.Progress) == 0 {
		t.Fatal("re-admitted supply plan missing", err)
	}
	newAcquisitionPlan, err := db2.LoadPlan(ctx, acquisitionResult2.Plan)
	if err != nil || len(newAcquisitionPlan.Progress) == 0 {
		t.Fatal("re-admitted acquisition plan missing", err)
	}
	for _, p := range newSupplyPlan.Progress {
		if p.View().Stage != domain.Pending {
			t.Fatal("re-admitted supply plan was not left pending", p)
		}
	}
	for _, p := range newAcquisitionPlan.Progress {
		if p.View().Stage != domain.Pending {
			t.Fatal("re-admitted acquisition plan was not left pending", p)
		}
	}
	if next, err := supplyPlanner2.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatalf("post-restart supply hold: %+v %v", next, err)
	}
	if next, err := acquisitionPlanner2.Step(ctx); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatalf("post-restart acquisition hold: %+v %v", next, err)
	}
}
