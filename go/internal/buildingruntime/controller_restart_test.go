package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

// The scripted building boundary's vocabulary. A scenario names every
// outcome it wants at the native boundary; anything it leaves unscripted
// fails the test rather than defaulting to success or silence.
const (
	// placeAppliedLostReply places the blueprint, then loses the reply in
	// transport: the controller sees bridge.ErrTransport and records an
	// unknown receipt.
	placeAppliedLostReply = "applied-lost-reply"
	// placeApplied places the blueprint, or finds the one an earlier
	// attempt placed, and answers applied.
	placeApplied = "applied"
	// placeRefused refuses the intent; nothing is placed.
	placeRefused = "refused"
)

// scriptedBuildingNative is the native building boundary under test
// control: the game as the controller's intent writer sees it, with a
// scripted outcome per Apply. It survives the controller's restarts the way
// the game does. Previews and reviewer facts come from the sleeping fixture
// it embeds; ticks and the native generation are the test's.
type scriptedBuildingNative struct {
	*sleepingNative
	t          *testing.T
	generation func() uint64
	mu         sync.Mutex
	tick       int64
	place      string
	// applied counts blueprints placed per action: the externally visible
	// effect a resent intent must not duplicate. A resend finds the
	// blueprint already on the cell and answers applied.
	applied map[domain.ActionID]int
	places  int
}

func newScriptedBuildingNative(t *testing.T, facts *routineNative, generation func() uint64) *scriptedBuildingNative {
	return &scriptedBuildingNative{sleepingNative: &sleepingNative{routineNative: facts}, t: t, generation: generation, tick: facts.reply.GetObserved().Context.GetTick(), applied: map[domain.ActionID]int{}}
}

func (n *scriptedBuildingNative) context(identity *c.Identity) *c.ObservationContext {
	return &c.ObservationContext{Identity: proto.Clone(identity).(*c.Identity), Tick: proto.Int64(n.tick), NativeGeneration: proto.Uint64(n.generation())}
}
func (n *scriptedBuildingNative) placeCount() int {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.places
}
func (n *scriptedBuildingNative) appliedCount(action domain.ActionID) int {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.applied[action]
}

func (n *scriptedBuildingNative) ReadMapBounds(_ context.Context, identity *c.Identity, _ domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	return bridge.MapBounds{Context: n.context(identity), Bounds: policy.Bounds{Width: 100, Height: 100}}, bridge.Result{}, nil
}
func (n *scriptedBuildingNative) ReadEmergency(_ context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	return bridge.EmergencyObservation{Context: n.context(identity), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true)}}, bridge.Result{}, nil
}

// PreviewBuilding is the sleeping fixture's preview at the native tick the
// dispatch runs under rather than the tick the planner admitted at.
func (n *scriptedBuildingNative) PreviewBuilding(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	v, result, err := n.sleepingNative.previewOne(ctx, action, s)
	v.Preview.Tick, v.Stock.Tick = domain.Tick(n.tick), domain.Tick(n.tick)
	return v, result, err
}

func (n *scriptedBuildingNative) Apply(_ context.Context, identity *c.Identity, actions []*o.Action) (*o.ApplyReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.places++
	reply := &o.ApplyReply{}
	for _, wire := range actions {
		key := wire.GetKey()
		action := domain.ActionID(key[:strings.LastIndex(key, "/")])
		switch n.place {
		case placeRefused:
			reply.Results = append(reply.Results, &o.ActionResult{Key: wire.Key, Outcome: &o.ActionResult_Refused{Refused: &o.Refusal{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum(), Reason: proto.String("cell blocked")}}})
		case placeApplied, placeAppliedLostReply:
			if n.applied[action] == 0 {
				n.applied[action]++
			}
			applied := &r.Receipt{AdmittedContext: n.context(identity), Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{}}}}
			reply.Results = append(reply.Results, &o.ActionResult{Key: wire.Key, Outcome: &o.ActionResult_Applied{Applied: applied}})
		default:
			n.t.Errorf("unscripted placement of %s", action)
			return nil, bridge.Result{}, errors.New("unscripted placement")
		}
	}
	if n.place == placeAppliedLostReply {
		return nil, bridge.Result{}, fmt.Errorf("%w: reply lost after the write", bridge.ErrTransport)
	}
	return reply, bridge.Result{}, nil
}

