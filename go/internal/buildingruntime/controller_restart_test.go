package buildingruntime

import (
	"context"
	"errors"
	"fmt"
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
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
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
	// placeAppliedLostReply admits and applies the placement in the native
	// ledger, then loses the reply in transport: the controller sees
	// bridge.ErrTransport and holds an unknown receipt.
	placeAppliedLostReply = "applied-lost-reply"
	// placeRefused refuses before admission; the ledger records nothing.
	placeRefused = "refused"

	// progressUnknown is a lookup that finds the attempt but an inspection
	// that cannot say what became of it (an incomplete read).
	progressUnknown = "unknown"
	// progressInFlight is the ledger's admitted-but-unfinished entry, the
	// form a lookup served before the main thread ran the write takes.
	progressInFlight = "in-flight"
	// progressPending is the placed blueprint, not yet built.
	progressPending = "pending"
	// progressCompleted is the finished building on the ordered cell.
	progressCompleted = "completed"
	// progressCompletedElsewhere is a finished building one cell off: a
	// completion that does not match the order.
	progressCompletedElsewhere = "completed-elsewhere"
	// progressStale is a completion whose context predates the attempt's
	// admission: an observation older than the write it claims to settle.
	progressStale = "stale"
)

// scriptedBuildingNative is the native building boundary under test
// control. It is the game as the controller's placement boundary sees it:
// the ledger of admitted attempts, a scripted outcome per placement and a
// scripted inspection per action. It survives the controller's restarts the
// way the game does. Previews and reviewer facts come from the sleeping
// fixture it embeds; ticks and the native generation are the test's.
type scriptedBuildingNative struct {
	*sleepingNative
	t          *testing.T
	generation func() uint64
	mu         sync.Mutex
	tick       int64
	place      string
	progress   map[domain.ActionID]string
	// admitted is the native ledger: every attempt the game admitted and
	// applied, by attempt key, with the context it was admitted under.
	admitted map[string]*r.Receipt
	// applied counts placements applied per action: the externally
	// visible effect a lost reply must not duplicate.
	applied map[domain.ActionID]int
	// lookedUp counts ledger lookups per attempt key.
	lookedUp                  map[string]int
	places, lookups, observes int
}

func newScriptedBuildingNative(t *testing.T, facts *routineNative, generation func() uint64) *scriptedBuildingNative {
	return &scriptedBuildingNative{sleepingNative: &sleepingNative{routineNative: facts}, t: t, generation: generation, tick: facts.reply.GetObserved().Context.GetTick(), progress: map[domain.ActionID]string{}, admitted: map[string]*r.Receipt{}, applied: map[domain.ActionID]int{}, lookedUp: map[string]int{}}
}

func attemptKeyString(k *c.AttemptKey) string {
	return fmt.Sprintf("%s|%s|%d", k.GetControllerSessionId(), k.GetActionId(), k.GetAttemptId())
}
func (n *scriptedBuildingNative) context(identity *c.Identity) *c.ObservationContext {
	return &c.ObservationContext{Identity: proto.Clone(identity).(*c.Identity), Tick: proto.Int64(n.tick), NativeGeneration: proto.Uint64(n.generation())}
}
func (n *scriptedBuildingNative) script(action domain.ActionID, mode string) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.progress[action] = mode
}
func (n *scriptedBuildingNative) counts() (places, lookups, observes int) {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.places, n.lookups, n.observes
}
func (n *scriptedBuildingNative) appliedCount(action domain.ActionID) int {
	n.mu.Lock()
	defer n.mu.Unlock()
	return n.applied[action]
}

// lookupsOf counts the ledger lookups of every attempt the plan's actions
// ever admitted.
func (n *scriptedBuildingNative) lookupsOf(plan store.PlanState) int {
	n.mu.Lock()
	defer n.mu.Unlock()
	total := 0
	for key, receipt := range n.admitted {
		for _, action := range plan.Spec.Actions() {
			if receipt.GetAttempt().GetActionId() == string(action.ID()) {
				total += n.lookedUp[key]
			}
		}
	}
	return total
}

