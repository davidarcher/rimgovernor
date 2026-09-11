package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"google.golang.org/protobuf/proto"
	"path/filepath"
	"sync"
	"testing"
	"time"
)

func draftSessionFixture(t *testing.T, unknown bool) (*Session, *draftFixtureNative, *store.Store, string) {
	t.Helper()
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, filepath.Join(dir, "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = journal.Close() })
	_, native := draftBoundaryFixture(t)
	namespace, err := journal.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	native.receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
	native.receipt.AuthorizingOwner.ControllerSessionId = proto.String(string(namespace))
	native.row.DraftClaim.GetOwned().Owner.ControllerSessionId = proto.String(string(namespace))
	native.progress.Attempt.ControllerSessionId = proto.String(string(namespace))
	draftReceiptJob(native.receipt).DraftOwner = proto.String(string(namespace))
	native.progress.GetCompleted().GetEvidence().GetJob().DraftOwner = proto.String(string(namespace))
	plan, err := domain.NewPlan("plan", 1, []domain.Action{native.p.Action})
	if err != nil {
		t.Fatal(err)
	}
	if err = journal.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.PrepareDraft(ctx, plan.ID(), native.p.Action.ID(), store.DraftAdmission{Snapshot: native.p.Snapshot, Tick: 10, Pawn: "pawn", PawnSnapshotToken: "cas"}); err != nil {
		t.Fatal(err)
	}
	if _, err = journal.Dispatch(ctx, plan.ID(), native.p.Action.ID(), native.p.Snapshot, 10); err != nil {
		t.Fatal(err)
	}
	claim := draftKnownClaim(native)
	claim.Session = domain.ControllerSessionID(namespace)
	if unknown {
		_, err = journal.RecordDraftReceipt(ctx, plan.ID(), native.p.Action.ID(), 1, domain.ReceiptUnknown, domain.Unknown[domain.DraftClaim]())
	} else {
		_, err = journal.RecordDraftReceipt(ctx, plan.ID(), native.p.Action.ID(), 1, domain.ReceiptAccepted, domain.Known(claim))
		if err == nil {
			_, err = journal.ObserveDraft(ctx, plan.ID(), domain.Observation{Action: native.p.Action.ID(), Attempt: 1, Snapshot: native.p.Snapshot, Tick: 10, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, native.p.Snapshot, domain.Known(claim))
		}
	}
	if err != nil {
		t.Fatal(err)
	}
	_, building := newBoundaryFixture(t)
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}, Draft: &DraftCapabilities{Native: native, Writer: native, Cleanup: native}}
	session, err := NewSession(ctx, config, journal, sessionNative{building}, &controlNative{generation: 2}, sessionNative{building}, boundaryClock{})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { native.readErr = nil; native.releaseErr = nil; _ = session.Close(context.Background()) })
	if session.control.config.Worlds == nil {
		t.Fatal("missing independent shutdown world source")
	}
	return session, native, journal, dir
}

