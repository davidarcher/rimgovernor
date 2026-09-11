package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/testkit"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type clockWindowDispatchClock struct {
	clock *testkit.ManualClock
	reads int
}

func (c *clockWindowDispatchClock) Now() time.Time {
	c.reads++
	if c.reads == 3 {
		c.clock.Advance(2 * time.Second)
	}
	return c.clock.Now()
}

func TestClockWindowExpiryAfterDispatchRetainsUncertainty(t *testing.T) {
	q, db, f, clock, request := clockWindowFixture(t)
	q.clock = &clockWindowDispatchClock{clock: clock}
	result, err := q.CommandWindow(context.Background(), request)
	if !errors.Is(err, executor.ErrHeld) || result.Phase != store.ClockUncertain || f.writes != 0 {
		t.Fatal(result, err, f.writes)
	}
	retained, err := db.LookupClockAttempt(context.Background(), request.Intent.RequestID)
	if err != nil || retained.Phase != store.ClockUncertain || retained.Intent.Window == nil {
		t.Fatal(retained, err)
	}
}

func clockWindowFixture(t *testing.T) (*ClockCoordinator, *store.Store, *clockCoreFake, *testkit.ManualClock, ClockWindowRequest) {
	t.Helper()
	q, db, f, intent := clockCoreFixture(t)
	clock := testkit.NewManualClock(time.Unix(1000, 0))
	q.clock = clock
	profile := t.TempDir()
	if _, err := db.BindClockInbox(context.Background(), profile); err != nil {
		t.Fatal(err)
	}
	intent.Window = &store.ClockWindowAdmission{Profile: profile, Snapshot: intent.Snapshot, Tick: 12, MaxTicks: intent.Command.Start.MaxTicks}
	emergency, err := policy.NewEmergencySnapshot(intent.Snapshot, 12, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)})
	if err != nil {
		t.Fatal(err)
	}
	facts := policy.ClockWindowFacts{Current: intent.Snapshot, Tick: 12, StartedAt: clock.Now(), ObservedAt: clock.Now(), Emergency: emergency, Review: policy.ClockWindowReview{HasHolds: domain.Known(false)}, Status: policy.ClockWindowStatus{Snapshot: intent.Snapshot, Tick: 12, State: policy.ClockNeverStarted, ActualPaused: domain.Known(true), NativeTickBoundary: domain.Known(true), DurableEvents: domain.Known(false), NewestCursor: domain.Known(int64(0))}, Obligations: policy.ClockWindowObligations{Complete: domain.Known(true), OwnedEpochPending: domain.Known(false), UnknownStartPending: domain.Known(false)}, WorkRemaining: domain.Known(true)}
	if err = q.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true}); err != nil {
		t.Fatal(err)
	}
	return q, db, f, clock, ClockWindowRequest{Intent: intent, Facts: facts, MaxAge: time.Second}
}

func TestClockWindowCommandRequiresExactPolicyAdmission(t *testing.T) {
	q, _, f, _, request := clockWindowFixture(t)
	if _, err := q.Command(context.Background(), request.Intent); err == nil {
		t.Fatal("generic freshness bypass")
	}
	bad := request
	w := *request.Intent.Window
	w.MaxTicks++
	bad.Intent.Window = &w
	if _, err := q.CommandWindow(context.Background(), bad); err == nil {
		t.Fatal("different decision admitted")
	}
	r, err := q.CommandWindow(context.Background(), request)
	if err != nil || r.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(r, err)
	}
	if _, err = q.CommandWindow(context.Background(), request); err != nil || f.writes != 1 {
		t.Fatal("replay started twice", err)
	}
	next := request
	next.Intent.RequestID = "second"
	if _, err = q.CommandWindow(context.Background(), next); err == nil || f.writes != 1 {
		t.Fatal("second owned start admitted", err)
	}
}