func (n *scriptedBuildingNative) ReadMapBounds(_ context.Context, identity *c.Identity, _ domain.Cell) (bridge.MapBounds, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	return bridge.MapBounds{Context: n.context(identity), Bounds: policy.Bounds{Width: 100, Height: 100}}, bridge.Result{}, nil
}
func (n *scriptedBuildingNative) ReadEmergency(_ context.Context, identity *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	return bridge.EmergencyObservation{Context: n.context(identity), Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}, bridge.Result{}, nil
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

func (n *scriptedBuildingNative) PlaceBuilding(_ context.Context, pre *a.WritePrecondition, candidate *p.PlacementCandidate) (*o.ExecuteReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.places++
	action := domain.ActionID(pre.GetAttempt().GetActionId())
	switch n.place {
	case placeRefused:
		return nil, bridge.Result{}, &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_INVALID_REQUEST.Enum()}}
	case placeAppliedLostReply:
		key := attemptKeyString(pre.GetAttempt())
		if _, dup := n.admitted[key]; dup {
			n.t.Errorf("attempt %s placed twice", key)
			return nil, bridge.Result{}, &bridge.NativeFailure{Value: &c.Failure{Code: c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT.Enum()}}
		}
		effect := &r.ConstructionEffect{OriginThingId: proto.String("blueprint-" + string(action)), CurrentThingId: proto.String("blueprint-" + string(action)), DefName: candidate.DefName, Stuff: candidate.Stuff, Cell: &c.Cell{X: candidate.X, Z: candidate.Z}, Rotation: candidate.Rotation, Stage: r.ConstructionStage_CONSTRUCTION_STAGE_BLUEPRINT.Enum(), Present: proto.Bool(true), Started: proto.Bool(false), Failed: proto.Bool(false)}
		n.admitted[key] = &r.Receipt{Attempt: proto.Clone(pre.GetAttempt()).(*c.AttemptKey), AdmittedContext: n.context(pre.GetIdentity()), Outcome: &r.Receipt_Applied{Applied: &r.Applied{Observed: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: effect}}}}}
		n.applied[action]++
		return nil, bridge.Result{}, fmt.Errorf("%w: reply lost after the write", bridge.ErrTransport)
	default:
		n.t.Errorf("unscripted placement of %s", action)
		return nil, bridge.Result{}, errors.New("unscripted placement")
	}
}

func (n *scriptedBuildingNative) LookupBuildingAttempt(_ context.Context, identity *c.Identity, attempt *c.AttemptKey, generation uint64, _ *p.PlacementCandidate) (*r.LookupReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.lookups++
	n.lookedUp[attemptKeyString(attempt)]++
	admitted, ok := n.admitted[attemptKeyString(attempt)]
	if !ok || !proto.Equal(identity, admitted.AdmittedContext.GetIdentity()) || generation != admitted.AdmittedContext.GetNativeGeneration() {
		return &r.LookupReply{Outcome: &r.LookupReply_Unknown{Unknown: &r.UnknownAttempt{Context: n.context(identity)}}}, bridge.Result{}, nil
	}
	if n.progress[domain.ActionID(attempt.GetActionId())] == progressInFlight {
		return &r.LookupReply{Outcome: &r.LookupReply_InFlight{InFlight: &r.InFlight{Attempt: proto.Clone(attempt).(*c.AttemptKey), AdmittedContext: proto.Clone(admitted.AdmittedContext).(*c.ObservationContext)}}}, bridge.Result{}, nil
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: admitted}}, bridge.Result{}, nil
}

