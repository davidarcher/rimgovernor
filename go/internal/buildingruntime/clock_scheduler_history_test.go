package buildingruntime

import (
	"context"
	"database/sql"
	"fmt"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/clock"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

// A long-running save's journal (#634): the scheduler step reads bounded
// catalogs (LoadPlans 256, LoadClockAttempts and LoadClockEpochs 4096), so
// the history a colony accumulates -- retired plans, settled attempts and
// their terminal epochs -- must stay out of them, and the step's journal
// reads must not slow with it. The test seeds ten times the plan bound
// and one full attempt bound (ten times takes over a minute to file); the
// benchmark seeds ten times both.
const (
	historyPlans    = 10 * 256
	historyAttempts = 4096
	historyActive   = 3
)

// seedClockHistory files plans retired plans the way retirement leaves
// them (retired=1, rows kept for diagnostics) and attempts applied Start
// windows observed stopped, retired at the tail the scheduler's own
// maintenance keeps (maintainClockAttempts), so the sequence watermark
// carries the history while the retained set stays the maintained tail.
// historyActive plans are then created active; their IDs are returned.
func seedClockHistory(tb testing.TB, db *store.Store, path string, plans, attempts int) []domain.PlanID {
	tb.Helper()
	ctx := context.Background()
	// A second connection on the shared-cache database retires the plans
	// directly, as the store's own catalog tests do: retirement through
	// the routine review needs a settled autopilot goal per plan.
	raw, err := sql.Open(store.DriverName, path)
	if err != nil {
		tb.Fatal(err)
	}
	defer raw.Close()
	for filed := 0; filed < plans; {
		batch := min(200, plans-filed)
		for i := 0; i < batch; i++ {
			plan := historyPlan(tb, fmt.Sprintf("history-%05d", filed+i))
			if err = db.CreatePlan(ctx, plan); err != nil {
				tb.Fatal(err)
			}
		}
		if _, err = raw.ExecContext(ctx, "UPDATE plans SET retired=1 WHERE id LIKE 'history-%'"); err != nil {
			tb.Fatal(err)
		}
		filed += batch
	}
	var active []domain.PlanID
	for i := 0; i < historyActive; i++ {
		plan := historyPlan(tb, fmt.Sprintf("active-%d", i))
		if err = db.CreatePlan(ctx, plan); err != nil {
			tb.Fatal(err)
		}
		active = append(active, plan.ID())
	}
	seedClockHistoryAttempts(tb, db, path, attempts)
	return active
}

// seedClockHistoryAttempts is the attempt half of seedClockHistory.
func seedClockHistoryAttempts(tb testing.TB, db *store.Store, path string, attempts int) {
	tb.Helper()
	ctx := context.Background()
	head, err := db.ReadClockSequence(ctx)
	if err != nil {
		tb.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 7}
	policy := &k.WatchPolicy{Mode: k.WatchMode_WATCH_MODE_COLONY.Enum(), HealthDropFraction: proto.Float32(.1), MinHealthFraction: proto.Float32(.2), HostileWithin: proto.Float32(20), InjuryStopCooldownMs: proto.Uint32(0)}
	for i := 0; i < attempts; i++ {
		id, err := head.NextRequestID()
		if err != nil {
			tb.Fatal(err)
		}
		head.LastAllocated++
		intent := store.ClockIntent{RequestID: id, Key: id, Snapshot: snapshot, Command: bridge.ClockCommand{Start: &bridge.ClockStart{Speed: k.Speed_SPEED_NORMAL, Policy: policy, LeaseMS: 1000, MaxTicks: 100}}}
		attempt, _, err := db.PrepareClock(ctx, intent)
		if err != nil {
			tb.Fatal(err)
		}
		if _, err = db.DispatchClock(ctx, id); err != nil {
			tb.Fatal(err)
		}
		reply := historyApplied(attempt)
		if _, err = db.RecordClockReply(ctx, id, reply); err != nil {
			tb.Fatal(err)
		}
		status := historyStopped(reply.GetReceipt().GetApplied().GetStatus())
		if _, err = db.ObserveClockEpoch(ctx, id, 0, status.Context, status); err != nil {
			tb.Fatal(err)
		}
		if head.LastAllocated-head.RetiredThrough >= clockHistoryTail {
			retired, err := db.RetireClockHistory(ctx, head, clockHistoryTail)
			if err != nil {
				tb.Fatal(err)
			}
			head = retired.State
		}
	}
}

// historyPlan is a one-action plan named id; action IDs are journal-wide,
// so each plan's is derived from its own.
func historyPlan(tb testing.TB, id string) domain.PlanSpec {
	tb.Helper()
	building, err := domain.NewBuilding("Wall", domain.Cell{X: 1, Z: 2}, domain.North, "WoodLog")
	if err != nil {
		tb.Fatal(err)
	}
	action, err := domain.NewBuildingAction(domain.ActionID(id+"-action"), building)
	if err != nil {
		tb.Fatal(err)
	}
	plan, err := domain.NewPlan(domain.PlanID(id), 1, []domain.Action{action})
	if err != nil {
		tb.Fatal(err)
	}
	return plan
}

// historyApplied is the applied receipt of a Start attempt: a running
// epoch owned by the attempt's session, as the native fake answers.
func historyApplied(v store.ClockAttempt) *k.ControlReply {
	e := clock.Expectation(v)
	ctx := &c.ObservationContext{Identity: e.Identity, NativeGeneration: proto.Uint64(e.NativeGeneration), Tick: proto.Int64(12)}
	start := v.Intent.Command.Start
	epoch := &k.Epoch{Owner: &k.EpochOwner{ControllerSessionId: proto.String(e.Attempt.GetControllerSessionId()), Epoch: proto.Int64(1)}, Origin: ctx, RequestedSpeed: start.Speed.Enum(), Policy: proto.Clone(start.Policy).(*k.WatchPolicy), StartTick: proto.Int64(12), TickDeadline: proto.Int64(112), LastTick: proto.Int64(12), LeaseRemainingMs: proto.Uint32(900)}
	status := &k.Status{Context: ctx, State: &k.Status_Running{Running: &k.Running{Epoch: epoch}}, NativeTickBoundary: proto.Bool(true), DurableEvents: proto.Bool(true), NewestCursor: proto.Int64(0), ObservedSpeed: k.ObservedSpeed_OBSERVED_SPEED_NORMAL.Enum(), ActualPaused: proto.Bool(false), EvidenceCompleteness: &c.PageInfo{Complete: proto.Bool(true)}}
	return &k.ControlReply{Outcome: &k.ControlReply_Receipt{Receipt: &k.ControlReceipt{Attempt: e.Attempt, AdmittedContext: ctx, Outcome: &k.ControlReceipt_Applied{Applied: &k.AppliedControl{Status: status}}}}}
}

// historyStopped is status with its running epoch stopped on a verified
// pause: the terminal observation that settles the epoch.
func historyStopped(status *k.Status) *k.Status {
	v := proto.Clone(status).(*k.Status)
	epoch := v.GetRunning().Epoch
	v.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_REQUESTED_PAUSE.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(true), StoppedAtUnixMs: proto.Int64(100)}}
	v.ActualPaused = proto.Bool(true)
	v.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_PAUSED.Enum()
	return v
}

