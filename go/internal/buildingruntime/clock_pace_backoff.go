package buildingruntime

import (
	"context"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	"google.golang.org/protobuf/proto"
)

// Player acceleration's controller side (issue #627). Native paces a
// PACING_PLAYER_ACCELERATED window up to the boosted rate; the controller
// keeps its critical evidence (the admission cycle's critical wave) current
// by lowering the window's tick-rate ceiling before that evidence can age
// past the safe horizon, rather than letting the window run blind and then
// stop and readmit at full speed.
//
// Each critical wave under a running window is watched: the wave's
// evidence was read at a known wall time, and at the window's worst-case
// rate (its ceiling, or the boosted rate without one) it ages past half the
// horizon after horizon*paceMargin/rate seconds. If the wave has not
// completed by then the evidence is stale, and the backoff lowers the
// ceiling to Normal at once. A completed wave is fresh evidence: its wall
// time sets the rate at which the next wave fits inside the margin, and the
// ceiling moves toward it, down at once, up at most doubling per wave, and
// released (full speed) only when the target reaches the boosted rate. A
// full-speed request is never issued while a wave's evidence is stale.
const (
	// PaceFullTicksPerSecond is the boosted rate native's player pacing
	// climbs to (150 x Normal); a ceiling at or above it is full speed.
	PaceFullTicksPerSecond uint32 = 9000
	// PaceFloorTicksPerSecond is Normal: where stale evidence parks the
	// window. The window keeps advancing, it only slows.
	PaceFloorTicksPerSecond uint32 = 60
	// PaceReleaseTicksPerSecond is the wire's largest ceiling: the request
	// that releases a lowered ceiling.
	PaceReleaseTicksPerSecond uint32 = 60000
	// paceMargin is the share of the horizon a critical wave may span.
	paceMargin = 0.5
	// DefaultPaceHorizonTicks is the safe horizon without a configured
	// one: the pawn/emergency fact tolerance planning already holds
	// (#583's blind-tick budget).
	DefaultPaceHorizonTicks domain.Tick = 300
)

// paceBackoff is one scheduler's ceiling state. request issues an owned
// speed change carrying the ceiling (PaceReleaseTicksPerSecond releases);
// its error leaves the recorded ceiling as it was, so the next decision
// retries.
type paceBackoff struct {
	mu      sync.Mutex
	horizon domain.Tick
	now     func() time.Time
	request func(ctx context.Context, ceiling uint32) error
	// ceiling is the rate the window was last set to, 0 for none (full).
	ceiling uint32
	// stale is set while a watched wave's evidence has aged past the
	// margin; cleared when the wave completes.
	stale bool
	// requests counts issued changes; stale ones those issued while stale.
	requests, released int
}

func newPaceBackoff(horizon domain.Tick, now func() time.Time, request func(context.Context, uint32) error) *paceBackoff {
	if horizon <= 0 {
		horizon = DefaultPaceHorizonTicks
	}
	return &paceBackoff{horizon: horizon, now: now, request: request}
}

// Ceiling is the ceiling a new window starts under: the rate the last
// waves earned, so recovery from a stop resumes it instead of restarting
// at full speed and backing off again. 0 is none.
func (b *paceBackoff) Ceiling() uint32 {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.ceiling
}

// Reset forgets the ceiling (a new game session).
func (b *paceBackoff) Reset() {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.ceiling, b.stale = 0, false
}

// worstRate is the fastest the window may run now. Callers hold mu.
func (b *paceBackoff) worstRate() uint32 {
	if b.ceiling == 0 {
		return PaceFullTicksPerSecond
	}
	return b.ceiling
}

// Watch arms the backoff for one critical wave whose evidence was read at
// readAt; ctx scopes the requests the watch issues. The returned done
// reports the wave's end: completed with its wall time, or not (cut off,
// failed), which leaves the stale decision standing.
func (b *paceBackoff) Watch(ctx context.Context, readAt time.Time) (done func(wave time.Duration, completed bool)) {
	b.mu.Lock()
	rate := b.worstRate()
	b.mu.Unlock()
	budget := time.Duration(float64(b.horizon) * paceMargin / float64(rate) * float64(time.Second))
	fire := budget - b.now().Sub(readAt)
	var once sync.Once
	stop := make(chan struct{})
	finished := make(chan struct{})
	go func() {
		defer close(finished)
		timer := time.NewTimer(max(fire, 0))
		defer timer.Stop()
		select {
		case <-stop:
			return
		case <-ctx.Done():
			return
		case <-timer.C:
		}
		b.markStale(ctx)
	}()
	return func(wave time.Duration, completed bool) {
		once.Do(func() {
			close(stop)
			<-finished
			if completed {
				b.fresh(ctx, wave)
			}
		})
	}
}

// markStale lowers the ceiling to the floor: the wave outlived the margin.
func (b *paceBackoff) markStale(ctx context.Context) {
	b.mu.Lock()
	b.stale = true
	if b.ceiling != 0 && b.ceiling <= PaceFloorTicksPerSecond {
		b.mu.Unlock()
		return
	}
	b.mu.Unlock()
	b.set(ctx, PaceFloorTicksPerSecond, "stale")
}