func (n *scriptedBuildingNative) ObserveBuildingProgress(_ context.Context, admitted *r.Receipt, candidate *p.PlacementCandidate) (*r.ProgressReply, bridge.Result, error) {
	n.mu.Lock()
	defer n.mu.Unlock()
	n.observes++
	action := domain.ActionID(admitted.GetAttempt().GetActionId())
	progress := &r.Progress{Attempt: proto.Clone(admitted.GetAttempt()).(*c.AttemptKey), Context: n.context(admitted.GetAdmittedContext().GetIdentity()), CompleteInspection: proto.Bool(true)}
	origin := boundary.Origin(admitted)
	effect := &r.ConstructionEffect{OriginThingId: proto.String(origin), CurrentThingId: proto.String(origin), DefName: candidate.DefName, Stuff: candidate.Stuff, Cell: &c.Cell{X: candidate.X, Z: candidate.Z}, Rotation: candidate.Rotation, Present: proto.Bool(true), Started: proto.Bool(true), Failed: proto.Bool(false)}
	mode := n.progress[action]
	switch mode {
	case progressUnknown:
		progress.CompleteInspection = proto.Bool(false)
		progress.Effect = &r.Progress_Unknown{Unknown: &r.UnknownEffect{Reason: proto.String("inspection incomplete")}}
	case progressPending:
		effect.Stage = r.ConstructionStage_CONSTRUCTION_STAGE_BLUEPRINT.Enum()
		progress.Effect = &r.Progress_Pending{Pending: &r.PendingEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: effect}}}}
	case progressCompleted, progressCompletedElsewhere, progressStale:
		effect.Stage = r.ConstructionStage_CONSTRUCTION_STAGE_BUILDING.Enum()
		effect.CurrentThingId = proto.String("building-" + string(action))
		if mode == progressCompletedElsewhere {
			effect.Cell.X = proto.Int32(candidate.GetX() + 1)
		}
		if mode == progressStale {
			progress.Context.Tick = proto.Int64(admitted.GetAdmittedContext().GetTick() - 1)
		}
		progress.Effect = &r.Progress_Completed{Completed: &r.CompletedEffect{Evidence: &r.EffectEvidence{Effect: &r.EffectEvidence_Construction{Construction: effect}}}}
	default:
		n.t.Errorf("unscripted inspection of %s", action)
		return nil, bridge.Result{}, errors.New("unscripted inspection")
	}
	return &r.ProgressReply{Outcome: &r.ProgressReply_Progress{Progress: progress}}, bridge.Result{}, nil
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
// dispatched with their replies lost: native applied each once and the
// journal holds each attempt as dispatched and unresolved.
func dispatchLostReplies(t *testing.T, rig *controllerRig, plan store.PlanState) {
	t.Helper()
	rig.native.place = placeAppliedLostReply
	// The game has not moved: an attempt the step after a loss looks up is
	// in the ledger with nothing to inspect yet.
	for _, action := range plan.Spec.Actions() {
		rig.native.script(action.ID(), progressUnknown)
	}
	// A transport loss ends the step after the candidate that hit it, so
	// the second spot dispatches in the next step; bound the loop by the
	// two dispatches the plan owes rather than by wall clock.
	for i := 0; i < 4; i++ {
		_ = rig.step(t, time.Duration(i+1)*2*time.Second)
		if places, _, _ := rig.native.counts(); places == 2 {
			break
		}
	}
	rig.native.place = ""
	places, _, _ := rig.native.counts()
	if places != 2 {
		t.Fatal("expected one placement per spot", places)
	}
	for id, v := range planStages(rig.plan(t, plan.Spec.ID())) {
		receipt, known := v.Receipt.Value()
		if v.Stage != domain.AwaitingObservation || !v.Unresolved || v.Attempt != 1 || !known || receipt != domain.ReceiptUnknown || rig.native.appliedCount(id) != 1 {
			t.Fatalf("lost reply not held uncertain: %+v applied=%d", v, rig.native.appliedCount(id))
		}
	}
}

