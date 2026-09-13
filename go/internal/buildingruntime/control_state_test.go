package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
)

func TestControlStateDisablePreservesReadScopeWithoutNativeCall(t *testing.T) {
	t.Parallel()
	control, native, sink, _ := controlFixture(t, nil)
	if got := control.State(); got != (ControlState{}) {
		t.Fatal(got)
	}
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	if got := control.State(); got != (ControlState{Snapshot: snapshot, ObservationKnown: true, Enabled: true}) {
		t.Fatal(got)
	}
	native.onRead = func(context.Context) error { t.Fatal("local operation called native read"); return nil }
	acquires, renews, revokes := native.acquires.Load(), native.renews.Load(), native.revokes.Load()
	for range 2 {
		if err = control.Disable(); err != nil {
			t.Fatal(err)
		}
		if got := control.State(); got != (ControlState{Snapshot: snapshot, ObservationKnown: true}) || sink.enabled() {
			t.Fatal(got)
		}
	}
	if native.acquires.Load() != acquires || native.renews.Load() != renews || native.revokes.Load() != revokes {
		t.Fatal("Disable performed native mutation")
	}
	if _, err = control.Lease(snapshot); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
	native.onRead = nil
}

func TestControlStateChecksExpiryAndHidesUnknownTarget(t *testing.T) {
	t.Parallel()
	control, native, sink, _ := controlFixture(t, nil)
	snapshot, err := control.Acquire(context.Background(), controlScope())
	if err != nil {
		t.Fatal(err)
	}
	control.mu.Lock()
	control.timer.Stop()
	control.deadline = time.Now().Add(-time.Second)
	control.mu.Unlock()
	state := control.State()
	if state.Enabled || !state.ObservationKnown || state.Snapshot != snapshot || sink.enabled() {
		t.Fatal(state)
	}
	native.onRead = func(context.Context) error { return errors.New("unavailable") }
	if err = control.Refresh(context.Background()); err == nil {
		t.Fatal("missing read failure")
	}
	if got := control.State(); got != (ControlState{}) {
		t.Fatal("private cleanup target leaked", got)
	}
	if control.snapshot == (domain.GenerationSnapshot{}) {
		t.Fatal("cleanup scope erased")
	}
	native.onRead = nil
}

func TestControlDisableInvalidatesBlockedAcquireGrant(t *testing.T) {
	t.Parallel()
	control, native, sink, _ := controlFixture(t, nil)
	entered, release := make(chan struct{}), make(chan struct{})
	native.onGrant = func(context.Context, string, *a.ControlReply) error { close(entered); <-release; return nil }
	done := make(chan error, 1)
	go func() { _, err := control.Acquire(context.Background(), controlScope()); done <- err }()
	<-entered
	if err := control.Disable(); err != nil {
		t.Fatal(err)
	}
	close(release)
	select {
	case err := <-done:
		if err == nil {
			t.Fatal("stale grant enabled control")
		}
	case <-time.After(time.Second):
		t.Fatal("acquire did not return")
	}
	if control.State().Enabled || sink.enabled() || native.acquires.Load() != 1 || native.renews.Load() != 0 || native.revokes.Load() != 0 {
		t.Fatal("disabled acquire resumed authority")
	}
}

func TestControlStateAndDisableAfterClose(t *testing.T) {
	t.Parallel()
	control, _, _, _ := controlFixture(t, nil)
	if err := control.ObserveTarget(context.Background(), controlScope()); err != nil {
		t.Fatal(err)
	}
	if err := control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := control.State(); got != (ControlState{}) {
		t.Fatal(got)
	}
	if err := control.Disable(); !errors.Is(err, ErrControl) {
		t.Fatal(err)
	}
}

type stateBlockedInspection struct{ entered chan struct{} }

func (b stateBlockedInspection) Inspect(ctx context.Context, _ executor.Target) (executor.Inspection, error) {
	close(b.entered)
	<-ctx.Done()
	return executor.Inspection{}, ctx.Err()
}
func (stateBlockedInspection) Place(context.Context, executor.Placement) (executor.Receipt, error) {
	panic("disabled inspection must never dispatch")
}
func (stateBlockedInspection) Observe(context.Context, executor.Placement, domain.GenerationSnapshot) (executor.Evidence, error) {
	panic("unissued action has no effect to observe")
}

func TestSessionDisableSynchronouslyInvalidatesBlockedRun(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, filepath.Join(dir, "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, fixture := boundary.NewFixture(t)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{fixture.Placement.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	blocked := stateBlockedInspection{entered: make(chan struct{})}
	worker, err := executor.New(journal, blocked, boundary.FixedClock{}, executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: time.Second})
	if err != nil {
		t.Fatal(err)
	}
	native := &controlNative{generation: 1}
	control, err := NewControl(ctx, ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second, StopWrites: worker.Stop}, journal, native, worker)
	if err != nil {
		t.Fatal(err)
	}
	session := &Session{control: control, executor: worker, journal: journal}
	defer session.Close(ctx)
	snapshot, err := session.Acquire(ctx, controlScope())
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { _, err := session.Run(ctx, "plan", "action"); done <- err }()
	<-blocked.entered
	if err = session.Disable(); err != nil {
		t.Fatal(err)
	}
	if got := session.State(); got != (ControlState{Snapshot: snapshot, ObservationKnown: true}) {
		t.Fatal(got)
	}
	select {
	case err = <-done:
		if err == nil {
			t.Fatal("disabled Run succeeded")
		}
	case <-time.After(time.Second):
		t.Fatal("disabled Run did not stop")
	}
	if _, err = session.Run(ctx, "plan", "action"); !errors.Is(err, executor.ErrAuthority) {
		t.Fatal(err)
	}
	state, err := journal.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Pending || state.Progress[0].View().Attempt != 0 {
		t.Fatal(state, err)
	}
	if native.acquires.Load() != 1 || native.renews.Load() != 0 || native.revokes.Load() != 0 {
		t.Fatal("Disable mutated native authority")
	}
}
