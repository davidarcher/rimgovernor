package buildingruntime

import (
	"context"
	"database/sql"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

type playerWorldSource struct {
	mu    sync.Mutex
	world store.World
	err   error
	calls int
}
type playerWorldFunc func(context.Context) (store.World, error)

func (f playerWorldFunc) ReadWorld(ctx context.Context) (store.World, error) { return f(ctx) }

func (w *playerWorldSource) ReadWorld(ctx context.Context) (store.World, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	w.calls++
	if ctx.Err() != nil {
		return store.World{}, ctx.Err()
	}
	return w.world, w.err
}

type playerFakeSession struct {
	rules                               []policy.ResourceRule
	mu                                  sync.Mutex
	state                               ControlState
	acquires, manuals, disables, closes atomic.Int32
	acquire                             func(context.Context, domain.GenerationSnapshot) (domain.GenerationSnapshot, error)
	close                               func(context.Context) error
}

func (s *playerFakeSession) State() ControlState { s.mu.Lock(); defer s.mu.Unlock(); return s.state }
func (s *playerFakeSession) Disable() error {
	s.disables.Add(1)
	s.mu.Lock()
	s.state.Enabled = false
	s.mu.Unlock()
	return nil
}
func (s *playerFakeSession) Acquire(ctx context.Context, requested domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
	s.acquires.Add(1)
	var err error
	if s.acquire != nil {
		requested, err = s.acquire(ctx, requested)
	} else {
		requested.Native = 1
	}
	if err == nil {
		s.mu.Lock()
		s.state = ControlState{Snapshot: requested, ObservationKnown: true, Enabled: true}
		s.mu.Unlock()
	}
	return requested, err
}
func (s *playerFakeSession) Manual(context.Context) error { s.manuals.Add(1); return s.Disable() }
func (s *playerFakeSession) Close(ctx context.Context) error {
	s.closes.Add(1)
	if s.close != nil {
		return s.close(ctx)
	}
	return s.Disable()
}
func playerFixture(t *testing.T) (*Player, *store.Store, *playerFakeSession, *playerWorldSource) {
	t.Helper()
	db, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	session := &playerFakeSession{}
	worlds := &playerWorldSource{world: store.World{Colony: "colony", Load: "load", Map: 0}}
	p, err := newPlayer(context.Background(), PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, worlds)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close(context.Background()); db.Close() })
	return p, db, session, worlds
}
func playerSubmission() store.SubmissionRequest {
	b, _ := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 2}, domain.North, "WoodLog")
	return store.SubmissionRequest{RequestID: "submit", World: store.World{Colony: "colony", Load: "load", Map: 0}, Building: b}
}

// playerAcquire submits one building as player guidance and returns the
// Resume request that enables autonomous play for its world. The submitted
// plan is dispatched under the root plan's authority (see playerPlan).
func playerAcquire(t *testing.T, p *Player) store.ControlRequest {
	t.Helper()
	s, created, err := p.Submit(context.Background(), playerSubmission())
	if err != nil || !created {
		t.Fatal(s, created, err)
	}
	return store.ControlRequest{RequestID: "acquire", Kind: store.ResumeControl, World: s.Request.World}
}

// playerPlan returns the submitted guidance plan created by playerAcquire.
func playerPlan(t *testing.T, db *store.Store) store.PlanState {
	t.Helper()
	return submittedPlan(t, db, playerSubmission().RequestID)
}

