package buildingruntime

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	"google.golang.org/protobuf/proto"
)

func stoppedStatus(atUnixMs int64) *k.Status {
	return &k.Status{State: &k.Status_Stopped{Stopped: &k.Stopped{StoppedAtUnixMs: proto.Int64(atUnixMs)}}}
}

// The pause estimate folds each native stop in once, weighting the newest
// span against the running estimate, and lets a later sighting of the
// same stop replace its contribution; spans that cannot be one review's
// wait (negative, or longer than clockWindowPauseLimit) are ignored, as is
// a status that is not stopped.
func TestClockWindowPauseObservesEachStopOnce(t *testing.T) {
	t.Parallel()
	var p clockWindowPause
	now := time.UnixMilli(100_000)
	p.observe(&k.Status{State: &k.Status_Running{Running: &k.Running{}}}, now)
	p.observe(stoppedStatus(100_000), now)
	p.observe(stoppedStatus(100_000-int64(clockWindowPauseLimit/time.Millisecond)-1), now)
	if p.known {
		t.Fatal(p)
	}
	p.observe(stoppedStatus(96_000), now)
	if !p.known || p.seconds != 4 {
		t.Fatal(p)
	}
	// The same stop seen later (an admission was refused) replaces the
	// sample rather than adding to it.
	p.observe(stoppedStatus(96_000), now.Add(2*time.Second))
	if p.seconds != 6 {
		t.Fatal(p)
	}
	p.observe(stoppedStatus(98_000), now.Add(10*time.Second))
	if p.seconds != 9 { // 0.5*12 + 0.5*6
		t.Fatal(p)
	}
	p.observe(stoppedStatus(98_000), now.Add(12*time.Second))
	if p.seconds != 10 { // 0.5*14 + 0.5*6
		t.Fatal(p)
	}
}

// A sized window is the ticks the speed runs in the larger of the target
// and the observed pause, rounded up to clockWindowQuantum, never below
// the floor nor above the cap; a zero sizing keeps the floor.
func TestClockWindowSizingColonyWindow(t *testing.T) {
	t.Parallel()
	known := func(seconds float64) clockWindowPause { return clockWindowPause{known: true, seconds: seconds} }
	for _, tc := range []struct {
		name   string
		sizing ClockWindowSizing
		floor  uint32
		pause  clockWindowPause
		want   ClockWindowSize
	}{
		{"disabled", ClockWindowSizing{}, 2500, known(30), ClockWindowSize{Ticks: 2500}},
		{"cap at floor", ClockWindowSizing{Seconds: 2, TicksPerSecond: 900, MaxTicks: 2500}, 2500, known(30), ClockWindowSize{Ticks: 2500}},
		{"floor wins", ClockWindowSizing{Seconds: 2, TicksPerSecond: 60, MaxTicks: 60000}, 2500, clockWindowPause{}, ClockWindowSize{Ticks: 2500, TargetSeconds: 2, TicksPerSecond: 60}},
		{"target at ultrafast", ClockWindowSizing{Seconds: 2, TicksPerSecond: 900, MaxTicks: 60000}, 100, clockWindowPause{}, ClockWindowSize{Ticks: 1800, TargetSeconds: 2, TicksPerSecond: 900}},
		{"pause stretches", ClockWindowSizing{Seconds: 2, TicksPerSecond: 900, MaxTicks: 60000}, 2500, known(4.01), ClockWindowSize{Ticks: 3700, TargetSeconds: 4.01, TicksPerSecond: 900}},
		{"cap under boost", ClockWindowSizing{Seconds: 2, TicksPerSecond: 7000, MaxTicks: 60000}, 2500, known(10), ClockWindowSize{Ticks: 60000, TargetSeconds: 10, TicksPerSecond: 7000}},
	} {
		if got := tc.sizing.colonyWindow(tc.floor, tc.pause, clockWindowRate{}); got != tc.want {
			t.Fatal(tc.name, got, tc.want)
		}
	}
}

func runningStatus(tick int64) *k.Status {
	return &k.Status{Context: &c.ObservationContext{Tick: proto.Int64(tick)}, State: &k.Status_Running{Running: &k.Running{}}}
}

