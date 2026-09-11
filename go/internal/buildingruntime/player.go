package buildingruntime

import (
	"context"
	"errors"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type WorldSource interface {
	ReadWorld(context.Context) (store.World, error)
}
type PlayerConfig struct{ CallTimeout, JournalTimeout time.Duration }

type playerSession interface {
	ResourceRules() []policy.ResourceRule
	Acquire(context.Context, domain.GenerationSnapshot) (domain.GenerationSnapshot, error)
	Manual(context.Context) error
	Disable() error
	State() ControlState
	Close(context.Context) error
}

// Player coordinates explicit trusted player requests. HTTP authentication and
// admission belong to its caller. It never runs plans, renews leases or resumes time.
type Player struct {
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
}

func NewPlayer(ctx context.Context, config PlayerConfig, journal *store.Store, session *Session, worlds WorldSource) (*Player, error) {
	if session == nil || session.journal != journal {
		return nil, ErrControl
	}
	return newPlayer(ctx, config, journal, session, worlds)
}
func newPlayer(ctx context.Context, config PlayerConfig, journal *store.Store, session playerSession, worlds WorldSource) (*Player, error) {
	if journal == nil || session == nil || worlds == nil || config.CallTimeout <= 0 || config.CallTimeout > time.Minute || config.JournalTimeout <= 0 || config.JournalTimeout > time.Minute {
		return nil, ErrControl
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

func (p *Player) enter(ctx context.Context, manual bool) (context.Context, context.Context, func(), error) {
	p.mu.Lock()
	if p.closing || p.closed || p.lifetime.Err() != nil {
		p.mu.Unlock()
		return nil, nil, nil, ErrControl
	}
	if manual {
		p.cancelEpoch()
		p.epoch, p.cancelEpoch = context.WithCancel(p.lifetime)
		// Local safety comes before a potentially stale request, queue or database.
		if err := p.session.Disable(); err != nil {
			p.mu.Unlock()
			return nil, nil, nil, err
		}
	}
	epoch := p.epoch
	p.mu.Unlock()
	call, cancel := context.WithTimeout(ctx, p.config.CallTimeout)
	stop := context.AfterFunc(epoch, cancel)
	cleanup := func() { stop(); cancel() }
	select {
	case p.gate <- struct{}{}:
	case <-call.Done():
		cleanup()
		return nil, nil, nil, call.Err()
	}
	if err := p.current(call, epoch); err != nil {
		<-p.gate
		cleanup()
		return nil, nil, nil, err
	}
	return call, epoch, func() { <-p.gate; cleanup() }, nil
}
func (p *Player) current(ctx, epoch context.Context) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.closing || p.closed || p.epoch != epoch || epoch.Err() != nil {
		return ErrControl
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
	call, epoch, done, err := p.enter(ctx, false)
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
func (p *Player) Acquire(ctx context.Context, request store.ControlRequest) (store.ControlRecord, error) {
	call, epoch, done, err := p.enter(ctx, false)
	if err != nil {
		return store.ControlRecord{}, err
	}
	defer done()
	if request.Kind != store.AcquireControl {
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
	state := p.session.State()
	if state.ObservationKnown && playerWorld(state.Snapshot) == request.World {
		if err = p.session.Manual(call); err != nil {
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
	snapshot := domain.GenerationSnapshot{Colony: request.World.Colony, Load: request.World.Load, Map: request.World.Map, Plan: request.Plan, Revision: request.Revision, Direction: record.Direction}
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
		return p.uncertain(record, ErrControl)
	}
	return p.finish(record, store.GrantedControl, granted.Native, nil)
}
func playerWorld(snapshot domain.GenerationSnapshot) store.World {
	return store.World{Colony: snapshot.Colony, Load: snapshot.Load, Map: snapshot.Map}
}

// Manual always stops local writes first, including on historical replay or a
// stale browser world. Only fresh matching scope permits native cleanup.
func (p *Player) Manual(ctx context.Context, request store.ControlRequest) (store.ControlRecord, error) {
	call, epoch, done, err := p.enter(ctx, true)
	if err != nil {
		return store.ControlRecord{}, err
	}
	defer done()
	if _, err = p.stopRoutine(call); err != nil {
		return store.ControlRecord{}, err
	}
	if request.Kind != store.ManualControl {
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
	state := p.session.State()
	if state.ObservationKnown && playerWorld(state.Snapshot) == request.World {
		if err = p.session.Manual(call); err != nil {
			return p.uncertain(record, err)
		}
	}
	if err = p.current(call, epoch); err != nil {
		return p.uncertain(record, err)
	}
	return p.finish(record, store.DisabledControl, 0, nil)
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