// LookupBuildingAttempt and ObserveBuildingProgress belong to the
// dispatch-and-observe flow building left (#856); nothing calls them.
func (n *scriptedBuildingNative) LookupBuildingAttempt(context.Context, *c.Identity, *c.AttemptKey, uint64, *p.PlacementCandidate) (*r.LookupReply, bridge.Result, error) {
	n.t.Error("building attempt looked up")
	return nil, bridge.Result{}, errors.New("unexpected lookup")
}
func (n *scriptedBuildingNative) ObserveBuildingProgress(context.Context, *r.Receipt, *p.PlacementCandidate) (*r.ProgressReply, bridge.Result, error) {
	n.t.Error("building progress observed")
	return nil, bridge.Result{}, errors.New("unexpected observation")
}

// controllerRig is one process lifetime of the routine controller over a
// durable journal: the real Session (executor and placement boundary),
// Player, RoutineReviewer, sleeping planner and Worker, wired the way
// autonomous play wires them, over a scripted native boundary. Restarting
// closes the rig and opens another on the same database path against the
// same native, which keeps its ledger the way the game keeps running.
type controllerRig struct {
	db       *store.Store
	session  *Session
	player   *Player
	reviewer *RoutineReviewer
	planner  *RoutineBuildingPlanner
	worker   *Worker
	native   *scriptedBuildingNative
	worlds   *playerWorldSource
	world    store.World
}

