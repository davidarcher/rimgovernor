package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

type WorkerConfig struct {
	RoutineMethods                        bool
	StepInterval, MaxBackoff, StepTimeout time.Duration
	RenewInterval, RenewTimeout           time.Duration
	// Wake names attempts whose outcome the native clock latched; the next
	// step reconciles them first and ignores their backoff. Nil keeps the
	// ticker cadence.
	Wake *WakeSignal
	// Advanced, when set, is called after a step that moved an action to a
	// new stage, so the clock step loop reviews at once (issue #162).
	Advanced func()
	// Facts is the scheduler's cross-step fact cache. Every worker step
	// runs under a child of it, so a write the worker issues (a setpoint
	// patch, a dispatch) discards the facts the planners would otherwise
	// keep reading from before it; nil leaves the worker uncached (#66).
	Facts *bridge.FactCache
	// WindowRunning, when set, reports the scheduler's hint that its window
	// is running; each native call the worker issues is recorded with it
	// as a "worker_dispatch" flight row (#243), so a run can count the
	// dispatches made live and the fraction native refused.
	WindowRunning func() bool
	// Trace, when set, is the trace of the scheduler's latest step
	// (ClockWorker.Trace). Each worker step is a span under it and each
	// dispatch a span under the step, so every row a dispatch leaves joins
	// the trace of the step that admitted the window it ran in (#298).
	// Nil starts a trace per worker step.
	Trace func() telemetry.Trace
}

// routineExecutableKind lists every action kind the worker (and, for a
// non-current plan, AuthorizeRoutinePlan) is allowed to dispatch or observe.
// Growing this list is how a new action family joins live automatic
// execution; each kind here already carries its own CAS-admission-guarded
// executor/boundary pair, so this is an allowlist of what has that
// machinery, not a bypass of it.
func routineExecutableKind(kind domain.ActionKind) bool {
	switch kind {
	case domain.BuildingAction, domain.OwnedDraftAction, domain.MeleeAttackAction, domain.RangedAttackAction,
		domain.SupplyAllowAction, domain.WorkAssignmentAction, domain.AcquisitionAction, domain.ZoneCreateAction,
		domain.ProductionBillAction, domain.TendAction, domain.RescueAction, domain.CaptureAction, domain.HaulAction, domain.EquipAction,
		domain.GearReplaceAction, domain.RecoveryServiceAction, domain.MovementAction, domain.HusbandryAction,
		domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction,
		domain.ProductionPolicyAction, domain.BuildingTemperatureAction, domain.BedMedicalAction, domain.GrowerCropAction, domain.BedAssignAction, domain.ExcavationAction, domain.DialogAnswerAction, domain.NamingConfirmationAction, domain.ResearchSelectAction, domain.TradeAction, domain.CutPlantAction, domain.HomeCoverageAction, domain.QuestAcceptAction:
		return true
	default:
		return false
	}
}

type workerSession interface {
	playerSession
	Run(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error)
	CleanupDraft(context.Context, domain.PlanID, domain.ActionID) (executor.Result, error)
	ObserveTarget(context.Context, domain.GenerationSnapshot) error
}

// Worker owns Player's lifecycle, but its caller retains bridge and store handles
// until Close succeeds. Background work cannot acquire player authority.
type Worker struct {
	player      *Player
	session     workerSession
	config      WorkerConfig
	ctx         context.Context
	cancel      context.CancelFunc
	stopPlayer  func() bool
	done        chan struct{}
	stopContext func() bool
	// Only the step loop accesses scheduling state. The catalog bounds this map.
	waits map[domain.ActionID]workerWait
	// cursor is the plan of the last rotation-selected action: the next
	// step starts at the first candidate of the plan after it in the
	// catalog, even once that plan has left the catalog (retired), so
	// neither the catalog's head nor one long plan can monopolise steps.
	cursor      domain.PlanID
	cursorKnown bool
	// focus holds woken actions not yet reconciled since the wake.
	focus map[domain.ActionID]WakeOutcome
	// advanced reports that the last step moved an action to another
	// stage, so a successor may have become dispatchable.
	advanced bool
}

