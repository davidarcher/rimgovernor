package buildingruntime

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/facts"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type WorldSource interface {
	ReadWorld(context.Context) (store.World, error)
}
type PlayerConfig struct {
	CallTimeout, JournalTimeout time.Duration
}

type playerSession interface {
	Acquire(context.Context, domain.GenerationSnapshot) (domain.GenerationSnapshot, error)
	Manual(context.Context) error
	ManualForResume(context.Context) error
	HoldsGrant(domain.GenerationSnapshot) bool
	TargetsWorld(store.World) bool
	Disable() error
	State() ControlState
	Close(context.Context) error
}

// Player coordinates explicit trusted player requests. HTTP authentication and
// admission belong to its caller. It never runs plans, renews leases or resumes time.
type Player struct {
	extentFacts     *facts.Store
	mu              sync.Mutex
	gate            chan struct{}
	config          PlayerConfig
	journal         *store.Store
	session         playerSession
	worlds          WorldSource
	lifetime        context.Context
	epoch           context.Context
	cancelEpoch     context.CancelFunc
	stopLifetime    func() bool
	closing, closed bool
	workerAttached  bool
	// holder names the caller holding the gate and when it took it (#1267),
	// so a slow wait can name what it queued behind.
	holder      string
	holderSince time.Time
	// queued, when set, runs after a call has read its epoch and before it
	// waits on the gate. Tests use it to order a queued call against Manual.
	queued func()
}

func NewPlayer(ctx context.Context, config PlayerConfig, journal *store.Store, session *Session, worlds WorldSource) (*Player, error) {
	if session == nil || session.journal != journal {
		return nil, fmt.Errorf("%w: NewPlayer: session == nil || session.journal != journal", ErrControl)
	}
	return newPlayer(ctx, config, journal, session, worlds)
}
func newPlayer(ctx context.Context, config PlayerConfig, journal *store.Store, session playerSession, worlds WorldSource) (*Player, error) {
	if journal == nil || session == nil || worlds == nil || config.CallTimeout <= 0 || config.CallTimeout > time.Minute || config.JournalTimeout <= 0 || config.JournalTimeout > time.Minute {
		return nil, fmt.Errorf("%w: newPlayer: journal == nil || session == nil || worlds == nil || config.CallTimeout <= 0 || config.CallTimeout > time.M", ErrControl)
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if err := session.Disable(); err != nil {
		return nil, err
	}
	epoch, cancel := context.WithCancel(ctx)
	p := &Player{gate: make(chan struct{}, 1), config: config, journal: journal, session: session, worlds: worlds, lifetime: ctx, epoch: epoch, cancelEpoch: cancel}
	p.stopLifetime = context.AfterFunc(ctx, func() {
		p.mu.Lock()
		defer p.mu.Unlock()
		if !p.closed {
			p.cancelEpoch()
			_ = p.session.Disable()
		}
	})
	return p, nil
}

func (p *Player) State() ControlState { return p.session.State() }

// gateWait describes one caller's wait for the player gate: how long it
// waited and who held the gate when the wait began (#1267).
type gateWait struct {
	wait       time.Duration
	holder     string
	holderHeld time.Duration
}

// slowGateWait is the wait past which enter names the gate's holder.
const slowGateWait = 100 * time.Millisecond

// enter takes the player gate for a caller named by label (clock_step,
// routine_review, submit, manual, ...).
func (p *Player) enter(ctx context.Context, label string, manual bool) (context.Context, context.Context, func(), error) {
	call, epoch, done, _, err := p.enterTimed(ctx, label, manual)
	return call, epoch, done, err
}

func (p *Player) enterTimed(ctx context.Context, label string, manual bool) (context.Context, context.Context, func(), gateWait, error) {
	var waited gateWait
	p.mu.Lock()
	if p.closing || p.closed || p.lifetime.Err() != nil {
		p.mu.Unlock()
		return nil, nil, nil, waited, fmt.Errorf("%w: enter: p.closing || p.closed || p.lifetime.Err() != nil", ErrControl)
	}
	if manual {
		p.cancelEpoch()
		p.epoch, p.cancelEpoch = context.WithCancel(p.lifetime)
		// Local safety comes before a potentially stale request, queue or database.
		if err := p.session.Disable(); err != nil {
			p.mu.Unlock()
			return nil, nil, nil, waited, err
		}
	}
	epoch := p.epoch
	holder, holderSince := p.holder, p.holderSince
	p.mu.Unlock()
	began := time.Now()
	// The wait for the gate and the call under it are budgeted apart, each
	// by CallTimeout: a caller queued behind a long scheduler step (its
	// planner reads run 5-11 s under peer load, up to serviceClockStepTimeout)
	// used to enter with a budget the wait had spent and lose its first
	// native call to that deadline (#410). Both waits end with the epoch.
	wait, cancelWait := context.WithTimeout(ctx, p.config.CallTimeout)
	stopWait := context.AfterFunc(epoch, cancelWait)
	if p.queued != nil {
		p.queued()
	}
	select {
	case p.gate <- struct{}{}:
		stopWait()
		cancelWait()
	case <-wait.Done():
		stopWait()
		cancelWait()
		return nil, nil, nil, waited, wait.Err()
	}
	acquired := time.Now()
	waited.wait = acquired.Sub(began)
	if holder != "" {
		waited.holder = holder
		waited.holderHeld = acquired.Sub(holderSince)
	}
	p.mu.Lock()
	p.holder, p.holderSince = label, acquired
	p.mu.Unlock()
	if waited.wait > slowGateWait {
		name := waited.holder
		if name == "" {
			name = "unknown"
		}
		clockSchedulerLog("player gate: %s waited %s behind %s (held %s)", label, waited.wait.Round(time.Millisecond), name, waited.holderHeld.Round(time.Millisecond))
	}
	release := func() {
		p.mu.Lock()
		p.holder, p.holderSince = "", time.Time{}
		p.mu.Unlock()
		<-p.gate
	}
	call, cancel := context.WithTimeout(ctx, p.config.CallTimeout)
	stop := context.AfterFunc(epoch, cancel)
	cleanup := func() { stop(); cancel() }
	if err := p.current(call, epoch); err != nil {
		release()
		cleanup()
		return nil, nil, nil, waited, err
	}
	return call, epoch, func() { release(); cleanup() }, waited, nil
}

// observe opens a bounded context for a read-only caller that claims no gate:
// a census issues only native reads, which need no ordering against the
// planner steps and writes that hold the gate (a step holds it up to
// CallTimeout), so queuing one behind them only makes it miss its deadline.
func (p *Player) observe(ctx context.Context) (context.Context, func(), error) {
	p.mu.Lock()
	closed := p.closing || p.closed || p.lifetime.Err() != nil
	p.mu.Unlock()
	if closed {
		return nil, nil, fmt.Errorf("%w: observe: p.closing || p.closed || p.lifetime.Err() != nil", ErrControl)
	}
	call, cancel := context.WithTimeout(ctx, p.config.CallTimeout)
	return call, cancel, nil
}
func (p *Player) current(ctx, epoch context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closing || p.closed || p.epoch != epoch || epoch.Err() != nil {
		return fmt.Errorf("%w: current: p.closing || p.closed || p.epoch != epoch || epoch.Err() != nil", ErrControl)
	}
	return nil
}
func (p *Player) world(ctx context.Context, expected store.World) error {
	if err := expected.Validate(); err != nil {
		return err
	}
	actual, err := p.worlds.ReadWorld(ctx)
	if err != nil {
		return err
	}
	if err = actual.Validate(); err != nil {
		return err
	}
	if actual != expected {
		return store.ErrConflict
	}
	return ctx.Err()
}

func (p *Player) Submit(ctx context.Context, request store.SubmissionRequest) (store.Submission, bool, error) {
	call, epoch, done, err := p.enter(ctx, "submit", false)
	if err != nil {
		return store.Submission{}, false, err
	}
	defer done()
	old, err := p.journal.LookupSubmission(call, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.Submission{}, false, store.ErrConflict
		}
		return old, false, nil
	}
	if !errors.Is(err, store.ErrNotFound) {
		return store.Submission{}, false, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.Submission{}, false, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.Submission{}, false, err
	}
	return p.journal.SubmitBuilding(call, request)
}

