package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func newClockSessionTest(t *testing.T, db *store.Store, fake *clockCoreFake) (*Session, *controlNative, string) {
	t.Helper()
	_, fixture := boundary.NewFixture(t)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{fixture.Placement.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = db.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}, Clock: &ClockCapabilities{Native: fake, Writer: fake}}
	native := sessionNative{fixture}
	authority := &controlNative{generation: 6}
	s, err := NewSession(context.Background(), config, db, native, authority, native, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { fake.pause = nil; _ = s.Close(context.Background()) })
	return s, authority, dir
}

func TestClockSessionManualJoinsCancelledStartAndAllowsFreshAcquire(t *testing.T) {
	t.Parallel()
	_, db, fake, intent := clockCoreFixture(t)
	s, authority, _ := newClockSessionTest(t, db, fake)
	if _, err := s.CommandClock(context.Background(), intent); !errors.Is(err, executor.ErrAuthority) {
		t.Fatal(err)
	}
	current, err := s.Acquire(context.Background(), intent.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	intent.Snapshot = current
	entered := make(chan struct{})
	fake.write = func(ctx context.Context) { close(entered); <-ctx.Done() }
	finished := make(chan error, 1)
	go func() { _, err := s.CommandClock(context.Background(), intent); finished <- err }()
	select {
	case <-entered:
	case <-time.After(time.Second):
		t.Fatal("start did not dispatch")
	}
	if err = s.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err = <-finished; !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	epoch, err := db.LookupClockEpoch(context.Background(), intent.RequestID)
	if err != nil || epoch.Stage != store.ClockEpochPaused || fake.pauses != 1 || s.State().Enabled {
		t.Fatal(epoch.Stage, fake.pauses, err)
	}
	if _, err = s.Acquire(context.Background(), current); err != nil || !s.State().Enabled || authority.acquires.Load() != 2 {
		t.Fatal(err)
	}
}

func TestClockSessionCloseRetainsOwnerUntilPauseConfirmed(t *testing.T) {
	t.Parallel()
	_, db, fake, intent := clockCoreFixture(t)
	s, _, dir := newClockSessionTest(t, db, fake)
	current, err := s.Acquire(context.Background(), intent.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	intent.Snapshot = current
	if _, err = s.CommandClock(context.Background(), intent); err != nil {
		t.Fatal(err)
	}
	fake.pause = func(context.Context) error { return errors.New("pause reply unavailable") }
	if err = s.Close(context.Background()); err == nil {
		t.Fatal("failed pause released ownership")
	}
	if owner, err := AcquireProfile(context.Background(), dir); err == nil {
		owner.Close()
		t.Fatal("profile released before cleanup")
	}
	fake.pause = nil
	if err = s.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if fake.pauses != 2 {
		t.Fatal(fake.pauses)
	}
	owner, err := AcquireProfile(context.Background(), dir)
	if err != nil {
		t.Fatal(err)
	}
	_ = owner.Close()
}

func TestClockSessionRestartRecoversWithoutAcquiringPermission(t *testing.T) {
	t.Parallel()
	q, db, fake, intent := clockCoreFixture(t)
	if err := q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true}); err != nil {
		t.Fatal(err)
	}
	if _, err := q.Command(context.Background(), intent); err != nil {
		t.Fatal(err)
	}
	if err := q.Stop(context.Background()); err != nil {
		t.Fatal(err)
	}
	if err := requireNoClockObligations(context.Background(), db); err == nil {
		t.Fatal("missing cleanup capability accepted")
	}
	s, authority, _ := newClockSessionTest(t, db, fake)
	if s.State().Enabled || fake.pauses != 0 {
		t.Fatal("construction restored authority or performed cleanup")
	}
	if err := s.CleanupClock(context.Background()); err != nil {
		t.Fatal(err)
	}
	if fake.pauses != 1 || fake.writes != 1 || authority.acquires.Load() != 0 || s.State().Enabled {
		t.Fatal("recovery acquired permission")
	}
	if err := requireNoClockObligations(context.Background(), db); err != nil {
		t.Fatal(err)
	}
}

func TestControlCleanupFailurePreventsReacquire(t *testing.T) {
	t.Parallel()
	control, native, _, _ := controlFixture(t, nil)
	snapshot := domain.GenerationSnapshot{Colony: "colony", Map: 0, Load: "load", Plan: "plan", Revision: 1, Native: 1}
	if _, err := control.Acquire(context.Background(), snapshot); err != nil {
		t.Fatal(err)
	}
	control.config.CleanupWrites = func(context.Context) error { return executor.ErrHeld }
	if err := control.Manual(context.Background()); !errors.Is(err, executor.ErrHeld) {
		t.Fatal(err)
	}
	if _, err := control.Acquire(context.Background(), snapshot); !errors.Is(err, executor.ErrHeld) || native.acquires.Load() != 1 {
		t.Fatal(err)
	}
	control.config.CleanupWrites = func(context.Context) error { return nil }
	if _, err := control.Acquire(context.Background(), snapshot); err != nil || native.acquires.Load() != 2 {
		t.Fatal(err)
	}
}
