package buildingruntime

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"google.golang.org/protobuf/proto"
)

func TestSessionOtherStoredPlanHoldSurvivesManualAndRestart(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	path := filepath.Join(dir, "plans.sqlite")
	journal, err := store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { journal.Close() }()
	_, fixture := newBoundaryFixture(t)
	planA, err := domain.NewPlan("plan", 1, []domain.Action{fixture.placement.Action})
	if err != nil {
		t.Fatal(err)
	}
	buildingB, err := domain.NewBuilding("Wall", domain.Cell{X: 5, Z: 6}, domain.North, "WoodLog")
	if err != nil {
		t.Fatal(err)
	}
	actionB, err := domain.NewBuildingAction("action-b", buildingB)
	if err != nil {
		t.Fatal(err)
	}
	planB, err := domain.NewPlan("plan-b", 1, []domain.Action{actionB})
	if err != nil {
		t.Fatal(err)
	}
	for _, plan := range []domain.PlanSpec{planA, planB} {
		if err = journal.CreatePlan(ctx, plan); err != nil {
			t.Fatal(err)
		}
	}
	native := sessionNative{fixture}
	authority := &controlNative{generation: 1}
	config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}}
	session, err := NewSession(ctx, config, journal, native, authority, native, boundaryClock{})
	if err != nil {
		t.Fatal(err)
	}
	defer func() { session.Close(ctx) }()
	currentA, err := session.Acquire(ctx, fixture.placement.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	// A competing session cannot own the same profile while this lease is live.
	competing, err := NewSession(ctx, config, journal, native, authority, native, boundaryClock{})
	if err == nil {
		competing.Close(ctx)
		t.Fatal("concurrent runtime ownership allowed")
	}
	namespace, err := journal.Identity(ctx)
	if err != nil {
		t.Fatal(err)
	}
	setPreview := func(action domain.Action, snapshot domain.GenerationSnapshot, cost int64) {
		building, _ := action.Building()
		fixture.preview.Preview.Action = action
		fixture.preview.Preview.Snapshot = snapshot
		fixture.preview.Stock.Snapshot = snapshot
		fixture.preview.Preview.CanPlace = domain.Known(true)
		fixture.preview.Preview.SafeToPlace = domain.Known(true)
		fixture.preview.Preview.MadeFromStuff = domain.Known(true)
		fixture.preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: cost}})
		fixture.preview.Preview.Footprint = domain.Known([]domain.Cell{building.Cell()})
		fixture.preview.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(5))}}
		fixture.bounds.Context.NativeGeneration = proto.Uint64(uint64(snapshot.Native))
	}
	setPreview(fixture.placement.Action, currentA, 4)
	fixture.receipt.AdmittedContext.NativeGeneration = proto.Uint64(uint64(currentA.Native))
	fixture.receipt.AdmittedContext.Tick = proto.Int64(11)
	fixture.receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
	fixture.receipt.AuthorizingOwner.ControllerSessionId = proto.String(string(namespace))
	result, err := session.Run(ctx, planA.ID(), fixture.placement.Action.ID())
	if err != nil || !result.NativeCalled || !result.Progress.View().Unresolved || fixture.places != 1 {
		t.Fatalf("A dispatch: %+v %v", result, err)
	}
	if err = session.Manual(ctx); err != nil {
		t.Fatal(err)
	}
	authority.mu.Lock()
	inactive := authority.owner == nil
	authority.mu.Unlock()
	if !inactive || authority.revokes.Load() != 1 {
		t.Fatal("Manual retained native authority")
	}
	if err = session.Close(ctx); err != nil {
		t.Fatal(err)
	}
	if err = journal.Close(); err != nil {
		t.Fatal(err)
	}
	journal, err = store.Open(ctx, path)
	if err != nil {
		t.Fatal(err)
	}
	restoredNamespace, err := journal.Identity(ctx)
	if err != nil || restoredNamespace != namespace {
		t.Fatal("restart changed journal namespace", err)
	}
	session, err = NewSession(ctx, config, journal, native, authority, native, boundaryClock{})
	if err != nil {
		t.Fatal(err)
	}
	if authority.acquires.Load() != 1 {
		t.Fatal("restart implicitly acquired")
	}
	requestedB := currentA
	requestedB.Plan = planB.ID()
	requestedB.Direction++
	currentB, err := session.Acquire(ctx, requestedB)
	if err != nil {
		t.Fatal(err)
	}
	setPreview(actionB, currentB, 2)
	result, err = session.Run(ctx, planB.ID(), actionB.ID())
	if !errors.Is(err, executor.ErrHeld) || result.NativeCalled || fixture.places != 1 {
		t.Fatalf("B overspent durable A hold: %+v %v", result, err)
	}
	if len(result.Refused) != 1 || result.Refused[0].Reason != policy.InsufficientStock {
		t.Fatalf("wrong refusal: %+v", result)
	}
	if authority.acquires.Load() != 2 || authority.revokes.Load() != 1 {
		t.Fatal("unexpected authority sequence")
	}
	// No lookup or observation released A's uncertain effect during B admission.
	if fixture.lookups != 0 || fixture.observes != 0 {
		t.Fatal("other plan was implicitly reconciled")
	}
	stateA, err := journal.LoadPlan(ctx, planA.ID())
	if err != nil {
		t.Fatal(err)
	}
	receipt, known := stateA.Progress[0].View().Receipt.Value()
	if !known || receipt != domain.ReceiptUnknown {
		t.Fatal("uncertain receipt was not durable")
	}
	stateB, err := journal.LoadPlan(ctx, planB.ID())
	if err != nil {
		t.Fatal(err)
	}
	if stateB.Progress[0].View().Attempt != 0 || len(stateB.Admissions) != 0 {
		t.Fatal("refused B recorded dispatch or admission")
	}
	if !stateA.Progress[0].View().Unresolved {
		t.Fatal("A uncertainty lost after restart")
	}
}