// Historical replay is returned before any native effect; even Granted never
// restores a live lease. Actual permission is reported separately by State.
func (p *Player) lookup(ctx context.Context, request store.ControlRequest) (store.ControlRecord, bool, error) {
	old, err := p.journal.LookupControl(ctx, request.RequestID)
	if err == nil {
		if old.Request != request {
			return store.ControlRecord{}, false, store.ErrConflict
		}
		return old, true, nil
	}
	if errors.Is(err, store.ErrNotFound) {
		return store.ControlRecord{}, false, nil
	}
	return store.ControlRecord{}, false, err
}
func (p *Player) finish(record store.ControlRecord, phase store.ControlPhase, generation domain.NativeGeneration, cause error) (store.ControlRecord, error) {
	ctx, cancel := context.WithTimeout(context.Background(), p.config.JournalTimeout)
	defer cancel()
	result, err := p.journal.CompleteControl(ctx, record.Request.RequestID, phase, generation)
	if err != nil {
		return record, errors.Join(cause, err, p.session.Disable())
	}
	return result, cause
}
func (p *Player) uncertain(record store.ControlRecord, cause error) (store.ControlRecord, error) {
	return p.finish(record, store.UncertainControl, 0, errors.Join(cause, p.session.Disable()))
}

// Resume enables autonomous play for the requested world under its root plan.
func (p *Player) Resume(ctx context.Context, request store.ControlRequest) (store.ControlRecord, error) {
	call, epoch, done, err := p.enter(ctx, "resume", false)
	if err != nil {
		return store.ControlRecord{}, err
	}
	defer done()
	if request.Kind != store.ResumeControl {
		return store.ControlRecord{}, store.ErrConflict
	}
	if old, found, err := p.lookup(call, request); err != nil || found {
		return old, err
	}
	if err = p.session.Disable(); err != nil {
		return store.ControlRecord{}, err
	}
	if _, err = p.stopRoutine(call); err != nil {
		return store.ControlRecord{}, err
	}
	if err = p.world(call, request.World); err != nil {
		return store.ControlRecord{}, err
	}
	if err = p.current(call, epoch); err != nil {
		return store.ControlRecord{}, err
	}
	record, created, err := p.journal.BeginControl(call, request)
	if err != nil || !created {
		return record, err
	}
	if err = p.current(call, epoch); err != nil {
		return p.uncertain(record, err)
	}
	root, err := p.journal.EnsureRootPlan(call, request.World)
	if err != nil {
		return p.uncertain(record, err)
	}
	snapshot := domain.GenerationSnapshot{Colony: request.World.Colony, Load: request.World.Load, Map: request.World.Map, Plan: root.Spec.ID(), Revision: root.Spec.Revision()}
	// A grant this process still holds for that scope as far as it has
	// observed (disabled locally on a clock hold, or live) is re-acquired in
	// place by Acquire: one native generation per resume, not the Manual->Auto
	// pair that rebinds every prepared action (#259). Manual first stays for
	// a genuine switch: an observed revocation or an uncertain grant.
	// Manual also revokes a grant left standing by a failed status read
	// (observation unknown, #328); the held-grant check answers that case
	// by re-acquiring it in place instead.
	// That Manual keeps the drafts a plan or an open fight still holds, as
	// the Acquire drain does: a player pause and resume mid-raid must not
	// undraft the defenders (#916).
	if p.session.TargetsWorld(request.World) && !p.session.HoldsGrant(snapshot) {
		if err = p.session.ManualForResume(call); err != nil {
			return p.uncertain(record, err)
		}
	}
	// Recheck actual world after cleanup, before attempting new authority.
	if err = p.world(call, request.World); err != nil {
		return p.uncertain(record, err)
	}
	if err = p.current(call, epoch); err != nil {
		return p.uncertain(record, err)
	}
	granted, err := p.session.Acquire(call, snapshot)
	if err != nil {
		return p.uncertain(record, err)
	}
	if err = p.current(call, epoch); err != nil {
		return p.uncertain(record, err)
	}
	expected := snapshot
	expected.Native = granted.Native
	actual := p.session.State()
	if granted.Native == 0 || granted != expected || !actual.Enabled || !actual.ObservationKnown || actual.Snapshot != granted {
		return p.uncertain(record, fmt.Errorf("%w: Resume: granted.Native == 0 || granted != expected || !actual.Enabled || !actual.ObservationKnown || actual.Snapsho", ErrControl))
	}
	return p.finish(record, store.RunningControl, granted.Native, nil)
}
func playerWorld(snapshot domain.GenerationSnapshot) store.World {
	return store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
}