func openControllerRig(t *testing.T, path, dir string, authority *controlNative, native *scriptedBuildingNative, world store.World) *controllerRig {
	t.Helper()
	ctx := context.Background()
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	// Hang guards only, at the widest bound the constructors accept.
	config := SessionConfig{RoutineMethods: true, Control: ControlConfig{ProfileDirectory: dir, CallTimeout: time.Minute}, Executor: executor.Limits{MaxAge: time.Minute, RunTimeout: time.Minute, JournalTimeout: time.Minute}}
	session, err := NewSession(ctx, config, db, native, authority, native, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	worlds := &playerWorldSource{world: world}
	player, err := NewPlayer(ctx, PlayerConfig{CallTimeout: time.Minute, JournalTimeout: time.Minute}, db, session, worlds)
	if err != nil {
		t.Fatal(err)
	}
	reviewer, err := NewRoutineReviewer(player, native.routineNative, testkit.NewManualClock(time.Now()), policy.DefaultRoutinePolicy(), time.Minute)
	if err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineSleepingPlanner(reviewer, native.sleepingNative)
	if err != nil {
		t.Fatal(err)
	}
	worker := &Worker{player: player, session: session, config: WorkerConfig{RoutineMethods: true, StepInterval: time.Millisecond, MaxBackoff: time.Second, StepTimeout: time.Minute}, waits: make(map[domain.ActionID]workerWait)}
	rig := &controllerRig{db: db, session: session, player: player, reviewer: reviewer, planner: planner, worker: worker, native: native, worlds: worlds, world: world}
	t.Cleanup(func() { rig.close(t) })
	return rig
}

// close ends the process: the player releases the profile and the journal
// closes. Nothing in memory survives it.
func (rig *controllerRig) close(t *testing.T) {
	t.Helper()
	if rig.db == nil {
		return
	}
	ctx := context.Background()
	if err := rig.player.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if err := rig.session.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if err := rig.db.Close(); err != nil {
		t.Fatal(err)
	}
	rig.db = nil
}

// resume is the dashboard's Resume for the rig's world followed by the
// review that binds the routine goals under the granted authority. The
// reviewer's facts report the granted generation, as the game would.
func (rig *controllerRig) resume(t *testing.T, requestID string) store.RoutineReviewResult {
	t.Helper()
	ctx := context.Background()
	if _, err := rig.player.Resume(ctx, store.ControlRequest{RequestID: requestID, Kind: store.ResumeControl, World: rig.world}); err != nil {
		t.Fatal(err)
	}
	if !rig.session.State().Enabled {
		t.Fatal("resume left the session disabled")
	}
	generation := uint64(rig.session.State().Snapshot.Native)
	stampContexts(rig.native.reply, func(v *c.ObservationContext) { v.NativeGeneration = proto.Uint64(generation) })
	return rig.review(t)
}

// stampContexts applies fn to every ObservationContext nested anywhere in
// m: the colony facts carry one per section, and a reader refuses a
// section whose context differs from its reply's, so the game's identity
// and generation change everywhere at once or nowhere.
func stampContexts(m proto.Message, fn func(*c.ObservationContext)) {
	if v, ok := m.(*c.ObservationContext); ok {
		fn(v)
		return
	}
	m.ProtoReflect().Range(func(fd protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		switch {
		case fd.IsList() && fd.Kind() == protoreflect.MessageKind:
			list := value.List()
			for i := 0; i < list.Len(); i++ {
				stampContexts(list.Get(i).Message().Interface(), fn)
			}
		case fd.IsMap():
		case fd.Kind() == protoreflect.MessageKind:
			stampContexts(value.Message().Interface(), fn)
		}
		return true
	})
}
func (rig *controllerRig) review(t *testing.T) store.RoutineReviewResult {
	t.Helper()
	result, err := rig.reviewer.Step(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return result
}

// step runs one worker step at a later wall-clock instant, past every
// backoff the previous step set; the error is the step's own report, which
// a lost reply legitimately carries.
func (rig *controllerRig) step(t *testing.T, at time.Duration) error {
	t.Helper()
	return rig.worker.step(context.Background(), time.Now().Add(at))
}
func (rig *controllerRig) plan(t *testing.T, id domain.PlanID) store.PlanState {
	t.Helper()
	plan, err := rig.db.LoadPlan(context.Background(), id)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}
func planStages(plan store.PlanState) map[domain.ActionID]domain.ProgressView {
	out := map[domain.ActionID]domain.ProgressView{}
	for _, progress := range plan.Progress {
		out[progress.View().Action] = progress.View()
	}
	return out
}

// admitSleepingSpots takes the rig from a fresh journal to the sleeping
// planner's admitted method: two SleepingSpot placements, pending and
// undispatched, under the routine review the resume enabled.
func admitSleepingSpots(t *testing.T, rig *controllerRig) store.PlanState {
	t.Helper()
	rig.resume(t, "resume-1")
	result, err := rig.planner.Step(context.Background())
	if err != nil || result.Reason != BuildingMethodAdmitted || !result.Decision.Admitted || len(result.Decision.Goal.Methods) != 1 {
		t.Fatalf("sleeping admission: %+v %v", result, err)
	}
	plan := rig.plan(t, result.Decision.Goal.Methods[0].Plan)
	if len(plan.Progress) != 2 {
		t.Fatal("expected two sleeping spots", plan)
	}
	for _, v := range planStages(plan) {
		if v.Stage != domain.Pending || v.Attempt != 0 {
			t.Fatal("planner dispatched", v)
		}
	}
	return plan
}

// dispatchLostReplies drives the worker until both placements are
// dispatched with their replies lost: native placed each blueprint once and
// the journal holds each attempt with an unknown receipt.
func dispatchLostReplies(t *testing.T, rig *controllerRig, plan store.PlanState) {
	t.Helper()
	rig.native.place = placeAppliedLostReply
	// A transport loss ends the step after the candidate that hit it, so
	// the second spot dispatches in the next step; bound the loop by the
	// effects the plan owes rather than by wall clock.
	for i := 0; i < 4; i++ {
		_ = rig.step(t, time.Duration(i+1)*2*time.Second)
		if rig.native.appliedCount(plan.Spec.Actions()[0].ID()) == 1 && rig.native.appliedCount(plan.Spec.Actions()[1].ID()) == 1 {
			break
		}
	}
	rig.native.place = ""
	for id, v := range planStages(rig.plan(t, plan.Spec.ID())) {
		receipt, known := v.Receipt.Value()
		if v.Stage == domain.Completed || v.Attempt < 1 || !known || receipt != domain.ReceiptUnknown || rig.native.appliedCount(id) != 1 {
			t.Fatalf("lost reply not held unknown: %+v applied=%d", v, rig.native.appliedCount(id))
		}
	}
}

// assertNoDuplicateEffect is the safety property of every restart
// scenario: whatever the controller did, native placed each spot once.
func assertNoDuplicateEffect(t *testing.T, rig *controllerRig, plan store.PlanState) {
	t.Helper()
	for _, action := range plan.Spec.Actions() {
		if got := rig.native.appliedCount(action.ID()); got != 1 {
			t.Fatalf("%s applied %d times", action.ID(), got)
		}
	}
}

// TestControllerRestartResendsLostIntentWithoutDuplicateEffect is the
// vertical controller scenario (#616, #856): the sleeping planner persists
// intent through the production admission path, the worker dispatches it
// through the real executor and intent writer, native places both
// blueprints and loses both replies, and the controller is restarted
// against its database. The restarted controller resends each intent under
// a fresh attempt; native finds the blueprint already on the cell and
// answers applied, so each spot completes and is placed exactly once.
func TestControllerRestartResendsLostIntentWithoutDuplicateEffect(t *testing.T) {
	t.Parallel()
	path, dir := storetest.Path(t), t.TempDir()
	authority := &controlNative{generation: 1}
	generation := func() uint64 { authority.mu.Lock(); defer authority.mu.Unlock(); return authority.generation }
	facts := colonyCoreNative(t)
	sleepingFacts(facts)
	native := newScriptedBuildingNative(t, facts, generation)
	world := store.World{Colony: "colony", Load: "load", Map: 0}

	first := openControllerRig(t, path, dir, authority, native, world)
	plan := admitSleepingSpots(t, first)
	dispatchLostReplies(t, first, plan)
	first.close(t)

	native.tick++
	restarted := openControllerRig(t, path, dir, authority, native, world)
	if restarted.session.State().Enabled {
		t.Fatal("restart restored authority without a resume")
	}
	restarted.resume(t, "resume-2")
	native.place = placeApplied
	for i := 1; i <= 6; i++ {
		_ = restarted.step(t, time.Duration(i)*10*time.Second)
		done := true
		for _, v := range planStages(restarted.plan(t, plan.Spec.ID())) {
			done = done && v.Stage == domain.Completed
		}
		if done {
			break
		}
	}
	for _, v := range planStages(restarted.plan(t, plan.Spec.ID())) {
		if v.Stage != domain.Completed || v.Attempt < 2 {
			t.Fatal("lost intent not resent to completion", v)
		}
	}
	assertNoDuplicateEffect(t, restarted, plan)
}

// TestControllerRefusalIsTerminal: an intent native refuses places nothing;
// the attempt closes as refused and the same activation does not retry it.
func TestControllerRefusalIsTerminal(t *testing.T) {
	t.Parallel()
	path, dir := storetest.Path(t), t.TempDir()
	authority := &controlNative{generation: 1}
	generation := func() uint64 { authority.mu.Lock(); defer authority.mu.Unlock(); return authority.generation }
	facts := colonyCoreNative(t)
	sleepingFacts(facts)
	native := newScriptedBuildingNative(t, facts, generation)
	rig := openControllerRig(t, path, dir, authority, native, store.World{Colony: "colony", Load: "load", Map: 0})
	plan := admitSleepingSpots(t, rig)
	native.place = placeRefused
	for i := 1; i <= 3; i++ {
		if err := rig.step(t, time.Duration(i)*2*time.Second); err != nil {
			t.Fatal(err)
		}
	}
	// Both spots go out in one batched Apply, and a refusal is not retried.
	if places := native.placeCount(); places != 1 {
		t.Fatal("refusal retried", places)
	}
	for id, v := range planStages(rig.plan(t, plan.Spec.ID())) {
		receipt, known := v.Receipt.Value()
		if v.Stage != domain.Unsuccessful || v.Unresolved || !known || receipt != domain.ReceiptRefused || native.appliedCount(id) != 0 {
			t.Fatalf("refusal misrecorded: %+v applied=%d", v, native.appliedCount(id))
		}
	}
}

func (n *scriptedBuildingNative) ReadRoutineFrame(ctx context.Context, id *c.Identity, definitions []string) (bridge.RoutineFrame, error) {
	return fakeFrame(ctx, n, id, definitions)
}
