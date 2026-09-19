package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// A committed Stopped event reaches the step loop as a pending stop, once,
// beside the ordinary wake evidence; a page without one leaves the reason
// clear. Nothing else waits on it: no admission needs the stop between
// windows (#244).
func TestWakeSignalCarriesStop(t *testing.T) {
	t.Parallel()
	page := &k.EventsPage{Events: []*k.Event{
		{Cursor: proto.Int64(1), Event: &k.Event_OperationOutcome{OperationOutcome: clockPollOutcome(1)}},
		{Cursor: proto.Int64(2), Event: &k.Event_Stopped{Stopped: &k.StopEvent{Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum()}}},
	}}
	outcomes, _, _, stopped, _ := clockPageWakeStopped(page)
	if !stopped || len(outcomes) != 1 {
		t.Fatal(stopped, outcomes)
	}
	if _, _, _, stopped, _ := clockPageWakeStopped(&k.EventsPage{Events: page.Events[:1]}); stopped {
		t.Fatal("no Stopped event on the page")
	}
	w := NewWakeSignal()
	w.NotifyStopped(outcomes, nil, false, true)
	if reason := w.TakeInvalidated(); !reason.Stopped || len(reason.Events) != 1 {
		t.Fatal(reason)
	}
	if reason := w.TakeInvalidated(); reason.Stopped {
		t.Fatal("stop is drained once")
	}
	w.NotifyInvalidated(nil, nil, true)
	if reason := w.TakeInvalidated(); reason.Stopped || !reason.Authority {
		t.Fatal("an invalidation is not a stop")
	}
	var none *WakeSignal
	if reason := none.TakeInvalidated(); reason.Stopped {
		t.Fatal("nil signal")
	}
}

// Every routine kind is validated natively at apply time and dispatches
// under a running window (#242, #243, #244); the pause-bound set is empty.
// A player order the Worker only reconciles is not a live dispatch.
func TestLiveDispatchKindCoversEveryRoutineKind(t *testing.T) {
	t.Parallel()
	for _, kind := range []domain.ActionKind{
		domain.BuildingAction, domain.HaulAction, domain.SupplyAllowAction, domain.WorkAssignmentAction, domain.ZoneCreateAction,
		domain.ProductionBillAction, domain.GrowerCropAction, domain.AcquisitionAction, domain.MineAcquisitionAction, domain.HusbandryAction,
		domain.ExcavationAction, domain.BedAssignAction, domain.WallRemovalAction, domain.ProductionPolicyAction, domain.ResearchSelectAction, domain.HomeCoverageAction,
	} {
		if !liveDispatchKind(kind) {
			t.Errorf("%s: not dispatched live", kind)
		}
	}
	if liveDispatchKind(domain.MeleeAttackAction) || liveDispatchKind(domain.OwnedDraftAction) {
		t.Fatal("a combat order is not a routine dispatch")
	}
}

// A dispatch held on stale_facts is retried at once, off its backoff,
// before the game's own work scanner takes the order's target (#288); the
// clock is not held for it, since every routine kind dispatches under the
// running window (#244). One retry per hold: a hold that survives it backs
// off, and the next hold after a dispatch is a new one.
func TestWorkerStaleHoldRetriesOnce(t *testing.T) {
	t.Parallel()
	w, f, db := workerFixture(t)
	v := workerPending(t, w, "stale", false)
	f.mu.Lock()
	f.state = ControlState{Snapshot: v.Snapshot, ObservationKnown: true, Enabled: true}
	f.mu.Unlock()
	for _, running := range []bool{false, true} {
		w.config.WindowRunning = func() bool { return running }
		w.waits = map[domain.ActionID]workerWait{}
		w.focus = nil
		f.runs.Store(0)
		held := true
		f.run = func(ctx context.Context, p domain.PlanID, a domain.ActionID) (executor.Result, error) {
			plan, err := db.LoadPlan(ctx, p)
			if err != nil {
				t.Fatal(err)
			}
			result := executor.Result{Progress: plan.Progress[0]}
			if held {
				result.Refused = []policy.Refusal{{Action: a, Reason: policy.StaleFacts}}
				return result, executor.ErrHeld
			}
			return result, nil
		}
		now := time.Now()
		step := func() {
			t.Helper()
			if err := w.step(context.Background(), now); err != nil && !errors.Is(err, executor.ErrHeld) {
				t.Fatal(err)
			}
		}
		step()
		if f.runs.Load() != 1 || len(w.focus) != 1 {
			t.Fatal(running, "a stale hold focuses its retry", f.runs.Load(), w.focus)
		}
		step()
		if f.runs.Load() != 2 || len(w.focus) != 0 {
			t.Fatal(running, "the retry ran at once", f.runs.Load(), w.focus)
		}
		step()
		if f.runs.Load() != 2 {
			t.Fatal(running, "a surviving hold is back on its backoff, not retried again")
		}
		held = false
		w.waits = map[domain.ActionID]workerWait{}
		step()
		if f.runs.Load() != 3 || len(w.focus) != 0 {
			t.Fatal(running, "the dispatch ran", f.runs.Load(), w.focus)
		}
	}
}

func TestWorkerHeldStale(t *testing.T) {
	t.Parallel()
	pending := domain.ProgressView{Stage: domain.Pending}
	stale := executor.Result{Refused: []policy.Refusal{{Reason: policy.StaleFacts}}}
	if !workerHeldStale(pending, stale, executor.ErrHeld) {
		t.Fatal("stale_facts refusal held")
	}
	if workerHeldStale(pending, stale, nil) || workerHeldStale(pending, stale, executor.ErrEvidence) {
		t.Fatal("only a hold counts")
	}
	if workerHeldStale(pending, executor.Result{Refused: []policy.Refusal{{Reason: policy.InsufficientStock}}}, executor.ErrHeld) {
		t.Fatal("a world-condition refusal is not stale facts")
	}
	if workerHeldStale(domain.ProgressView{Stage: domain.AwaitingObservation, Unresolved: true}, stale, executor.ErrHeld) {
		t.Fatal("a dispatched attempt is reconciliation, not a held dispatch")
	}
	if workerHeldStale(pending, executor.Result{}, executor.ErrHeld) {
		t.Fatal("a bare hold names no reason")
	}
}
