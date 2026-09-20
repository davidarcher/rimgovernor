package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// retakeFixture starts a window, stops it on an external pause and lets the
// player run the game by hand to tick 5000: the step that sees the stop
// settles the epoch and, with the game running, admits nothing (#601). It
// returns with the epoch settled, one start written and no pause issued.
func retakeFixture(t *testing.T) (*ClockScheduler, *schedulerNative) {
	t.Helper()
	s, f := schedulerFixture(t)
	first, err := s.Step(context.Background())
	if err != nil || first.Attempt == nil || first.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(first, err, f.writes)
	}
	retakePlayerRuns(f, 5000)
	settled, err := s.Step(context.Background())
	if !errors.Is(err, executor.ErrHeld) || settled.Retaken || settled.Deferred || f.writes != 1 || f.pauses != 0 {
		t.Fatal(settled, err, f.writes, f.pauses)
	}
	epochs, err := s.player.journal.LoadClockEpochs(context.Background(), 16)
	if err != nil || len(epochs) != 1 || !clockCoordinatorTerminal(epochs[0].Stage) {
		t.Fatal(epochs, err)
	}
	return s, f
}

// retakePlayerRuns puts the fake's clock in the state the issue observed:
// the window stopped on an external pause, the game un-paused by the
// player and its tick at the given value.
func retakePlayerRuns(f *schedulerNative, tick int64) {
	epoch := clockCoordinatorEpoch(f.status)
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: epoch, Reason: k.StopReason_STOP_REASON_EXTERNAL_PAUSE.Enum(), ActualPaused: proto.Bool(false), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(true), StoppedAtUnixMs: proto.Int64(100)}}
	f.status.ActualPaused = proto.Bool(false)
	f.status.ObservedSpeed = k.ObservedSpeed_OBSERVED_SPEED_ULTRAFAST.Enum()
	f.status.DurableEvents = proto.Bool(true)
	f.status.Context.Tick = proto.Int64(tick)
}

// TestClockSchedulerRetakesPlayerDrivenClock: a stopped clock whose tick
// advances under no owned epoch is the player running the game; after the
// quiet period the step pauses natively and starts the next window in the
// same step (#601).
func TestClockSchedulerRetakesPlayerDrivenClock(t *testing.T) {
	t.Parallel()
	s, f := retakeFixture(t)
	f.status.Context.Tick = proto.Int64(6000)
	got, err := s.Step(context.Background())
	if err != nil || !got.Retaken || got.Deferred || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.pauses != 1 || f.writes != 2 {
		t.Fatal(got, err, f.pauses, f.writes)
	}
	if f.status.GetRunning() == nil || !got.Reason.TickAdvanced {
		t.Fatal(f.status.State, got.Reason)
	}
}

// TestClockSchedulerRetakeWaitsForPlayerQuiet: a Manual authority bump
// within the quiet period (the player still pressing keys) defers the
// re-take; once the bump is old enough the step re-takes.
func TestClockSchedulerRetakeWaitsForPlayerQuiet(t *testing.T) {
	t.Parallel()
	s, f := retakeFixture(t)
	f.status.Context.Tick = proto.Int64(6000)
	s.noteManual(s.clock.Now().Add(-time.Second))
	got, err := s.Step(context.Background())
	if err != nil || !got.Deferred || got.Retaken || got.Attempt != nil || f.pauses != 0 || f.writes != 1 {
		t.Fatal(got, err, f.pauses, f.writes)
	}
	f.status.Context.Tick = proto.Int64(7000)
	s.noteManual(s.clock.Now().Add(-DefaultPlayerQuiet))
	got, err = s.Step(context.Background())
	if err != nil || !got.Retaken || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.pauses != 1 || f.writes != 2 {
		t.Fatal(got, err, f.pauses, f.writes)
	}
}

// TestClockSchedulerRetakeNeedsTheTickToAdvance: a stopped clock whose
// tick stands still is not the player's, whether the game reads paused (the
// player paused and stays paused: the normal pause-bound admission) or
// not (nothing to re-take; the admission holds as before).
func TestClockSchedulerRetakeNeedsTheTickToAdvance(t *testing.T) {
	t.Parallel()
	s, f := retakeFixture(t)
	got, err := s.Step(context.Background())
	if !errors.Is(err, executor.ErrHeld) || got.Retaken || got.Deferred || f.pauses != 0 || f.writes != 1 {
		t.Fatal(got, err, f.pauses, f.writes)
	}
	f.status.ActualPaused = proto.Bool(true)
	f.status.GetStopped().ActualPaused = proto.Bool(true)
	got, err = s.Step(context.Background())
	if err != nil || got.Retaken || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.pauses != 0 || f.writes != 2 {
		t.Fatal(got, err, f.pauses, f.writes)
	}
}

// TestClockPollNotesManualBumps: the poll records the wall time of a
// Manual authority change; every other event leaves it alone.
func TestClockPollNotesManualBumps(t *testing.T) {
	t.Parallel()
	manual := &k.EventsPage{Events: []*k.Event{{Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Reason: proto.String("Manual"), Active: proto.Bool(false)}}}}}
	other := &k.EventsPage{Events: []*k.Event{{Event: &k.Event_AuthorityChanged{AuthorityChanged: &k.AuthorityChanged{Reason: proto.String("None"), Active: proto.Bool(true)}}}}}
	if !clockPollManual(manual) || clockPollManual(other) || clockPollManual(&k.EventsPage{}) {
		t.Fatal("manual detection")
	}
}

// TestClockSchedulerLivePaceMeasuresThePlayer: livePace measures the pace
// under a stopped clock the player runs the same way it does under a
// running window, but only the window widens the drift.
func TestClockSchedulerLivePaceMeasuresThePlayer(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	t.Cleanup(func() { domain.SetLiveDrift(0) })
	retakePlayerRuns(f, 1000)
	at := time.Unix(200, 0)
	s.livePace(f.status, at)
	f.status.Context.Tick = proto.Int64(3000)
	s.livePace(f.status, at.Add(2*time.Second))
	if s.pacePerSecond != 1000 || domain.LiveDrift() != 0 {
		t.Fatal(s.pacePerSecond, domain.LiveDrift())
	}
	f.status.ActualPaused = proto.Bool(true)
	f.status.Context.Tick = proto.Int64(5000)
	s.livePace(f.status, at.Add(4*time.Second))
	if s.pacePerSecond != 1000 || domain.LiveDrift() != 0 {
		t.Fatal("a paused stop must neither measure nor widen", s.pacePerSecond, domain.LiveDrift())
	}
}
