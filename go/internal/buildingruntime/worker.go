package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"os"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type WorkerConfig struct {
	RoutineMethods                        bool
	StepInterval, MaxBackoff, StepTimeout time.Duration
	RenewInterval, RenewTimeout           time.Duration
	// Wake names attempts whose outcome the native clock latched; the next
	// step reconciles them first and ignores their backoff. Nil keeps the
	// ticker cadence.
	Wake *WakeSignal
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
		domain.ProductionPolicyAction, domain.BuildingTemperatureAction, domain.BedMedicalAction, domain.GrowerCropAction, domain.ExcavationAction:
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
	waits  map[domain.ActionID]workerWait
	cursor domain.ActionID
	// focus holds woken actions not yet reconciled since the wake.
	focus map[domain.ActionID]WakeOutcome
}

type workerCandidate struct {
	view    domain.ProgressView
	cleanup bool
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
	return fmt.Sprintf("stage=%s attempt=%d receipt=%s effect=%s refused=[%s] err=%v", after.Stage, after.Attempt, receipt, effect, strings.Join(reasons, ","), err)
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
	for {
		if w.ctx.Err() != nil {
			return
		}
		err := w.step(w.ctx, time.Now())
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
		// so each of them is reconciled without waiting a StepInterval.
		if len(w.focus) > 0 {
			continue
		}
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
func (w *Worker) focusNamed(id domain.ActionID) bool { _, ok := w.focus[id]; return ok }
func (w *Worker) step(ctx context.Context, now time.Time) error {
	w.takeWake()
	call, cancel := context.WithTimeout(ctx, w.config.StepTimeout)
	defer cancel()
	call, epoch, done, err := w.player.enter(call, false)
	if err != nil {
		return err
	}
	defer done()
	world, worldErr := w.player.worlds.ReadWorld(call)
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
				candidates = append(candidates, workerCandidate{view: v, cleanup: cleanup})
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
	// Catalog order is stable; rotate by the last selected action, including on
	// failed reads, so an unavailable attempt cannot monopolize reconciliation.
	// Woken actions come first: the native clock latched their outcome, so
	// reconciling them is the reason this step runs.
	start := 0
	for i, v := range candidates {
		if v.view.Action == w.cursor {
			start = (i + 1) % len(candidates)
			break
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
		w.cursor = v.Action
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
		if err == nil {
			if candidate.cleanup {
				result, err = w.session.CleanupDraft(call, v.Plan, v.Action)
			} else {
				result, err = w.session.Run(call, v.Plan, v.Action)
			}
		}
		after := v
		if result.Progress.View().Action == v.Action {
			after = result.Progress.View()
		}
		delay := w.config.StepInterval
		if workerSameView(after, v) && workerSameView(wait.view, v) && wait.scope == workerScope(scope) && wait.cleanup == candidate.cleanup {
			delay = min(wait.delay*2, workerBackoffCap(w.config, after))
		}
		// One line per change of outcome, in either log: a refusal that
		// repeats verbatim on every retry (a CAS token that never matches, a
		// native read refused for the same reason) would otherwise dominate
		// the run's log without adding anything a reader can act on (#100).
		outcome := workerOutcome(after, result, err)
		repeats := wait.repeats
		if outcome != wait.outcome {
			if err != nil {
				fmt.Fprintf(os.Stderr, "[worker] %s %s%s\n", v.Action, outcome, workerRepeats(repeats))
			} else {
				clockSchedulerLog("worker ran %s: stage %s -> %s%s", v.Action, v.Stage, outcome, workerRepeats(repeats))
			}
			repeats = 0
		} else {
			repeats++
		}
		w.waits[v.Action] = workerWait{cleanup: candidate.cleanup, view: after, scope: workerScope(w.session.State()), delay: delay, until: now.Add(delay), outcome: outcome, repeats: repeats}
		return errors.Join(worldErr, err)
	}
	return worldErr
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