func TestDraftSessionCloseDrainsTerminalAndLostReceipt(t *testing.T) {
	for _, unknown := range []bool{false, true} {
		t.Run(map[bool]string{false: "terminal", true: "lost receipt"}[unknown], func(t *testing.T) {
			session, native, journal, dir := draftSessionFixture(t, unknown)
			if err := session.Close(context.Background()); err != nil {
				t.Fatal(err)
			}
			if native.releases != 1 || native.writes != 0 {
				t.Fatal(native.releases, native.writes)
			}
			state, err := journal.LoadPlan(context.Background(), "plan")
			if err != nil {
				t.Fatal(err)
			}
			cleanup, _ := state.Progress[0].View().DraftCleanup.Value()
			if cleanup.Stage != domain.DraftReleased {
				t.Fatal(cleanup)
			}
			owner, err := runtimeowner.Acquire(context.Background(), dir)
			if err != nil {
				t.Fatal(err)
			}
			_ = owner.Close()
		})
	}
}
func TestDraftSessionUncertainCloseRetainsOwnerAndRetriesOnlyOnNextCall(t *testing.T) {
	session, native, journal, dir := draftSessionFixture(t, false)
	native.releaseErr = context.DeadlineExceeded
	if err := session.Close(context.Background()); err == nil {
		t.Fatal("uncertainty closed")
	}
	if native.releases != 1 {
		t.Fatal("retried within sweep", native.releases)
	}
	if owner, err := runtimeowner.Acquire(context.Background(), dir); err == nil {
		_ = owner.Close()
		t.Fatal("released owner before cleanup")
	}
	state, _ := journal.LoadPlan(context.Background(), "plan")
	cleanup, _ := state.Progress[0].View().DraftCleanup.Value()
	if cleanup.Stage != domain.DraftCleanupUncertain {
		t.Fatal(cleanup)
	}
	native.releaseErr = nil
	if err := session.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	if native.releases != 2 {
		t.Fatal(native.releases)
	}
}
func TestDraftSessionManualDrainsWithoutPermanentlyStopping(t *testing.T) {
	session, native, _, _ := draftSessionFixture(t, true)
	if err := session.Manual(context.Background()); err != nil {
		t.Fatal(err)
	}
	if native.releases != 1 {
		t.Fatal(native.releases)
	}
	if _, err := session.Run(context.Background(), "plan", "action"); errors.Is(err, executor.ErrStopped) {
		t.Fatal("Manual permanently stopped executor")
	}
}
func TestDraftSessionRejectsPartialCapabilitiesBeforeOwnership(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	journal, err := store.Open(ctx, filepath.Join(dir, "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer journal.Close()
	_, f := newBoundaryFixture(t)
	native := sessionNative{f}
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}, Draft: &DraftCapabilities{}}
	if _, err = NewSession(ctx, config, journal, native, &controlNative{generation: 1}, native, boundaryClock{}); err == nil {
		t.Fatal("partial capabilities accepted")
	}
	owner, err := runtimeowner.Acquire(ctx, dir)
	if err != nil {
		t.Fatal(err)
	}
	_ = owner.Close()
}

func TestDraftSessionConcurrentSweepsDoNotRepeatRelease(t *testing.T) {
	session, native, _, _ := draftSessionFixture(t, false)
	var joined sync.WaitGroup
	errorsCh := make(chan error, 2)
	for range 2 {
		joined.Add(1)
		go func() { defer joined.Done(); errorsCh <- session.drafts.run(context.Background()) }()
	}
	joined.Wait()
	close(errorsCh)
	for err := range errorsCh {
		if err != nil {
			t.Fatal(err)
		}
	}
	if native.releases != 1 {
		t.Fatal(native.releases)
	}
}

func TestCancelledSessionAttachmentDoesNotStrandOwnerOnExistingDraft(t *testing.T) {
	session, native, journal, _ := draftSessionFixture(t, true)
	native.readErr = errors.New("native unavailable during construction")
	dir := t.TempDir()
	sink := &sessionSink{}
	control, err := NewControl(context.Background(), ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second, StopWrites: sink.stop}, journal, &controlNative{generation: 2}, sink)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err = sink.attach(ctx, session.executor, session.drafts, session.clock); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if err = control.Close(context.Background()); err != nil {
		t.Fatal(err)
	}
	owner, err := runtimeowner.Acquire(context.Background(), dir)
	if err != nil {
		t.Fatal("unreachable construction retained owner", err)
	}
	_ = owner.Close()
	state, err := journal.LoadPlan(context.Background(), "plan")
	if err != nil {
		t.Fatal(err)
	}
	cleanup, _ := state.Progress[0].View().DraftCleanup.Value()
	if cleanup.Stage != domain.DraftAwaitingClaim || native.identities != 0 || native.releases != 0 {
		t.Fatal("construction drained existing work", cleanup)
	}
}