// historyJournal opens a journal and hands back its shared-cache URI too.
func historyJournal(tb testing.TB) (*store.Store, string) {
	tb.Helper()
	path := storetest.Path(tb)
	db, err := store.Open(context.Background(), path)
	if err != nil {
		tb.Fatal(err)
	}
	tb.Cleanup(func() { db.Close() })
	return db, path
}

func TestClockSchedulerHistoryKeepsActiveObligationsVisible(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db, path := historyJournal(t)
	s, _ := schedulerFixtureJournal(t, db, nil)
	// The step's journal time on an empty history, before any is seeded:
	// this step admits the scheduler's window.
	fresh, err := s.Step(ctx)
	if err != nil || fresh.Attempt == nil || fresh.Attempt.Phase != store.ClockApplied {
		t.Fatal(fresh, err)
	}
	active := seedClockHistory(t, db, path, historyPlans, historyAttempts)
	// The catalogs the step reads: every active plan, the maintained tail
	// of attempts, and the scheduler's own window as the one open epoch.
	plans, err := db.LoadPlans(ctx, 256)
	if err != nil {
		t.Fatal(err)
	}
	seen := map[domain.PlanID]bool{}
	for _, plan := range plans {
		seen[plan.Spec.ID()] = true
	}
	for _, id := range active {
		if !seen[id] {
			t.Fatalf("active plan %s hidden behind %d retired plans: %d visible", id, historyPlans, len(plans))
		}
	}
	if !seen["plan"] || len(plans) != historyActive+1 {
		t.Fatalf("active catalog %d plans, want %d", len(plans), historyActive+1)
	}
	head, err := db.ReadClockSequence(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if head.LastAllocated < uint64(historyAttempts) {
		t.Fatalf("sequence allocated %d, want >= %d", head.LastAllocated, historyAttempts)
	}
	attempts, err := db.LoadClockAttempts(ctx, 4096)
	if err != nil || len(attempts) > 2*clockHistoryTail {
		t.Fatalf("retained attempts %d after %d settled, err %v", len(attempts), historyAttempts, err)
	}
	var open []string
	for _, attempt := range attempts {
		if attempt.Phase == store.ClockDispatched || attempt.Phase == store.ClockUncertain {
			open = append(open, attempt.Intent.RequestID)
		}
	}
	if len(open) != 0 {
		t.Fatalf("unresolved attempts %v", open)
	}
	epochs, err := db.LoadClockEpochs(ctx, 4096)
	if err != nil {
		t.Fatal(err)
	}
	for _, owned := range epochs {
		if !clockCoordinatorTerminal(owned.Stage) {
			open = append(open, owned.Epoch.GetOwner().String())
		}
	}
	if len(open) != 1 {
		t.Fatalf("open epochs %v among %d, want the scheduler's own", open, len(epochs))
	}
	// The step over the seeded history still finds its window running.
	seeded, err := s.Step(ctx)
	if err != nil || !seeded.Running {
		t.Fatal(seeded, err)
	}
	t.Logf("journal_ms fresh=%.2f seeded=%.2f (plans=%d attempts=%d)", float64(fresh.Journal)/float64(time.Millisecond), float64(seeded.Journal)/float64(time.Millisecond), historyPlans, historyAttempts)
}

// BenchmarkClockSchedulerHistoryJournal times the catalog reads the step
// issues (LoadClockAttempts, LoadClockEpochs, LoadPlan, LoadPlans) over
// ten times the catalog bounds of retired history against the same reads
// with nothing retired: an empty journal, and one holding just the attempt
// tail maintenance keeps (clockHistoryTail), which every colony carries
// after its first windows. History must stay within 2x of the tail (#634);
// the empty journal is reported for the tail's own cost.
func BenchmarkClockSchedulerHistoryJournal(b *testing.B) {
	measure := func(b *testing.B, plans, attempts int) time.Duration {
		b.Helper()
		ctx := context.Background()
		db, path := historyJournal(b)
		if err := db.CreatePlan(ctx, historyPlan(b, "plan")); err != nil {
			b.Fatal(err)
		}
		seedClockHistory(b, db, path, plans, attempts)
		b.ResetTimer()
		began := time.Now()
		for i := 0; i < b.N; i++ {
			if _, err := db.LoadClockAttempts(ctx, 4096); err != nil {
				b.Fatal(err)
			}
			if _, err := db.LoadClockEpochs(ctx, 4096); err != nil {
				b.Fatal(err)
			}
			if _, err := db.LoadPlan(ctx, "plan"); err != nil {
				b.Fatal(err)
			}
			if _, err := db.LoadPlans(ctx, 256); err != nil {
				b.Fatal(err)
			}
		}
		b.StopTimer()
		return time.Since(began) / time.Duration(b.N)
	}
	var tail, seeded time.Duration
	b.Run("empty", func(b *testing.B) {
		empty := measure(b, 0, 0)
		b.ReportMetric(float64(empty)/float64(time.Millisecond), "journal_ms/step")
	})
	b.Run("tail", func(b *testing.B) {
		tail = measure(b, 0, clockHistoryTail)
		b.ReportMetric(float64(tail)/float64(time.Millisecond), "journal_ms/step")
	})
	b.Run("history", func(b *testing.B) {
		seeded = measure(b, historyPlans, 10*4096)
		b.ReportMetric(float64(seeded)/float64(time.Millisecond), "journal_ms/step")
	})
	if seeded > 2*tail {
		b.Fatalf("journal time %s at %d retired plans and %d settled attempts exceeds 2x the tail's %s", seeded, historyPlans, 10*4096, tail)
	}
}