// submittedPlan loads the plan behind one building submission: the player's
// guidance plan, which dispatches under the root plan's authority.
func submittedPlan(t *testing.T, db *store.Store, requestID string) store.PlanState {
	t.Helper()
	s, err := db.LookupSubmission(context.Background(), requestID)
	if err != nil {
		t.Fatal(err)
	}
	plan, err := db.LoadPlan(context.Background(), s.Plan)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

func TestPlayerExactReplayAndChangedRequestConflict(t *testing.T) {
	t.Parallel()
	p, _, s, worlds := playerFixture(t)
	q := playerAcquire(t, p)
	record, err := p.Resume(context.Background(), q)
	if err != nil || record.Phase != store.RunningControl {
		t.Fatal(record, err)
	}
	worlds.err = errors.New("native now unavailable")
	before := s.disables.Load()
	replay, err := p.Resume(context.Background(), q)
	if err != nil || replay != record || s.acquires.Load() != 1 || s.disables.Load() != before {
		t.Fatal(replay, err)
	}
	changed := q
	changed.World.Map++
	if _, err = p.Resume(context.Background(), changed); !errors.Is(err, store.ErrConflict) || s.acquires.Load() != 1 {
		t.Fatal(err)
	}
	submission, created, err := p.Submit(context.Background(), playerSubmission())
	if err != nil || created || submission.Plan != playerPlan(t, p.journal).Spec.ID() {
		t.Fatal(submission, created, err)
	}
}

func TestPlayerManualStaleWorldStopsLocallyAndReplayDoesNotCleanAgain(t *testing.T) {
	t.Parallel()
	p, _, s, worlds := playerFixture(t)
	q := playerAcquire(t, p)
	if _, err := p.Resume(context.Background(), q); err != nil {
		t.Fatal(err)
	}
	worlds.world.Load = "replacement"
	manual := store.ControlRequest{RequestID: "manual-stale", Kind: store.PauseControl, World: q.World}
	record, err := p.Pause(context.Background(), manual)
	if !errors.Is(err, store.ErrConflict) || record.Phase != store.RefusedControl || p.State().Enabled || s.manuals.Load() != 0 {
		t.Fatal(record, err, p.State())
	}
	before := s.disables.Load()
	replay, err := p.Pause(context.Background(), manual)
	if err != nil || replay != record || s.disables.Load() != before+1 || s.manuals.Load() != 0 {
		t.Fatal(replay, err)
	}
	changed := manual
	changed.World = worlds.world
	if _, err = p.Pause(context.Background(), changed); !errors.Is(err, store.ErrConflict) {
		t.Fatal(err)
	}
}

func TestPlayerHistoricalReplayCannotChangeControlMethod(t *testing.T) {
	t.Parallel()
	p, _, session, _ := playerFixture(t)
	acquire := playerAcquire(t, p)
	if _, err := p.Resume(context.Background(), acquire); err != nil {
		t.Fatal(err)
	}
	if _, err := p.Pause(context.Background(), acquire); !errors.Is(err, store.ErrConflict) || p.State().Enabled || session.manuals.Load() != 0 {
		t.Fatal("Acquire replay accepted by Manual", err)
	}
	manual := store.ControlRequest{RequestID: "manual", Kind: store.PauseControl, World: acquire.World}
	if _, err := p.Pause(context.Background(), manual); err != nil {
		t.Fatal(err)
	}
	before := session.disables.Load()
	if _, err := p.Resume(context.Background(), manual); !errors.Is(err, store.ErrConflict) || session.acquires.Load() != 1 || session.disables.Load() != before {
		t.Fatal("Manual replay accepted by Acquire", err)
	}
}

// Done is observed after enter captured its epoch, providing a deterministic
// barrier for a request queued behind the in-flight native control call.
type queuedPlayerContext struct {
	context.Context
	entered chan struct{}
	once    sync.Once
	done    chan struct{}
}

func (c *queuedPlayerContext) Done() <-chan struct{} {
	c.once.Do(func() { close(c.entered) })
	return c.done
}
func TestPlayerManualPreemptsActiveAndQueuedAcquire(t *testing.T) {
	t.Parallel()
	p, db, s, _ := playerFixture(t)
	q := playerAcquire(t, p)
	entered, release := make(chan struct{}), make(chan struct{})
	s.acquire = func(_ context.Context, snapshot domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
		close(entered)
		<-release
		snapshot.Native = 7
		return snapshot, nil
	}
	active := make(chan error, 1)
	go func() { _, err := p.Resume(context.Background(), q); active <- err }()
	<-entered
	queuedCtx := &queuedPlayerContext{Context: context.Background(), entered: make(chan struct{}), done: make(chan struct{})}
	queuedRequest := q
	queuedRequest.RequestID = "queued"
	queued := make(chan error, 1)
	go func() { _, err := p.Resume(queuedCtx, queuedRequest); queued <- err }()
	<-queuedCtx.entered
	manual := make(chan error, 1)
	before := s.disables.Load()
	go func() {
		_, err := p.Pause(context.Background(), store.ControlRequest{RequestID: "manual", Kind: store.PauseControl, World: q.World})
		manual <- err
	}()
	// Observe synchronous local disable before allowing the deliberately late grant.
	deadline := time.After(time.Second)
	for s.disables.Load() == before {
		select {
		case <-deadline:
			t.Fatal("Manual did not disable before waiting")
		default:
			time.Sleep(time.Millisecond)
		}
	}
	close(release)
	for name, done := range map[string]<-chan error{"active": active, "queued": queued} {
		select {
		case err := <-done:
			if err == nil {
				t.Fatal(name, "survived Manual")
			}
		case <-time.After(time.Second):
			t.Fatal(name, "did not drain")
		}
	}
	select {
	case err := <-manual:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(time.Second):
		t.Fatal("Manual did not drain")
	}
	record, err := db.LookupControl(context.Background(), q.RequestID)
	if err != nil || record.Phase != store.UncertainControl || p.State().Enabled || s.acquires.Load() != 1 {
		t.Fatal(record, err, p.State())
	}
	if _, err = db.LookupControl(context.Background(), queuedRequest.RequestID); !errors.Is(err, store.ErrNotFound) {
		t.Fatal(err)
	}
}

func TestPlayerDroppedContextFinishesAdmittedControl(t *testing.T) {
	t.Parallel()
	p, db, s, _ := playerFixture(t)
	q := playerAcquire(t, p)
	ctx, cancel := context.WithCancel(context.Background())
	s.acquire = func(context.Context, domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
		cancel()
		return domain.GenerationSnapshot{}, context.Canceled
	}
	record, err := p.Resume(ctx, q)
	if !errors.Is(err, context.Canceled) || record.Phase != store.UncertainControl || p.State().Enabled {
		t.Fatal(record, err)
	}
	persisted, err := db.LookupControl(context.Background(), q.RequestID)
	if err != nil || persisted != record {
		t.Fatal(persisted, err)
	}
}

func TestPlayerWorldReplacementAfterIntentPreventsAcquire(t *testing.T) {
	t.Parallel()
	p, db, session, _ := playerFixture(t)
	q := playerAcquire(t, p)
	reads := 0
	p.worlds = playerWorldFunc(func(context.Context) (store.World, error) {
		reads++
		world := q.World
		if reads == 2 {
			world.Load = "replacement"
		}
		return world, nil
	})
	record, err := p.Resume(context.Background(), q)
	if !errors.Is(err, store.ErrConflict) || record.Phase != store.UncertainControl || session.acquires.Load() != 0 || session.manuals.Load() != 0 {
		t.Fatal(record, err)
	}
	persisted, err := db.LookupControl(context.Background(), q.RequestID)
	if err != nil || persisted != record {
		t.Fatal(persisted, err)
	}
}

func TestPlayerRestartHistoricalPendingAndGrantedNeverEnable(t *testing.T) {
	t.Parallel()
	for _, phase := range []store.ControlPhase{store.PendingControl, store.RunningControl} {
		t.Run(string(phase), func(t *testing.T) {
			ctx := context.Background()
			path := filepath.Join(t.TempDir(), "state.sqlite")
			db, err := store.Open(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			submission, _, err := db.SubmitBuilding(ctx, playerSubmission())
			if err != nil {
				t.Fatal(err)
			}
			q := store.ControlRequest{RequestID: "request", Kind: store.ResumeControl, World: submission.Request.World}
			record, _, err := db.BeginControl(ctx, q)
			if err != nil {
				t.Fatal(err)
			}
			if phase == store.RunningControl {
				record, err = db.CompleteControl(ctx, q.RequestID, phase, 3)
				if err != nil {
					t.Fatal(err)
				}
			}
			if err = db.Close(); err != nil {
				t.Fatal(err)
			}
			db, err = store.Open(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			defer db.Close()
			session := &playerFakeSession{}
			worlds := &playerWorldSource{err: errors.New("no native read allowed")}
			p, err := newPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, worlds)
			if err != nil {
				t.Fatal(err)
			}
			defer p.Close(ctx)
			replay, err := p.Resume(ctx, q)
			if err != nil || replay != record || p.State().Enabled || session.acquires.Load() != 0 || worlds.calls != 0 {
				t.Fatal(replay, err)
			}
		})
	}
}

func TestPlayerActualSessionCleansPriorOwnedLeaseAndRejectsForeignOwner(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	db, err := store.Open(ctx, filepath.Join(dir, "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	_, native := boundary.NewFixture(t)
	authority := &controlNative{generation: 1}
	session, err := NewSession(ctx, SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}}, db, native, authority, native, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	worlds := &playerWorldSource{world: playerSubmission().World}
	p, err := NewPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, worlds)
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close(ctx)
	q := playerAcquire(t, p)
	_, err = p.Resume(ctx, q)
	if err != nil {
		t.Fatal(err)
	}
	second := q
	second.RequestID = "second"
	got, err := p.Resume(ctx, second)
	if err != nil || got.Phase != store.RunningControl || authority.acquires.Load() != 2 || authority.revokes.Load() != 1 || !p.State().Enabled {
		t.Fatal(got, err)
	}
	// Simulate the native side reporting Auto authority whose generation counter
	// is exhausted: the bot must refuse to touch it rather than revoke or adopt it.
	authority.mu.Lock()
	authority.active = true
	authority.generation = ^uint64(0)
	authority.mu.Unlock()
	third := q
	third.RequestID = "third"
	got, err = p.Resume(ctx, third)
	if err == nil || got.Phase != store.UncertainControl || authority.revokes.Load() != 1 || authority.acquires.Load() != 2 || p.State().Enabled {
		t.Fatal(got, err)
	}
	if err := p.Close(ctx); err == nil {
		t.Fatal("foreign authority was treated as confirmed shutdown")
	}
	requireProfileHeld(t, dir)
	// The foreign owner independently leaves; shutdown can now observe inactivity.
	authority.mu.Lock()
	authority.active = false
	authority.generation = 2
	authority.mu.Unlock()
	if err := p.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if authority.revokes.Load() != 1 || authority.acquires.Load() != 2 {
		t.Fatal("shutdown changed foreign authority")
	}
}

func TestPlayerCloseDrainsBeforeSessionCloseAndRetriesFailure(t *testing.T) {
	t.Parallel()
	p, _, s, _ := playerFixture(t)
	q := playerAcquire(t, p)
	entered, release := make(chan struct{}), make(chan struct{})
	s.acquire = func(context.Context, domain.GenerationSnapshot) (domain.GenerationSnapshot, error) {
		close(entered)
		<-release
		return domain.GenerationSnapshot{}, errors.New("late failure")
	}
	active := make(chan error, 1)
	go func() { _, err := p.Resume(context.Background(), q); active <- err }()
	<-entered
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if err := p.Close(ctx); !errors.Is(err, context.DeadlineExceeded) || s.closes.Load() != 0 {
		t.Fatal(err)
	}
	if _, _, err := p.Submit(context.Background(), playerSubmission()); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
	close(release)
	<-active
	failure := errors.New("drain failed")
	s.close = func(context.Context) error { return failure }
	if err := p.Close(context.Background()); !errors.Is(err, failure) {
		t.Fatal(err)
	}
	s.close = nil
	if err := p.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
}

func TestPlayerCompletionJournalFailureDisablesGrantedLease(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "state.sqlite")
	db, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	fault, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	defer fault.Close()
	if _, err = fault.ExecContext(ctx, `CREATE TRIGGER reject_control_completion BEFORE UPDATE ON control_intents BEGIN SELECT RAISE(ABORT,'test journal failure'); END`); err != nil {
		t.Fatal(err)
	}
	session := &playerFakeSession{}
	p, err := newPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, &playerWorldSource{world: playerSubmission().World})
	if err != nil {
		t.Fatal(err)
	}
	defer p.Close(ctx)
	q := playerAcquire(t, p)
	result, err := p.Resume(ctx, q)
	if err == nil || result.Phase != store.PendingControl || p.State().Enabled || session.acquires.Load() != 1 {
		t.Fatal(result, err)
	}
	replay, err := p.Resume(ctx, q)
	if err != nil || replay.Phase != store.PendingControl || session.acquires.Load() != 1 || p.State().Enabled {
		t.Fatal(replay, err)
	}
}
