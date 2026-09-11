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
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type sessionNative struct{ *boundaryFixture }

func (f sessionNative) LookupBuildingAttempt(_ context.Context, id *c.Identity, attempt *c.AttemptKey, generation uint64, _ *p.PlacementCandidate) (*r.LookupReply, bridge.Result, error) {
	f.lookups++
	if !proto.Equal(id, f.receipt.AdmittedContext.Identity) || !proto.Equal(attempt, f.receipt.Attempt) || generation != f.receipt.AdmittedContext.GetNativeGeneration() {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.receipt}}, bridge.Result{}, nil
}

func TestSessionOwnsDispatchAndManualReconciliation(t *testing.T) {
	for _, restart := range []bool{false, true} {
		t.Run(map[bool]string{false: "manual", true: "restart"}[restart], func(t *testing.T) {
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
			native := sessionNative{fixture}
			authority := &controlNative{generation: 1}
			config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, LeaseDuration: time.Second, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}}
			session, err := NewSession(ctx, config, journal, native, authority, native, boundaryClock{})
			if err != nil {
				t.Fatal(err)
			}
			defer session.Close(ctx)
			if owner, err := runtimeowner.Acquire(ctx, dir); err == nil {
				owner.Close()
				t.Fatal("session did not own profile")
			}
			requested := fixture.placement.Snapshot
			requested.Revision = 2
			if _, err = session.Acquire(ctx, requested); err == nil || authority.acquires.Load() != 0 {
				t.Fatal("stale plan acquired authority")
			}
			requested.Revision = 1
			current, err := session.Acquire(ctx, requested)
			if err != nil {
				t.Fatal(err)
			}
			namespace, err := journal.Identity(ctx)
			if err != nil {
				t.Fatal(err)
			}
			fixture.preview.Preview.Snapshot = current
			fixture.preview.Stock.Snapshot = current
			fixture.preview.Preview.CanPlace = domain.Known(true)
			fixture.preview.Preview.SafeToPlace = domain.Known(true)
			fixture.preview.Preview.MadeFromStuff = domain.Known(true)
			fixture.preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 1}})
			building, _ := fixture.placement.Action.Building()
			fixture.preview.Preview.Footprint = domain.Known([]domain.Cell{building.Cell()})
			fixture.preview.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(5))}}
			fixture.bounds.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
			fixture.receipt.AdmittedContext.NativeGeneration = proto.Uint64(uint64(current.Native))
			fixture.receipt.AdmittedContext.Tick = proto.Int64(11)
			fixture.receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
			fixture.receipt.AuthorizingOwner.ControllerSessionId = proto.String(string(namespace))
			fixture.progress.Attempt = proto.Clone(fixture.receipt.Attempt).(*c.AttemptKey)
			result, err := session.Run(ctx, "plan", "action")
			if err != nil || !result.NativeCalled || !result.Progress.View().Unresolved {
				t.Fatalf("dispatch %v %v", result, err)
			}
			if restart {
				if err = session.Close(ctx); err != nil {
					t.Fatal(err)
				}
				if err = journal.Close(); err != nil {
					t.Fatal(err)
				}
				journal, err = store.Open(ctx, filepath.Join(dir, "state.sqlite"))
				if err != nil {
					t.Fatal(err)
				}
				defer journal.Close()
				session, err = NewSession(ctx, config, journal, native, authority, native, boundaryClock{})
				if err != nil {
					t.Fatal(err)
				}
				defer session.Close(ctx)
				if err = session.ObserveTarget(ctx, current); err != nil {
					t.Fatal(err)
				}
				if authority.acquires.Load() != 1 {
					t.Fatal("restart reacquired authority")
				}
			} else if err = session.Manual(ctx); err != nil {
				t.Fatal(err)
			}
			fixture.progress.Context.NativeGeneration = proto.Uint64(uint64(current.Native) + 1)
			fixture.progress.Context.Tick = proto.Int64(12)
			result, err = session.Run(ctx, "plan", "action")
			if err != nil || result.NativeCalled || result.Progress.View().Stage != domain.Completed || fixture.places != 1 {
				t.Fatalf("manual reconciliation %v %v", result, err)
			}
			if err = session.Close(ctx); err != nil {
				t.Fatal(err)
			}
			if _, err = session.Run(ctx, "plan", "action"); !errors.Is(err, executor.ErrStopped) {
				t.Fatalf("closed runtime ran: %v", err)
			}
			owner, err := runtimeowner.Acquire(ctx, dir)
			if err != nil {
				t.Fatal("drained runtime retained lock", err)
			}
			owner.Close()
		})
	}
}