func TestClockWindowExpiresInQueueAndLeaseLookup(t *testing.T) {
	for _, where := range []string{"queue", "lease"} {
		t.Run(where, func(t *testing.T) {
			q, db, f, clock, request := clockWindowFixture(t)
			var err error
			if where == "queue" {
				q.gate <- struct{}{}
				done := make(chan error, 1)
				go func() { _, e := q.CommandWindow(context.Background(), request); done <- e }()
				clock.Advance(2 * time.Second)
				<-q.gate
				err = <-done
			} else {
				q.leases = clockCoreLease{func(domain.GenerationSnapshot) (string, error) { clock.Advance(2 * time.Second); return "lease", nil }}
				_, err = q.CommandWindow(context.Background(), request)
			}
			if !errors.Is(err, executor.ErrHeld) || f.writes != 0 {
				t.Fatal(err, f.writes)
			}
			v, lookup := db.LookupClockAttempt(context.Background(), request.Intent.RequestID)
			if where == "queue" && !errors.Is(lookup, store.ErrNotFound) {
				t.Fatal(v, lookup)
			}
			if where == "lease" && (lookup != nil || v.Phase != store.ClockPrepared) {
				t.Fatal(v, lookup)
			}
		})
	}
}

func TestClockWindowRechecksNativeStatus(t *testing.T) {
	for _, field := range []string{"tick", "cursor", "paused", "boundary", "durability"} {
		t.Run(field, func(t *testing.T) {
			q, _, f, _, request := clockWindowFixture(t)
			switch field {
			case "tick":
				f.status.Context.Tick = proto.Int64(13)
			case "cursor":
				f.status.NewestCursor = proto.Int64(1)
			case "paused":
				f.status.ActualPaused = proto.Bool(false)
			case "boundary":
				f.status.NativeTickBoundary = proto.Bool(false)
			case "durability":
				f.status.DurableEvents = nil
			}
			if _, err := q.CommandWindow(context.Background(), request); err == nil || f.writes != 0 {
				t.Fatal(field, err)
			}
		})
	}
}

func TestClockWindowInterveningEventAndManualPreventDispatch(t *testing.T) {
	for _, kind := range []string{"event", "manual"} {
		t.Run(kind, func(t *testing.T) {
			q, db, f, _, request := clockWindowFixture(t)
			q.leases = clockCoreLease{func(domain.GenerationSnapshot) (string, error) {
				if kind == "manual" {
					if err := q.UpdateAuthority(executor.Authority{Snapshot: request.Intent.Snapshot}); err != nil {
						t.Fatal(err)
					}
				} else {
					ctx := proto.Clone(f.status.Context).(*c.ObservationContext)
					r := &k.EventsRequest{Identity: ctx.Identity, AfterCursor: proto.Int64(0), Limit: proto.Uint32(1)}
					page := &k.EventsPage{Context: ctx, NewestCursor: proto.Int64(1), NextCursor: proto.Int64(1), Gap: proto.Bool(false), LostCount: proto.Uint64(0), Events: []*k.Event{{Cursor: proto.Int64(1), Owner: &k.EpochOwner{ControllerSessionId: proto.String("session"), Epoch: proto.Int64(1)}, Context: ctx, ObservedAtUnixMs: proto.Int64(100), Event: &k.Event_Alert{Alert: &k.Alert{Key: proto.String("alert"), Label: proto.String("danger"), Priority: proto.String("High")}}}}}
					if _, _, err := db.AppendClockEvents(context.Background(), request.Intent.Window.Profile, r, page); err != nil {
						t.Fatal(err)
					}
				}
				return "lease", nil
			}}
			if _, err := q.CommandWindow(context.Background(), request); err == nil || f.writes != 0 {
				t.Fatal(kind, err)
			}
			v, err := db.LookupClockAttempt(context.Background(), request.Intent.RequestID)
			if err != nil || v.Phase != store.ClockPrepared {
				t.Fatal(v, err)
			}
		})
	}
}