// assertNoDuplicateEffect is the safety property of every restart
// scenario: whatever the controller did, native applied each spot once.
func assertNoDuplicateEffect(t *testing.T, rig *controllerRig, plan store.PlanState) {
	t.Helper()
	for _, action := range plan.Spec.Actions() {
		if got := rig.native.appliedCount(action.ID()); got != 1 {
			t.Fatalf("%s applied %d times", action.ID(), got)
		}
	}
}

// TestControllerRestartReconcilesLostReplyBeforeAnyRetry is the first
// vertical controller scenario (#616): the sleeping planner persists intent
// through the production admission path, the worker dispatches it through
// the real executor and placement boundary, native applies both placements
// and loses both replies, and the controller is then restarted against its
// database. The restarted controller reconciles through the native ledger
// instead of retrying, and each spot completes only once native reports the
// finished building on the ordered cell: an unknown inspection, an in-flight
// entry and a blueprint all keep it open, and a completion elsewhere or one
// older than the admission is refused as evidence. Native applies each spot
// exactly once throughout.
func TestControllerRestartReconcilesLostReplyBeforeAnyRetry(t *testing.T) {
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
	before := first.review(t)
	first.close(t)

	// The game ran on: the next tick is later than every admission.
	native.tick++
	restarted := openControllerRig(t, path, dir, authority, native, world)
	if restarted.session.State().Enabled {
		t.Fatal("restart restored authority without a resume")
	}
	// Before authority returns the review is disabled and keeps its
	// ranking: waiting ages are not rewritten to the restart's tick.
	disabled := restarted.review(t)
	if disabled.Review.Enabled || len(disabled.Review.Development.Rows) == 0 {
		t.Fatal("disabled review lost its ranking", disabled.Review.Development)
	}
	for _, want := range before.Review.Development.Rows {
		for _, got := range disabled.Review.Development.Rows {
			if got.Goal == want.Goal && !want.Committed && !got.Committed && got.WaitingSince != want.WaitingSince {
				t.Fatalf("%s waiting age rewritten across restart: %d -> %d", want.Goal, want.WaitingSince, got.WaitingSince)
			}
		}
	}
	for _, action := range plan.Spec.Actions() {
		native.script(action.ID(), progressUnknown)
	}
	restarted.resume(t, "resume-2")
	if next, err := restarted.planner.Step(context.Background()); err != nil || next.Reason != BuildingMethodExistingWork {
		t.Fatalf("restarted planner did not recognise its uncertain work: %+v %v", next, err)
	}

	// Unknown: the ledger has the attempt, the inspection cannot say what
	// became of it. The attempt stays open; nothing is placed again.
	_ = restarted.step(t, 10*time.Second)
	for _, v := range planStages(restarted.plan(t, plan.Spec.ID())) {
		if v.Stage != domain.AwaitingObservation || !v.Unresolved || v.Attempt != 1 {
			t.Fatal("unknown inspection resolved the attempt", v)
		}
	}
	places, lookups, _ := native.counts()
	if places != 2 || lookups == 0 {
		t.Fatal("restart retried before consulting the ledger", places, lookups)
	}

	// In flight, then a blueprint: accepted work that has not finished
	// stays dispatched. Stale and misplaced completions are refused.
	spots := plan.Spec.Actions()
	native.script(spots[0].ID(), progressInFlight)
	native.script(spots[1].ID(), progressPending)
	_ = restarted.step(t, 20*time.Second)
	native.tick++
	native.script(spots[0].ID(), progressStale)
	native.script(spots[1].ID(), progressCompletedElsewhere)
	_ = restarted.step(t, 30*time.Second)
	for _, v := range planStages(restarted.plan(t, plan.Spec.ID())) {
		if v.Stage != domain.AwaitingObservation || !v.Unresolved || v.Attempt != 1 {
			t.Fatal("unfinished or mismatched evidence completed the attempt", v)
		}
	}

	// Completion is the observed postcondition, per spot.
	native.script(spots[0].ID(), progressCompleted)
	_ = restarted.step(t, 40*time.Second)
	stages := planStages(restarted.plan(t, plan.Spec.ID()))
	if stages[spots[0].ID()].Stage != domain.Completed || stages[spots[1].ID()].Stage != domain.AwaitingObservation || !stages[spots[1].ID()].Unresolved {
		t.Fatal("completion did not follow the observed building", stages)
	}
	native.script(spots[1].ID(), progressCompleted)
	_ = restarted.step(t, 50*time.Second)
	for _, v := range planStages(restarted.plan(t, plan.Spec.ID())) {
		if v.Stage != domain.Completed || v.Attempt != 1 {
			t.Fatal("second spot did not complete", v)
		}
	}
	assertNoDuplicateEffect(t, restarted, plan)
	if places, _, _ = native.counts(); places != 2 {
		t.Fatal("a completed attempt was placed again", places)
	}
}