// Pause always stops local writes first, including on historical replay or a
// stale browser world. Only fresh matching scope permits native cleanup.
func (p *Player) Pause(ctx context.Context, request store.ControlRequest) (store.ControlRecord, error) {
	call, epoch, done, err := p.enter(ctx, "manual", true)
	if err != nil {
		return store.ControlRecord{}, err
	}
	defer done()
	if _, err = p.stopRoutine(call); err != nil {
		return store.ControlRecord{}, err
	}
	if request.Kind != store.PauseControl {
		return store.ControlRecord{}, store.ErrConflict
	}
	if old, found, err := p.lookup(call, request); err != nil || found {
		return old, err
	}
	record, created, err := p.journal.BeginControl(call, request)
	if err != nil || !created {
		return record, err
	}
	if err = p.world(call, request.World); err != nil {
		return p.finish(record, store.RefusedControl, 0, err)
	}
	if err = p.current(call, epoch); err != nil {
		return p.uncertain(record, err)
	}
	// Manual revokes this process's own grant for the world when its
	// observation is known, and also when a failed status read left the
	// observation unknown while the grant still stands natively -- Acquire
	// refuses an Active it once targeted, so without the revoke every later
	// request would end uncertain (#328).
	if p.session.TargetsWorld(request.World) {
		if err = p.session.Manual(call); err != nil {
			return p.uncertain(record, err)
		}
	}
	if err = p.current(call, epoch); err != nil {
		return p.uncertain(record, err)
	}
	return p.finish(record, store.PausedControl, 0, nil)
}

// Close retains Session and caller-owned bridge/store handles on failed drain.
// It may be retried, but no new player request is accepted once closing begins.
func (p *Player) Close(ctx context.Context) error {
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()
		return nil
	}
	p.closing = true
	p.cancelEpoch()
	disableErr := p.session.Disable()
	p.mu.Unlock()
	call, cancel := context.WithTimeout(ctx, p.config.CallTimeout)
	defer cancel()
	select {
	case p.gate <- struct{}{}:
		defer func() { <-p.gate }()
	case <-call.Done():
		return errors.Join(disableErr, call.Err())
	}
	p.mu.Lock()
	closed := p.closed
	p.mu.Unlock()
	if closed {
		return nil
	}
	if err := p.session.Close(call); err != nil {
		return errors.Join(disableErr, err)
	}
	p.mu.Lock()
	p.closed = true
	p.stopLifetime()
	p.mu.Unlock()
	return nil
}
