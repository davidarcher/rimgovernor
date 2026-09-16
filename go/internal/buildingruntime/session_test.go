package buildingruntime

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	p "github.com/davidarcher/RimGovernor/go/internal/wire/placementpb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type sessionNative struct{ *boundary.Fixture }

func (f sessionNative) LookupBuildingAttempt(_ context.Context, id *c.Identity, attempt *c.AttemptKey, generation uint64, _ *p.PlacementCandidate) (*r.LookupReply, bridge.Result, error) {
	f.Lookups++
	if !proto.Equal(id, f.Receipt.AdmittedContext.Identity) || !proto.Equal(attempt, f.Receipt.Attempt) || generation != f.Receipt.AdmittedContext.GetNativeGeneration() {
		return nil, bridge.Result{}, executor.ErrEvidence
	}
	return &r.LookupReply{Outcome: &r.LookupReply_Receipt{Receipt: f.Receipt}}, bridge.Result{}, nil
}

func TestSessionOwnsDispatchAndManualReconciliation(t *testing.T) {
	t.Parallel()
	for _, restart := range []bool{false, true} {
		t.Run(map[bool]string{false: "manual", true: "restart"}[restart], func(t *testing.T) {
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
			native := sessionNative{fixture}
			authority := &controlNative{generation: 1}
			config := SessionConfig{Control: ControlConfig{ProfileDirectory: dir, CallTimeout: time.Second}, Executor: executor.Limits{MaxAge: time.Second, RunTimeout: time.Second, JournalTimeout: time.Second}}
			session, err := NewSession(ctx, config, journal, native, authority, native, boundary.FixedClock{})
			if err != nil {
				t.Fatal(err)
			}
			defer session.Close(ctx)
			if owner, err := AcquireProfile(ctx, dir); err == nil {
				owner.Close()
				t.Fatal("session did not own profile")
			}
			requested := fixture.Placement.Snapshot
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
			fixture.Preview.Preview.Snapshot = current
			fixture.Preview.Stock.Snapshot = current
			fixture.Preview.Preview.CanPlace = domain.Known(true)
			fixture.Preview.Preview.SafeToPlace = domain.Known(true)
			fixture.Preview.Preview.MadeFromStuff = domain.Known(true)
			fixture.Preview.Preview.Costs = domain.Known([]policy.Amount{{Resource: "WoodLog", Count: 1}})
			building, _ := fixture.Placement.Action.Building()
			fixture.Preview.Preview.Footprint = domain.Known([]domain.Cell{building.Cell()})
			fixture.Preview.Stock.Values = []policy.Stock{{Resource: "WoodLog", Available: domain.Known(int64(5))}}
			fixture.Bounds.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
			fixture.Emergency.Context.NativeGeneration = proto.Uint64(uint64(current.Native))
			fixture.Receipt.AdmittedContext.NativeGeneration = proto.Uint64(uint64(current.Native))
			fixture.Receipt.AdmittedContext.Tick = proto.Int64(11)
			fixture.Receipt.Attempt.ControllerSessionId = proto.String(string(namespace))
			fixture.Progress.Attempt = proto.Clone(fixture.Receipt.Attempt).(*c.AttemptKey)
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
				session, err = NewSession(ctx, config, journal, native, authority, native, boundary.FixedClock{})
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
			fixture.Progress.Context.NativeGeneration = proto.Uint64(uint64(current.Native) + 1)
			fixture.Progress.Context.Tick = proto.Int64(12)
			fixture.EmergencyErr = errors.New("emergency facts unavailable during reconciliation")
			fixture.Emergency.Facts.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
			emergencyCalls := fixture.Emergencies
			result, err = session.Run(ctx, "plan", "action")
			if fixture.Emergencies != emergencyCalls {
				t.Fatal("issued attempt reconciliation read emergency facts")
			}
			if err != nil || result.NativeCalled || result.Progress.View().Stage != domain.Completed || fixture.Places != 1 {
				t.Fatalf("manual reconciliation %v %v", result, err)
			}
			if err = session.Close(ctx); err != nil {
				t.Fatal(err)
			}
			if _, err = session.Run(ctx, "plan", "action"); !errors.Is(err, executor.ErrStopped) {
				t.Fatalf("closed runtime ran: %v", err)
			}
			owner, err := AcquireProfile(ctx, dir)
			if err != nil {
				t.Fatal("drained runtime retained lock", err)
			}
			owner.Close()
		})
	}
}