// workerBurstMax bounds the steps one wake or advance runs back to back.
const workerBurstMax = 16

type workerCandidate struct {
	view    domain.ProgressView
	cleanup bool
	plan    domain.PlanID
}

type workerWait struct {
	cleanup bool
	view    domain.ProgressView
	scope   ControlState
	delay   time.Duration
	until   time.Time
	// outcome is the last surfaced "stage/refusals/error" of this action, so
	// a sustained hold logs once instead of every step (see issue #70);
	// repeats counts the unlogged runs that restated it, reported when the
	// outcome next changes so a reader can see how long the hold lasted.
	outcome string
	repeats int
	// stale records that the run was held on stale facts (workerHeldStale):
	// the next stop is a new observation, so it is retried there at once.
	stale bool
}

// workerOutcome keys one action run by what a reader of the log needs to
// diagnose a stuck action: its stage, the executor's refusal reasons and the
// error. Refusal reasons matter most -- an owned draft held before prepare
// otherwise leaves no trace but a bare "building execution held".
func workerOutcome(after domain.ProgressView, result executor.Result, err error) string {
	reasons := make([]string, 0, len(result.Refused))
	for _, r := range result.Refused {
		reasons = append(reasons, string(r.Reason))
	}
	// The receipt and effect name why an attempt is unresolved: an unknown
	// receipt is a native call that timed out on the controller side (#71),
	// and with the attempt number it identifies the native ledger entry.
	receipt, effect := "-", "-"
	if v, known := after.Receipt.Value(); known {
		receipt = string(v)
	}
	if v, known := after.Effect.Value(); known {
		effect = string(v)
	}
	detail := ""
	if result.Detail != "" {
		detail = fmt.Sprintf(" detail=%q", result.Detail)
	}
	return fmt.Sprintf("stage=%s attempt=%d receipt=%s effect=%s%s refused=[%s] err=%v", after.Stage, after.Attempt, receipt, effect, detail, strings.Join(reasons, ","), err)
}

