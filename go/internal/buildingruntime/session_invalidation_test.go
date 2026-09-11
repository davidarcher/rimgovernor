package buildingruntime

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type blockedSessionObservation struct {
	sessionNative
	entered chan struct{}
	block   bool
}

func (n *blockedSessionObservation) ObserveBuildingProgress(ctx context.Context, receipt *r.Receipt, candidate *p.PlacementCandidate) (*r.ProgressReply, bridge.Result, error) {
	if n.block {
		close(n.entered)
		<-ctx.Done()
		return nil, bridge.Result{}, ctx.Err()
	}
	return n.sessionNative.ObserveBuildingProgress(ctx, receipt, candidate)
}

func TestSessionFailedRefreshCancelsDisabledReconciliationUntilFreshObservation(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, filepath.Join(dir, "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, fixture := newBoundaryFixture(t)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{fixture.placement.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := fixture.placement.Snapshot
	building, _ := fixture.placement.Action.Building()
	admission := store.Admission{Snapshot: snapshot, Tick: 10, Costs: []store.MaterialCost{{Definition: "WoodLog", Count: 1}}, Footprint: []domain.Cell{building.Cell()}}
	if _, err = journal.ReserveAndPrepare(ctx, "plan", "action", admission); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, "plan", "action", snapshot, 10); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.RecordReceipt(ctx, "plan", "action", 1, domain.ReceiptUnknown); err != nil {
		t.Fatal(err)
	}
	namespace, err := journal.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	fixture.receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
	fixture.receipt.AuthorizingOwner.ControllerSessionId = proto.String(string(namespace))
	fixture.progress.Attempt = proto.Clone(fixture.receipt).(*r.Receipt).Attempt
	native := &blockedSessionObservation{sessionNative: sessionNative{fixture}, entered: make(chan struct{}), block: true}
	authority := &controlNative{generation: 1}
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: 5 * time.Second, JournalTimeout: time.Second}}
	session, err := NewSession(ctx, config, journal, native, authority, native, boundaryClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer session.Close(ctx)
	if err = session.ObserveTarget(ctx, snapshot); err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { _, err := session.Run(ctx, "plan", "action"); done <- err }()
	select {
	case <-native.entered:
	case <-time.After(time.Second):
		t.Fatal("reconciliation did not begin")
	}
	readFailure := errors.New("native identity unavailable after replacement")
	authority.onRead = func(context.Context) error { return readFailure }
	if err = session.Refresh(ctx); !errors.Is(err, readFailure) {
		t.Fatal(err)
	}
	select {
	case err = <-done:
		if err == nil {
			t.Fatal("failed refresh did not interrupt reconciliation")
		}
	case <-time.After(time.Second):
		t.Fatal("disabled reconciliation retained stale target")
	}
	before := fixture.lookups
	if _, err = session.Run(ctx, "plan", "action"); !errors.Is(err, executor.ErrAuthority) || fixture.lookups != before {
		t.Fatal("new stale observation admitted", err, fixture.lookups)
	}
	// A later ordinary invalidation must not republish the private cleanup target.
	if err = session.Manual(ctx); !errors.Is(err, readFailure) {
		t.Fatal(err)
	}
	if _, err = session.Run(ctx, "plan", "action"); !errors.Is(err, executor.ErrAuthority) || fixture.lookups != before {
		t.Fatal("manual resurrected stale target", err)
	}
	state, err := journal.LoadPlan(ctx, "plan")
	if err != nil || !state.Progress[0].View().Unresolved {
		t.Fatal("lost uncertainty", state, err)
	}
	authority.onRead = nil
	native.block = false
	if err = session.Refresh(ctx); err != nil {
		t.Fatal("fresh retry lost cleanup target", err)
	}
	result, err := session.Run(ctx, "plan", "action")
	if err != nil || result.Progress.View().Stage != domain.Completed || result.NativeCalled || fixture.places != 0 {
		t.Fatal(result, err)
	}
	if authority.acquires.Load() != 0 || authority.renews.Load() != 0 {
		t.Fatal("observation acquired authority")
	}
}
