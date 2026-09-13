package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

type schedulerNative struct {
	*clockCoreFake
	emergency policy.EmergencyFacts
}

func (f *schedulerNative) ReadEmergency(ctx context.Context, id *c.Identity) (bridge.EmergencyObservation, bridge.Result, error) {
	return bridge.EmergencyObservation{Context: proto.Clone(f.status.Context).(*c.ObservationContext), Facts: f.emergency}, bridge.Result{}, ctx.Err()
}
func schedulerFixture(t *testing.T) (*ClockScheduler, *schedulerNative) {
	t.Helper()
	_, db, f, intent := clockCoreFixture(t)
	s, _, profile := newClockSessionTest(t, db, f)
	p, err := NewPlayer(context.Background(), PlayerConfig{CallTimeout: time.Second, JournalTimeout: time.Second}, db, s, playerWorldFunc(func(context.Context) (store.World, error) { return playerWorld(intent.Snapshot), nil }))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = p.Close(context.Background()) })
	if _, err = s.Acquire(context.Background(), intent.Snapshot); err != nil {
		t.Fatal(err)
	}
	native := &schedulerNative{f, policy.EmergencyFacts{ColonistsComplete: domain.Known(true), ThreatsComplete: domain.Known(true)}}
	scheduler, err := NewClockScheduler(p, s, native, ClockSchedulerConfig{Profile: profile, Start: *intent.Command.Start, MaxAge: time.Second}, boundary.FixedClock{})
	if err != nil {
		t.Fatal(err)
	}
	return scheduler, native
}
func TestClockSchedulerStartsOnceAndLeavesRunningEpoch(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	second, err := s.Step(context.Background())
	if err != nil || !second.Running || f.writes != 1 || f.pauses != 0 {
		t.Fatal(second, err)
	}
	if err = s.session.Disable(); err != nil {
		t.Fatal(err)
	}
	cleanup, err := s.Step(context.Background())
	if err != nil || !cleanup.Cleaned || f.pauses != 1 || f.writes != 1 {
		t.Fatal(cleanup, err)
	}
}
func TestClockSchedulerUnknownRecoversExactRequest(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	f.lost = true
	first, err := s.Step(context.Background())
	if err == nil || first.Attempt == nil || first.Attempt.Phase != store.ClockUncertain {
		t.Fatal(first, err)
	}
	second, err := s.Step(context.Background())
	if err != nil || !second.Reconciled || second.Attempt == nil || second.Attempt.Intent.RequestID != first.Attempt.Intent.RequestID || f.writes != 1 {
		t.Fatal(second, err)
	}
}
func TestClockSchedulerUnsafeAndUnreviewedNeverStart(t *testing.T) {
	t.Parallel()
	for _, kind := range []string{"unsafe", "unreviewed", "unknown", "disabled", "cancelled"} {
		t.Run(kind, func(t *testing.T) {
			s, f := schedulerFixture(t)
			ctx := context.Background()
			switch kind {
			case "unsafe":
				f.emergency.Threats = []policy.EmergencyThreat{{ID: "raider", Kind: policy.Hostile, Dead: domain.Known(false), Downed: domain.Known(false)}}
			case "unreviewed":
				f.status.NewestCursor = proto.Int64(1)
			case "unknown":
				f.emergency.ColonistsComplete = domain.Unknown[bool]()
			case "disabled":
				_ = s.session.Disable()
			case "cancelled":
				var cancel context.CancelFunc
				ctx, cancel = context.WithCancel(ctx)
				cancel()
			}
			if _, err := s.Step(ctx); err == nil {
				t.Fatal("admitted")
			}
			if f.writes != 0 {
				t.Fatal("native start")
			}
		})
	}
}
func TestClockSchedulerIdentityAndInertPrepared(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	// Prepare the exact prospective request without dispatch, then let native time
	// advance. Undispatched history must not permanently prevent a new window.
	state := s.session.State()
	plan, err := s.player.journal.LoadPlan(context.Background(), state.Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	_, work, err := clockSchedulerWork(plan, state.Snapshot)
	if err != nil {
		t.Fatal(err)
	}
	admission := &store.ClockWindowAdmission{Profile: s.config.Profile, Snapshot: state.Snapshot, Tick: 11, MaxTicks: s.config.Start.MaxTicks}
	key, err := clockSchedulerKey(admission, work, s.config.Start)
	if err != nil {
		t.Fatal(err)
	}
	same, _ := clockSchedulerKey(admission, work, s.config.Start)
	if same != key {
		t.Fatal("unstable logical key")
	}
	start := s.config.Start
	id := clockTestNextID(t, s.player.journal)
	_, _, err = s.player.journal.PrepareClock(context.Background(), store.ClockIntent{RequestID: id, Key: key, Snapshot: state.Snapshot, Command: bridge.ClockCommand{Start: &start}, Window: admission})
	if err != nil {
		t.Fatal(err)
	}
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Intent.RequestID == id || f.writes != 1 {
		t.Fatal(got, err)
	}
}
func TestClockSchedulerRejectsProfileAndSuppression(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	config := s.config
	config.Profile = t.TempDir()
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("foreign profile")
	}
	config = s.config
	config.Start.Policy = proto.Clone(config.Start.Policy).(*k.WatchPolicy)
	config.Start.Policy.AcknowledgedHostileIds = []string{"raider"}
	if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
		t.Fatal("suppression")
	}
}

func TestClockSchedulerCancelledWorkCannotAdvanceTime(t *testing.T) {
	t.Parallel()
	s, _ := schedulerFixture(t)
	state, err := s.player.journal.LoadPlan(context.Background(), s.session.State().Snapshot.Plan)
	if err != nil {
		t.Fatal(err)
	}
	p := state.Progress[0]
	p, err = p.Prepare(s.session.State().Snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(s.session.State().Snapshot, 10)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	state.Progress[0] = p
	work, _, err := clockSchedulerWork(state, s.session.State().Snapshot)
	if err != nil || work || !p.View().Unresolved {
		t.Fatal(work, err, p.View())
	}
}

func TestClockSchedulerUnknownStartWorldReplacement(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	f.lost = true
	first, err := s.Step(context.Background())
	if err == nil || first.Attempt == nil {
		t.Fatal(first, err)
	}
	f.status.Context.Identity.LoadToken = proto.String("replacement")
	got, err := s.Step(context.Background())
	if err != nil || !got.Cleaned || f.writes != 1 || f.pauses != 0 {
		t.Fatal(got, err)
	}
	old, err := s.player.journal.LookupClockAttempt(context.Background(), first.Attempt.Intent.RequestID)
	if err != nil || old.SupersededAt == nil || old.Phase != store.ClockUncertain {
		t.Fatal(old, err)
	}
}