// The observed rate samples the ticks between consecutive running
// statuses at least clockWindowRateMinSpan apart, weighting the newest
// against the estimate; a stop between them breaks the chain, and the
// rate only narrows a sized window below the nominal rate (#193).
func TestClockWindowRateNarrowsTheWindowUnderLoad(t *testing.T) {
	t.Parallel()
	var r clockWindowRate
	now := time.UnixMilli(100_000)
	r.observe(runningStatus(1000), now)
	r.observe(runningStatus(1100), now.Add(200*time.Millisecond)) // too close
	if r.known {
		t.Fatal(r)
	}
	r.observe(runningStatus(1500), now.Add(2*time.Second)) // 400 ticks in 1.8s from the last sighting
	if !r.known || r.ticksPerSecond < 222 || r.ticksPerSecond > 223 {
		t.Fatal(r)
	}
	r.observe(stoppedStatus(102_000), now.Add(3*time.Second))
	r.observe(runningStatus(9000), now.Add(4*time.Second)) // after a stop: no sample
	if r.ticksPerSecond < 222 || r.ticksPerSecond > 223 {
		t.Fatal(r)
	}
	r.observe(runningStatus(9300), now.Add(5*time.Second)) // 300/s
	if want := 0.5*300 + 0.5*r.ticksPerSecond; r.ticksPerSecond > 262 || r.ticksPerSecond < 261 || want < 261 {
		t.Fatal(r)
	}
	sizing := ClockWindowSizing{Seconds: 2, TicksPerSecond: 7000, MaxTicks: 60000}
	pause := clockWindowPause{known: true, seconds: 4.4}
	if got := sizing.colonyWindow(2500, pause, clockWindowRate{known: true, ticksPerSecond: 250}); got != (ClockWindowSize{Ticks: 2500, TargetSeconds: 4.4, TicksPerSecond: 250}) {
		t.Fatal(got)
	}
	if got := sizing.colonyWindow(500, pause, clockWindowRate{known: true, ticksPerSecond: 250}); got != (ClockWindowSize{Ticks: 1100, TargetSeconds: 4.4, TicksPerSecond: 250}) {
		t.Fatal(got)
	}
	// A rate above the nominal one (a boost the config underestimates)
	// never widens the window.
	if got := sizing.colonyWindow(2500, pause, clockWindowRate{known: true, ticksPerSecond: 9000}); got != (ClockWindowSize{Ticks: 30900, TargetSeconds: 4.4, TicksPerSecond: 7000}) {
		t.Fatal(got)
	}
}

// The scheduler refuses a sizing whose cap is below the fixed budget or
// whose target or rate is negative, and a sized step asks native for the
// sized window while reporting it in the result.
func TestClockSchedulerSizesTheColonyWindow(t *testing.T) {
	t.Parallel()
	s, f := schedulerFixture(t)
	for _, bad := range []ClockWindowSizing{{Seconds: -1}, {TicksPerSecond: -1}, {Seconds: 2, TicksPerSecond: 900, MaxTicks: s.config.Start.MaxTicks - 1}} {
		config := s.config
		config.Window = bad
		if _, err := NewClockScheduler(s.player, s.session, s.native, config, s.clock); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	config := s.config
	config.Window = ClockWindowSizing{Seconds: 2, TicksPerSecond: 900, MaxTicks: 60000}
	var err error
	if s, err = NewClockScheduler(s.player, s.session, s.native, config, s.clock); err != nil {
		t.Fatal(err)
	}
	// The first window starts under the 2s target: 1800 ticks at 900/s.
	got, err := s.Step(context.Background())
	if err != nil || got.Attempt == nil || got.Attempt.Phase != store.ClockApplied || f.writes != 1 {
		t.Fatal(got, err, f.writes)
	}
	if got.Window != (ClockWindowSize{Ticks: 1800, TargetSeconds: 2, TicksPerSecond: 900}) || got.Attempt.Intent.Command.Start.MaxTicks != 1800 || f.status.GetRunning().Epoch.GetTickDeadline() != 12+1800 {
		t.Fatal(got.Window, got.Attempt.Intent.Command.Start.MaxTicks, f.status.GetRunning().Epoch.GetTickDeadline())
	}
	// That window ran out 3s before the fixed clock's now: the step that
	// retires the epoch reviews at once (issue #162), and the observed
	// pause stretches the target to 3s for the next admission (which the
	// fixture's status facts then refuse; the first window proved the
	// size reaches native).
	f.status.State = &k.Status_Stopped{Stopped: &k.Stopped{Epoch: f.status.GetRunning().Epoch, Reason: k.StopReason_STOP_REASON_TICK_BUDGET.Enum(), ActualPaused: proto.Bool(true), PauseVerified: proto.Bool(true), PauseRequested: proto.Bool(false), StoppedAtUnixMs: proto.Int64(s.clock.Now().Add(-3 * time.Second).UnixMilli())}}
	f.status.ActualPaused = proto.Bool(true)
	if got, err = s.Step(context.Background()); !errors.Is(err, executor.ErrHeld) || got.Cleaned || got.Window != (ClockWindowSize{Ticks: 2700, TargetSeconds: 3, TicksPerSecond: 900}) {
		t.Fatal(got, err)
	}
	// Seeing the same stop again does not compound the estimate.
	if got, _ = s.Step(context.Background()); got.Window != (ClockWindowSize{Ticks: 2700, TargetSeconds: 3, TicksPerSecond: 900}) {
		t.Fatal(got.Window)
	}
}
