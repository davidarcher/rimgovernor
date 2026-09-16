package buildingruntime

import (
	"context"
	"errors"
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
		domain.GearReplaceAction, domain.RecoveryServiceAction, domain.HusbandryAction,
		domain.PrisonerInteractionAction, domain.RepairAction, domain.CleanAction, domain.MineAcquisitionAction,
		domain.ProductionPolicyAction, domain.BuildingTemperatureAction:
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
	for {
		if w.ctx.Err() != nil {
			return
		}
		if err := w.step(w.ctx, time.Now()); err != nil {
			clockSchedulerLog("worker step: %v", err)
		}
		select {
		case <-w.ctx.Done():
			return
		case <-ticker.C:
		}
	}
}
func (w *Worker) step(ctx context.Context, now time.Time) error {
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
			if (planAuthorizer{w.player.journal, w.config.RoutineMethods}).AuthorizeRoutinePlan(call, scope.Snapshot, target) == nil {
				planScope.Snapshot = target
			} else if clockSchedulerDebug {
				clockSchedulerLog("worker: authorize plan=%s root=%+v err=%v", plan.Spec.ID(), scope.Snapshot, err)
			}
		}
		for _, progress := range plan.Progress {
			v := progress.View()
			cleanup := workerCleanupEligible(plan, v, scope, world)
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
	// Catalog order is stable; rotate by the last selected action, including on
	// failed reads, so an unavailable attempt cannot monopolize reconciliation.
	start := 0
	for i, v := range candidates {
		if v.view.Action == w.cursor {
			start = (i + 1) % len(candidates)
			break
		}
	}
	for n := 0; n < len(candidates); n++ {
		candidate := candidates[(start+n)%len(candidates)]
		v := candidate.view
		wait := w.waits[v.Action]
		if wait.view == v && wait.scope == workerScope(scope) && wait.cleanup == candidate.cleanup && now.Before(wait.until) {
			continue
		}
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
		if clockSchedulerDebug {
			clockSchedulerLog("worker: action=%s kind=%v cleanup=%v stage=%v->%v err=%v", v.Action, candidate.view.Plan, candidate.cleanup, v.Stage, after.Stage, err)
		}
		delay := w.config.StepInterval
		if after == v && wait.view == v && wait.scope == workerScope(scope) && wait.cleanup == candidate.cleanup {
			delay = wait.delay * 2
			if delay > w.config.MaxBackoff {
				delay = w.config.MaxBackoff
			}
		}
		w.waits[v.Action] = workerWait{cleanup: candidate.cleanup, view: after, scope: workerScope(w.session.State()), delay: delay, until: now.Add(delay)}
		clockSchedulerLog("worker ran %s: stage %s -> %s attempt %d err=%v", v.Action, v.Stage, after.Stage, after.Attempt, err)
		return errors.Join(worldErr, err)
	}
	return worldErr
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
	if v.Attempt == 0 {
		return v.Stage != domain.Prepared || v.Snapshot == scope.Snapshot
	}
	// A trusted refusal is a no-effect proof. Only a later resume (a newer
	// native generation) authorizes another attempt; an unknown receipt always
	// stays reconciliation.
	receipt, known := v.Receipt.Value()
	effect, effectKnown := v.Effect.Value()
	return v.Stage == domain.Pending && known && receipt == domain.ReceiptRefused && effectKnown && effect == domain.EffectAbsent && playerWorld(v.Snapshot) == world && scope.Snapshot.Native > v.Snapshot.Native
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