// fresh moves the ceiling toward the rate at which a wave of this wall
// time fits inside the margin.
func (b *paceBackoff) fresh(ctx context.Context, wave time.Duration) {
	b.mu.Lock()
	b.stale = false
	current := b.ceiling
	b.mu.Unlock()
	wave = max(wave, time.Millisecond)
	target := uint32(min(float64(PaceReleaseTicksPerSecond), float64(b.horizon)*paceMargin/wave.Seconds()))
	target = max(target, PaceFloorTicksPerSecond)
	switch {
	case current == 0:
		if target < PaceFullTicksPerSecond {
			b.set(ctx, target, "wave")
		}
	case target < current:
		b.set(ctx, target, "wave")
	case target > current:
		next := min(target, 2*current)
		if next >= PaceFullTicksPerSecond {
			b.set(ctx, 0, "fresh")
			return
		}
		if next > current {
			b.set(ctx, next, "fresh")
		}
	}
}

// set issues the change (0 releases) and records it once native took it.
// A release is refused outright while any wave's evidence is stale.
func (b *paceBackoff) set(ctx context.Context, ceiling uint32, why string) {
	b.mu.Lock()
	if ceiling == 0 && b.stale {
		b.mu.Unlock()
		return
	}
	b.mu.Unlock()
	wire := ceiling
	if wire == 0 {
		wire = PaceReleaseTicksPerSecond
	}
	err := b.request(ctx, wire)
	slog.Default().Log(ctx, slog.LevelInfo, "pace backoff", telemetry.ComponentKey, "clock-scheduler", telemetry.KindKey, "pace_backoff",
		"ceiling", wire, "why", why, "horizon_ticks", int64(b.horizon), "err", err)
	if err != nil {
		return
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	b.ceiling = ceiling
	b.requests++
	if ceiling == 0 {
		b.released++
	}
}

// requestCeiling is the backoff's request: an owned speed change on the
// running epoch the last watched wave saw, keeping its speed and carrying
// the ceiling. It shares the renewal gate, which also serializes the
// journal's request sequence against the renewal's.
func (s *ClockScheduler) requestCeiling(ctx context.Context, ceiling uint32) error {
	original := s.paceEpoch.Load()
	if original == nil {
		return executor.ErrHeld
	}
	select {
	case s.renewGate <- struct{}{}:
	case <-ctx.Done():
		return ctx.Err()
	}
	defer func() { <-s.renewGate }()
	state := s.session.State()
	if !state.Enabled || !state.ObservationKnown {
		return executor.ErrAuthority
	}
	sequence, err := s.player.journal.ReadClockSequence(ctx)
	if err != nil {
		return err
	}
	id, err := sequence.NextRequestID()
	if err != nil {
		return err
	}
	intent := store.ClockIntent{RequestID: id, Snapshot: state.Snapshot, Command: bridge.ClockCommand{Speed: &bridge.ClockSpeed{Original: proto.Clone(original).(*k.Epoch), Speed: original.GetRequestedSpeed(), MaxTicksPerSecond: &ceiling}}}
	result, err := s.session.CommandClock(ctx, intent)
	if err == nil && result.Phase != store.ClockApplied {
		err = executor.ErrHeld
	}
	return err
}

// StepPacing is the pace a step's clock status reported (#627): the
// running epoch's pacing reason, the rate it holds and its ceiling, and
// native's effective speed (ticks per wall second, pauses included).
type StepPacing struct {
	Reason       string
	PacedTPS     uint32
	Ceiling      uint32
	EffectiveTPS float64
	Player       bool
}

func stepPacing(status *k.Status) StepPacing {
	out := StepPacing{EffectiveTPS: status.GetEffectiveTicksPerSecond()}
	if epoch := status.GetRunning().GetEpoch(); epoch != nil {
		out.PacedTPS, out.Ceiling = epoch.GetPacedTicksPerSecond(), epoch.GetMaxTicksPerSecond()
		out.Player = epoch.GetPacing() == k.Pacing_PACING_PLAYER_ACCELERATED
		if epoch.PacingReason != nil {
			out.Reason = PacingReasonName(epoch.GetPacingReason())
		}
	}
	return out
}

// PacingReasonName is the wire reason without its enum prefix, lower case:
// "frame_budget", "ceiling", ...
func PacingReasonName(reason k.PacingReason) string {
	return strings.ToLower(strings.TrimPrefix(reason.String(), "PACING_REASON_"))
}

// publish adds the pace to a clock_step row: what the dashboard's pacing
// reason and effective speed read.
func (p StepPacing) publish(extra map[string]any) {
	if p.Reason != "" {
		extra["pacing_reason"] = p.Reason
		extra["paced_tps"] = p.PacedTPS
	}
	if p.Ceiling != 0 {
		extra["tps_ceiling"] = p.Ceiling
	}
	if p.Player {
		extra["player_pacing"] = true
	}
	if p.EffectiveTPS > 0 {
		extra["effective_tps"] = p.EffectiveTPS
	}
}
