package buildingruntime

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"path/filepath"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/runtimeowner"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

func TestClockWorkerDisabledRestart(t *testing.T) {
	t.Parallel()
	for _, scenario := range []string{"applied", "lost-start-reply", "historical-unknown-renew"} {
		t.Run(scenario, func(t *testing.T) {
			ctx := context.Background()
			fixture, _, fake, intent := clockCoreFixture(t)
			path := filepath.Join(t.TempDir(), "restart.sqlite")
			db, err := store.Open(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			intent.RequestID = clockTestNextID(t, db)
			var lostRenewID string
			// Seed through the real command journal before acquiring any Session
			// profile owner. Stopping this core joins calls but does not erase debt.
			core, err := NewClockCoordinator(db, fake, fake, fixture.leases, boundary.FixedClock{}, fixture.config)
			if err != nil {
				t.Fatal(err)
			}
			if err = core.UpdateAuthority(executor.Authority{Snapshot: intent.Snapshot, Enabled: true}); err != nil {
				t.Fatal(err)
			}
			fake.lost = scenario == "lost-start-reply"
			original, commandErr := core.Command(ctx, intent)
			if fake.lost {
				if commandErr == nil || original.Phase != store.ClockUncertain {
					t.Fatal(original, commandErr)
				}
			} else if commandErr != nil || original.Phase != store.ClockApplied {
				t.Fatal(original, commandErr)
			}
			fake.lost = false
			if scenario == "historical-unknown-renew" {
				lostRenewID = clockTestNextID(t, db)
				renew := store.ClockIntent{RequestID: lostRenewID, Snapshot: intent.Snapshot, Command: bridge.ClockCommand{Renew: &bridge.ClockRenew{Original: proto.Clone(clockCoordinatorEpoch(fake.status)).(*k.Epoch), LeaseMS: 1000}}}
				if _, _, err = db.PrepareClock(ctx, renew); err != nil {
					t.Fatal(err)
				}
				if _, err = db.DispatchClock(ctx, renew.RequestID); err != nil {
					t.Fatal(err)
				}
				if _, err = db.MarkClockUncertain(ctx, renew.RequestID); err != nil {
					t.Fatal(err)
				}
			}
			if err = core.Stop(ctx); err != nil {
				t.Fatal(err)
			}
			namespace, err := db.Identity(ctx)
			if err != nil {
				t.Fatal(err)
			}
			if err = db.Close(); err != nil {
				t.Fatal(err)
			}
			db, err = store.Open(ctx, path)
			if err != nil {
				t.Fatal(err)
			}
			t.Cleanup(func() { _ = db.Close() })
			reopenedNamespace, err := db.Identity(ctx)
			if err != nil || reopenedNamespace != namespace {
				t.Fatal(reopenedNamespace, namespace, err)
			}

			session, authority, profile := newClockSessionTest(t, db, fake)
			player, err := NewPlayer(ctx, PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, session, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
			if err != nil {
				t.Fatal(err)
			}
			t.Cleanup(func() { _ = player.Close(context.Background()) })
			source := &schedulerNative{fake, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}
			native := &joinedClockNative{source: source, started: make(chan struct{}), paused: make(chan struct{}), captured: make(chan struct{})}
			scheduler, err := NewClockScheduler(player, session, native, ClockSchedulerConfig{Profile: profile, Start: *intent.Command.Start, MaxAge: time.Second}, boundary.FixedClock{})
			if err != nil {
				t.Fatal(err)
			}
			session.clock.native, session.clock.writer = native, native
			session.control.config.Worlds = clockWorldSource{native}
			if session.State().Enabled || authority.acquires.Load() != 0 {
				t.Fatal("restart restored permission")
			}
			worker, err := NewClockWorker(ctx, scheduler, native, ClockWorkerConfig{PollInterval: 10 * time.Millisecond, RenewInterval: 10 * time.Millisecond, StepInterval: 10 * time.Millisecond, MaxBackoff: 100 * time.Millisecond, CallTimeout: 200 * time.Millisecond, PageLimit: 128})
			if err != nil {
				t.Fatal(err)
			}
			t.Cleanup(func() { _ = worker.Stop(context.Background()) })
			select {
			case <-native.paused:
			case <-time.After(3 * time.Second):
				t.Fatal("restart failed to pause original epoch")
			}
			if owner, err := runtimeowner.Acquire(ctx, profile); !errors.Is(err, runtimeowner.ErrOwned) {
				if owner != nil {
					_ = owner.Close()
				}
				t.Fatal("profile released while worker still attached", err)
			}
			// Stop joins the pause's durable completion, including a lookup recovered
			// from the original Start namespace when its reply was lost.
			stopCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
			defer cancel()
			if err = worker.Stop(stopCtx); err != nil {
				t.Fatal(err)
			}
			select {
			case <-worker.done:
			default:
				t.Fatal("worker not joined")
			}
			epoch, err := db.LookupClockEpoch(ctx, intent.RequestID)
			if err != nil || epoch.Stage != store.ClockEpochPaused {
				t.Fatal(epoch, err)
			}
			recovered, err := db.LookupClockAttempt(ctx, intent.RequestID)
			if err != nil || recovered.Phase != store.ClockApplied || !proto.Equal(clockCoordinatorExpectation(original).Attempt, clockCoordinatorExpectation(recovered).Attempt) {
				t.Fatal(recovered, err)
			}
			if scenario == "historical-unknown-renew" {
				renew, err := db.LookupClockAttempt(ctx, lostRenewID)
				if err != nil || renew.Phase != store.ClockUncertain {
					t.Fatal(renew, err)
				}
			}
			native.mu.Lock()
			writes, pauses, lookups := fake.writes, fake.pauses, fake.lookups
			native.mu.Unlock()
			if writes != 1 || pauses != 1 || (scenario == "lost-start-reply" && lookups != 1) || session.State().Enabled || authority.acquires.Load() != 0 {
				t.Fatal("restart side effects", writes, pauses, lookups, session.State())
			}
			if owner, err := runtimeowner.Acquire(ctx, profile); !errors.Is(err, runtimeowner.ErrOwned) {
				if owner != nil {
					_ = owner.Close()
				}
				t.Fatal("profile not retained by Session", err)
			}
			if err = session.Close(stopCtx); err != nil {
				t.Fatal(err)
			}
			owner, err := runtimeowner.Acquire(ctx, profile)
			if err != nil {
				t.Fatal("joined Close retained profile", err)
			}
			if err = owner.Close(); err != nil {
				t.Fatal(err)
			}
		})
	}
}