func NewWorker(ctx context.Context, config WorkerConfig, player *Player, session *Session) (*Worker, error) {
	if session == nil || player == nil || player.session != session || player.journal != session.journal {
		return nil, ErrControl
	}
	if config.RoutineMethods && !session.routineMethods {
		return nil, ErrControl
	}
	return newWorker(ctx, config, player, session)
}
func newWorker(ctx context.Context, config WorkerConfig, player *Player, session workerSession) (*Worker, error) {
	if player == nil || session == nil || player.session != session || config.StepInterval <= 0 || config.MaxBackoff < config.StepInterval || config.MaxBackoff > time.Minute || config.StepTimeout <= 0 || config.StepTimeout > player.config.CallTimeout {
		return nil, ErrControl
	}
	player.mu.Lock()
	defer player.mu.Unlock()
	if player.workerAttached || player.closing || player.closed || player.lifetime.Err() != nil {
		return nil, ErrControl
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	lifetime, cancel := context.WithCancel(ctx)
	w := &Worker{player: player, session: session, config: config, ctx: lifetime, cancel: cancel, done: make(chan struct{}), waits: make(map[domain.ActionID]workerWait)}
	player.workerAttached = true
	w.stopPlayer = context.AfterFunc(player.lifetime, cancel)
	w.stopContext = context.AfterFunc(lifetime, func() { _ = w.stop() })
	var loops sync.WaitGroup
	loops.Add(1)
	go func() { defer loops.Done(); w.steps() }()
	go func() { loops.Wait(); w.stop(); close(w.done) }()
	return w, nil
}

// stop rejects new player requests before waiting for a possibly slow native call.
func (w *Worker) stop() error {
	p := w.player
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closed || p.closing {
		return nil
	}
	p.closing = true
	p.cancelEpoch()
	return p.session.Disable()
}
func (w *Worker) Close(ctx context.Context) error {
	w.cancel()
	disableErr := w.stop()
	select {
	case <-w.done:
	case <-ctx.Done():
		return errors.Join(disableErr, ctx.Err())
	}
	// Player.Close already serializes concurrent closers through its gate.
	w.stopPlayer()
	w.stopContext()
	return errors.Join(disableErr, w.player.Close(ctx))
}
func (w *Worker) steps() {
	ticker := time.NewTicker(w.config.StepInterval)
	defer ticker.Stop()
	// The per-action outcome line already names the failing action; the
	// step-level error only adds information when it changes.
	previous, repeats := "", 0
	burst := 0
	for {
		if w.ctx.Err() != nil {
			return
		}
		err := w.step(w.ctx, time.Now())
		burst++
		message := ""
		if err != nil {
			message = err.Error()
		}
		if message != previous {
			clockSchedulerLog("worker step: err=%v%s", err, workerRepeats(repeats))
			previous, repeats = message, 0
		} else {
			repeats++
		}
		// A wake steps at once; while focused actions remain, keep stepping
		// so each of them is reconciled without waiting a StepInterval, and
		// a step that advanced an action steps again so the successor it
		// unblocked dispatches before the clock readmits a window rather
		// than a StepInterval or its own backoff later (issue #162).
		if (len(w.focus) > 0 || w.advanced) && burst < workerBurstMax {
			continue
		}
		burst = 0
		select {
		case <-w.ctx.Done():
			return
		case <-ticker.C:
		case <-w.config.Wake.C():
		}
	}
}

// workerFocusMax bounds the woken set a single step carries forward.
const workerFocusMax = 64

func (w *Worker) takeWake() {
	woken, _ := w.config.Wake.Take()
	if len(woken) == 0 {
		return
	}
	// Latched outcomes change what every other action in their plans is
	// waiting on: drop the backoffs so successors are reconsidered at once.
	w.waits = map[domain.ActionID]workerWait{}
	if w.focus == nil {
		w.focus = map[domain.ActionID]WakeOutcome{}
	}
	for id, outcome := range woken {
		if len(w.focus) >= workerFocusMax {
			break
		}
		w.focus[id] = outcome
		delete(w.waits, id)
	}
}

// readWorld names the loaded world a step reconciles against. The step
// needs the load, not its tick, so the identity row the scheduler's bundle
// read seeds into Facts each step serves it without a round trip; a write
// or a scope change drops that row and the next step reads natively (#181).
func (w *Worker) readWorld(ctx context.Context) (store.World, error) {
	if cached, ok := w.config.Facts.Context(); ok && bridge.ValidateContext(cached.Context) == nil {
		identity := cached.Context.GetIdentity()
		world := store.World{Colony: domain.ColonyID(identity.GetColonyId()), Load: domain.LoadID(identity.GetLoadToken()), Map: domain.MapID(identity.GetMapId())}
		if world.Validate() == nil {
			return world, ctx.Err()
		}
	}
	return w.player.worlds.ReadWorld(ctx)
}
func (w *Worker) focusNamed(id domain.ActionID) bool { _, ok := w.focus[id]; return ok }
func (w *Worker) step(ctx context.Context, now time.Time) error {
	w.advanced = false
	w.takeWake()
	call, cancel := context.WithTimeout(ctx, w.config.StepTimeout)
	defer cancel()
	var parent telemetry.Trace
	if w.config.Trace != nil {
		parent = w.config.Trace()
	}
	stepTrace := parent.Child()
	call = telemetry.WithTrace(call, stepTrace)
	if w.config.Facts != nil {
		call = bridge.WithStepReadCache(call, bridge.NewChildReadCache(w.config.Facts))
	}
	call, epoch, done, err := w.player.enter(call, false)
	if err != nil {
		return err
	}
	defer done()
	world, worldErr := w.readWorld(call)
	if worldErr == nil {
		worldErr = world.Validate()
	}
	if worldErr != nil {
		if err = w.session.Disable(); err != nil {
			return errors.Join(worldErr, err)
		}
	}
	if err = w.player.current(call, epoch); err != nil {
		return err
	}
	scope := w.session.State()
	// Identity is a prerequisite for retaining permission, not just dispatch.
	// Keep the private cleanup scope, but stop renewal of an unavailable or
	// different world before considering any read-only reconciliation target.
	if scope.ObservationKnown && playerWorld(scope.Snapshot) != world || scope.Enabled && !scope.ObservationKnown {
		if err = w.session.Disable(); err != nil {
			return err
		}
		scope = w.session.State()
	}
	plans, err := w.player.journal.LoadPlans(call, 256)
	if err != nil {
		return err
	}
	live := make(map[domain.ActionID]bool)
	var candidates []workerCandidate
	// One author: the root plan carries authority, and every other plan --
	// routine method or player submission -- is dispatched under it once
	// authorized. There is no priority arbitration between them.
	for _, plan := range plans {
		planScope := scope
		if scope.Enabled && scope.ObservationKnown && plan.Spec.ID() != scope.Snapshot.Plan {
			target := scope.Snapshot
			target.Plan, target.Revision = plan.Spec.ID(), plan.Spec.Revision()
			if authErr := (planAuthorizer{w.player.journal, w.config.RoutineMethods}).AuthorizeRoutinePlan(call, scope.Snapshot, target); authErr == nil {
				planScope.Snapshot = target
			} else if clockSchedulerDebug {
				clockSchedulerLog("worker: authorize plan=%s root=%+v err=%v", plan.Spec.ID(), scope.Snapshot, authErr)
			}
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			cleanup := workerCleanupEligible(plan, v, planScope, world)
			routineObservation := w.config.RoutineMethods && v.Unresolved && routineExecutableKind(progress.Action().Kind()) && playerWorld(v.Snapshot) == world
			if clockSchedulerDebug && progress.Action().Kind() == domain.BuildingTemperatureAction {
				clockSchedulerLog("worker: temperature candidate action=%s stage=%v authorized=%v eligible=%v worldErr=%v", v.Action, v.Stage, planScope.Snapshot != scope.Snapshot, workerEligible(plan, v, planScope, world), worldErr)
			}
			if cleanup || worldErr == nil && (routineObservation || workerEligible(plan, v, planScope, world)) {
				live[v.Action] = true
				candidates = append(candidates, workerCandidate{view: v, cleanup: cleanup, plan: plan.Spec.ID()})
			}
		}
	}
	for id := range w.waits {
		if !live[id] {
			delete(w.waits, id)
		}
	}
	for id := range w.focus {
		if !live[id] {
			delete(w.focus, id)
		}
	}
	// Catalog order is stable; rotate by plan, including on failed reads,
	// so an unavailable attempt cannot monopolize reconciliation. The
	// rotation resumes at the first candidate of the plan after the last
	// selection's, whether or not that plan still lists one: rotating by
	// action gave a forty-action shell plan every step until its last
	// action and restarted at the catalog's head whenever the selected
	// action completed, so the work-assignment plan sorted after the shell
	// never had a turn (#322). Within a plan the first candidate is its
	// earliest action, so a sequential plan still progresses in order.
	// Woken actions come first: the native clock latched their outcome, so
	// reconciling them is the reason this step runs; they leave the
	// rotation where it was.
	start := 0
	if w.cursorKnown {
		start = len(candidates)
		for i, v := range candidates {
			if v.plan > w.cursor {
				start = i
				break
			}
		}
		if len(candidates) > 0 {
			start %= len(candidates)
		}
	}
	ordered := make([]workerCandidate, 0, len(candidates))
	for _, focused := range []bool{true, false} {
		for n := 0; n < len(candidates); n++ {
			if candidate := candidates[(start+n)%len(candidates)]; w.focusNamed(candidate.view.Action) == focused {
				ordered = append(ordered, candidate)
			}
		}
	}
	for _, candidate := range ordered {
		v := candidate.view
		wait := w.waits[v.Action]
		focused := w.focusNamed(v.Action)
		if !focused && workerSameView(wait.view, v) && wait.scope == workerScope(scope) && wait.cleanup == candidate.cleanup && now.Before(wait.until) {
			continue
		}
		delete(w.focus, v.Action)
		if !focused {
			w.cursor, w.cursorKnown = candidate.plan, true
		}
		if err = w.player.current(call, epoch); err != nil {
			return err
		}
		if !candidate.cleanup && !scope.Enabled {
			err = w.session.ObserveTarget(call, v.Snapshot)
		}
		if err == nil {
			err = w.player.current(call, epoch)
		}
		var result executor.Result
		running := w.config.WindowRunning != nil && w.config.WindowRunning()
		run, tally := bridge.WithReadTally(telemetry.WithTrace(call, stepTrace.Child()))
		if err == nil {
			if candidate.cleanup {
				result, err = w.session.CleanupDraft(run, v.Plan, v.Action)
			} else {
				result, err = w.session.Run(run, v.Plan, v.Action)
			}
		}
		after := v
		if result.Progress.View().Action == v.Action {
			after = result.Progress.View()
		}
		if result.NativeCalled {
			workerDispatchRow(run, tally, candidate.view, after, running, err)
		}
		delay := w.config.StepInterval
		if workerSameView(after, v) && workerSameView(wait.view, v) && wait.scope == workerScope(scope) && wait.cleanup == candidate.cleanup {
			delay = min(wait.delay*2, workerBackoffCap(w.config, after))
		}
		w.advanced = err == nil && after.Stage != v.Stage
		if w.advanced && w.config.Advanced != nil {
			w.config.Advanced()
		}
		// A dispatch held on stale facts (the authority or tick moved under
		// its inspection) clears on the next observation, so it is retried
		// at once, off its backoff, before the game's own work scanner takes
		// the order's target (#288); the clock is not held for it, since
		// every routine kind dispatches under the running window (#244). One
		// retry per hold: a hold that survives it backs off as usual.
		stale := !candidate.cleanup && workerHeldStale(after, result, err)
		if stale && !wait.stale {
			if w.focus == nil {
				w.focus = map[domain.ActionID]WakeOutcome{}
			}
			w.focus[v.Action] = WakeOutcome{Action: v.Action, Attempt: after.Attempt}
		}
		// One line per change of outcome, in either log: a refusal that
		// repeats verbatim on every retry (a CAS token that never matches, a
		// native read refused for the same reason) would otherwise dominate
		// the run's log without adding anything a reader can act on (#100).
		outcome := workerOutcome(after, result, err)
		repeats := wait.repeats
		if outcome != wait.outcome {
			workerOutcomeEvent(run, v, after, outcome, err, repeats)
			repeats = 0
		} else {
			repeats++
		}
		w.waits[v.Action] = workerWait{cleanup: candidate.cleanup, view: after, scope: workerScope(w.session.State()), delay: delay, until: now.Add(delay), outcome: outcome, repeats: repeats, stale: stale}
		return errors.Join(worldErr, err)
	}
	return worldErr
}

// workerDispatchRow publishes one "worker_dispatch" flight row for a run
// that reached native: the action's kind and attempt, the receipt the run
// left (accepted, refused, unknown; "-" when the write was not a dispatch),
// whether the scheduler's window was running when the run began (#243),
// and the error. `rimgovernor phases` sums them (bridge.DispatchSample).
func workerDispatchRow(ctx context.Context, tally *bridge.ReadTally, before, after domain.ProgressView, running bool, err error) {
	receipt := "-"
	if v, known := after.Receipt.Value(); known && (after.Attempt != before.Attempt || !workerSameReceipt(before, after)) {
		receipt = string(v)
	}
	extra := map[string]any{"action": string(after.Action), "attempt": after.Attempt, "stage": string(after.Stage), "receipt": receipt, "running": running}
	if err != nil {
		extra["error"] = err.Error()
	}
	tally.PublishAs(ctx, "worker_dispatch", extra)
}

func workerSameReceipt(a, b domain.ProgressView) bool {
	x, xk := a.Receipt.Value()
	y, yk := b.Receipt.Value()
	return xk == yk && x == y
}

// workerOutcomeEvent publishes one "worker_outcome" event when an action's
// reconciliation outcome changes: the action, its stage before and after
// the run, the outcome text, and the error, at Warn when the run failed.
func workerOutcomeEvent(ctx context.Context, before, after domain.ProgressView, outcome string, err error, repeats int) {
	level := slog.LevelInfo
	if err != nil {
		level = slog.LevelWarn
	}
	slog.Default().Log(ctx, level, "worker outcome", telemetry.ComponentKey, "worker", telemetry.KindKey, "worker_outcome", "action", string(before.Action), "attempt", int64(after.Attempt), "stage", string(before.Stage), "stage_after", string(after.Stage), "outcome", outcome, "err", err, "repeated", repeats)
}

// workerRepeats renders how many unlogged runs restated the previous outcome.
func workerRepeats(n int) string {
	if n == 0 {
		return ""
	}
	return fmt.Sprintf(" (previous outcome repeated %d more times)", n)
}

// workerSameView reports whether two views of one action describe the same
// reconciliation state. The observed tick advances with every read of a
// running game, so it is not a change; a read that only restated an
// unresolved effect must back off like any other unchanged attempt, or a
// handful of unresolvable observations monopolise the one-action-per-step
// rotation and starve the plans that could progress.
func workerSameView(a, b domain.ProgressView) bool {
	a.Tick, b.Tick = 0, 0
	a.ConstructionObserved, b.ConstructionObserved = domain.Unknown[domain.Tick](), domain.Unknown[domain.Tick]()
	return a == b
}

// workerBackoffCap bounds the retry delay of an unchanged attempt. An
// observation the native side could not classify (EffectUnknown: the source
// or output is gone, the pawn is unobservable) rarely resolves within the
// ordinary cap and is rechecked at most once a minute; everything else keeps
// the configured cap so pending work is noticed promptly.
func workerBackoffCap(config WorkerConfig, v domain.ProgressView) time.Duration {
	if effect, known := v.Effect.Value(); known && effect == domain.EffectUnknown && v.Stage == domain.AwaitingObservation {
		return max(config.MaxBackoff, min(6*config.MaxBackoff, time.Minute))
	}
	return config.MaxBackoff
}

// liveDispatchKind reports a kind whose native operation validates its own
// preconditions at apply time and refuses with a named reason (#242,
// action-contracts.md "Apply-time preconditions"), so the Worker dispatches
// it under a running window as readily as between windows (#243): a world
// that moved under the order is a refusal the Worker reconciles, not a
// wrong effect. Every routine kind the Worker dispatches is one (#244):
// nothing native gates an admission on a paused map any more, so no
// admission waits for the stop between windows.
func liveDispatchKind(kind domain.ActionKind) bool {
	switch kind {
	case domain.BuildingAction, domain.HaulAction, domain.SupplyAllowAction, domain.WorkAssignmentAction, domain.ZoneCreateAction,
		domain.ProductionBillAction, domain.GrowerCropAction, domain.AcquisitionAction, domain.MineAcquisitionAction, domain.HusbandryAction,
		domain.ExcavationAction, domain.BedAssignAction, domain.WallRemovalAction, domain.ProductionPolicyAction, domain.ResearchSelectAction, domain.HomeCoverageAction, domain.CutPlantAction:
		return true
	}
	return false
}

// workerHeldStale reports a run held before dispatch on facts from a
// generation or tick the current one has outrun: the executor's stale_facts
// refusal, or an emergency hold recorded for the same reason. Such a hold is
// expected to clear on the next observation, unlike a refusal that names a
// world condition (stock, geometry, an unavailable pawn).
func workerHeldStale(after domain.ProgressView, result executor.Result, err error) bool {
	if !errors.Is(err, executor.ErrHeld) || after.Unresolved || after.Stage != domain.Pending && after.Stage != domain.Prepared {
		return false
	}
	for _, refusal := range result.Refused {
		if refusal.Reason == policy.StaleFacts {
			return true
		}
	}
	if len(result.Refused) > 0 {
		return false
	}
	held, known := after.HeldReason.Value()
	if !known {
		return false
	}
	for _, reason := range held.Reasons() {
		if reason == domain.HeldStaleFacts {
			return true
		}
	}
	return false
}

func workerEligible(plan store.PlanState, v domain.ProgressView, scope ControlState, world store.World) bool {
	supported := false
	for _, action := range plan.Spec.Actions() {
		if action.ID() == v.Action && routineExecutableKind(action.Kind()) {
			supported = true
			break
		}
	}
	if !supported {
		return false
	}
	if cleanup, known := v.DraftCleanup.Value(); known && (cleanup.Stage == domain.DraftReleased || cleanup.Stage == domain.DraftSuperseded) {
		return false
	}
	if scope.Enabled {
		if !scope.ObservationKnown || playerWorld(scope.Snapshot) != world || scope.Snapshot.Plan != v.Plan || scope.Snapshot.Revision != v.Revision {
			return false
		}
	} else {
		return v.Unresolved && playerWorld(v.Snapshot) == world
	}
	if v.Unresolved {
		return playerWorld(v.Snapshot) == world
	}
	if v.Stage != domain.Pending && v.Stage != domain.Prepared {
		return false
	}
	// A Prepared action has no write outstanding (dispatch is journaled before
	// the native call), so one whose snapshot went stale -- the step budget
	// cancelled its dispatch and the native generation then moved -- is
	// re-prepared under the current scope by the next run rather than
	// stranded behind an authority it can never dispatch against (#101).
	if v.Attempt == 0 {
		return true
	}
	// A trusted refusal is a no-effect proof. Only a later resume (a newer
	// native generation) authorizes another attempt; an unknown receipt always
	// stays reconciliation. A write the transport never issued refused
	// nothing, so the same generation may retry it.
	receipt, known := v.Receipt.Value()
	effect, effectKnown := v.Effect.Value()
	if !known || !effectKnown || effect != domain.EffectAbsent || playerWorld(v.Snapshot) != world {
		return false
	}
	// A refused attempt already re-prepared carries the newer generation that
	// authorized it, so it stays eligible until it dispatches.
	return receipt == domain.ReceiptUnsent || receipt == domain.ReceiptRefused && (v.Stage == domain.Prepared || scope.Snapshot.Native > v.Snapshot.Native)
}

// Read reconciliation may rotate plan targets without changing world authority.
// That rotation must not reset a paused attempt's cooldown.
func workerScope(scope ControlState) ControlState {
	if !scope.Enabled {
		scope.Snapshot.Plan = ""
		scope.Snapshot.Revision = 0
	}
	return scope
}

// Draft ownership outlives ordinary progress. Completed drafts remain useful only
// while their original active plan still has unfinished, nonfailed work.
func workerCleanupEligible(plan store.PlanState, v domain.ProgressView, scope ControlState, world store.World) bool {
	cleanup, known := v.DraftCleanup.Value()
	if !known || v.Attempt == 0 {
		return false
	}
	switch cleanup.Stage {
	case domain.DraftAwaitingClaim, domain.DraftCleanupRequired, domain.DraftCleanupDispatched, domain.DraftCleanupUncertain:
	default:
		return false
	}
	draft := false
	for _, a := range plan.Spec.Actions() {
		if a.ID() == v.Action {
			_, draft = a.OwnedDraft()
			break
		}
	}
	if !draft {
		return false
	}
	if !scope.Enabled || !scope.ObservationKnown || playerWorld(scope.Snapshot) != world || scope.Snapshot != v.Snapshot {
		return true
	}
	if v.Stage == domain.Cancelled || v.Stage == domain.Unsuccessful {
		return true
	}
	if v.Stage != domain.Completed {
		return false
	}
	unfinished := false
	for _, p := range plan.Progress {
		other := p.View()
		if other.Action == v.Action {
			continue
		}
		switch other.Stage {
		case domain.Cancelled, domain.Unsuccessful:
			return true
		case domain.Pending, domain.Prepared, domain.Dispatched, domain.AwaitingObservation:
			unfinished = true
		}
	}
	return !unfinished
}