// TestControllerRestartIntoAnotherWorldKeepsUncertainWorkUnsettled: the same
// lost-reply attempts restarted under a different load token. The old
// attempts belong to the previous world's identity: the worker never asks
// the ledger about them, the planner starts fresh under the new world's
// goals, and the attempts stay unresolved rather than completing from a
// receipt an incompatible world could not have produced.
func TestControllerRestartIntoAnotherWorldKeepsUncertainWorkUnsettled(t *testing.T) {
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

	// The player loaded another save: a new load token everywhere the game
	// reports its identity, and a ledger that would happily report the old
	// attempts finished if anyone asked.
	reloaded := store.World{Colony: "colony", Load: "load-2", Map: 0}
	stampContexts(facts.reply, func(v *c.ObservationContext) { v.Identity.LoadToken = proto.String(string(reloaded.Load)) })
	native.tick++
	for _, action := range plan.Spec.Actions() {
		native.script(action.ID(), progressCompleted)
	}
	lookupsBefore := native.lookupsOf(plan)
	restarted := openControllerRig(t, path, dir, authority, native, reloaded)
	restarted.resume(t, "resume-2")
	fresh, err := restarted.planner.Step(context.Background())
	if err != nil || fresh.Reason != BuildingMethodAdmitted || fresh.Decision.Goal.Methods[len(fresh.Decision.Goal.Methods)-1].Plan == plan.Spec.ID() {
		t.Fatalf("new world did not start fresh sleeping work: %+v %v", fresh, err)
	}
	// The new world's own spots are not the property here; native refuses
	// them so their attempts close without a ledger entry.
	native.place = placeRefused
	for i := 1; i <= 3; i++ {
		_ = restarted.step(t, time.Duration(i)*10*time.Second)
	}
	for _, v := range planStages(restarted.plan(t, plan.Spec.ID())) {
		if v.Stage == domain.Completed || !v.Unresolved || v.Attempt != 1 {
			t.Fatal("old-world attempt settled under the new load", v)
		}
	}
	if lookups := native.lookupsOf(plan); lookups != lookupsBefore {
		t.Fatal("old-world attempt was looked up under the new load", lookups, lookupsBefore)
	}
	assertNoDuplicateEffect(t, restarted, plan)
}

// TestControllerRefusalBeforeApplicationLeavesNothingToRecover: a placement
// native refuses before admission records nothing in the ledger and does not
// complete or hold anything; the attempt closes as refused without a
// reconciliation lookup, and the same activation does not retry it.
func TestControllerRefusalBeforeApplicationLeavesNothingToRecover(t *testing.T) {
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
	places, lookups, _ := native.counts()
	if places != 2 || lookups != 0 {
		t.Fatal("refusal retried or reconciled", places, lookups)
	}
	for id, v := range planStages(rig.plan(t, plan.Spec.ID())) {
		receipt, known := v.Receipt.Value()
		if v.Stage == domain.Completed || v.Unresolved || !known || receipt != domain.ReceiptRefused || native.appliedCount(id) != 0 {
			t.Fatalf("refusal misrecorded: %+v applied=%d", v, native.appliedCount(id))
		}
	}
}
