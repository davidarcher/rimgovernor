package nativeaccept

import (
	"context"
	"fmt"
	"os"
	"time"
)

// RunSpeed and RunBoost are how a harness runs the game while it waits for
// the game to do something (issue #91): Ultrafast with RimWorld's dev
// tick boost, which drops the per-frame tick cap. Measured on the debug
// colony: Fast 168 ticks/s, Superfast 348, Ultrafast 899, boosted ~7000,
// so a wait stated in ticks costs the least wall clock. Neither needs
// Prefs.devMode (which would add a 35s def check to every boot). Waits
// bounded in ticks mean the same at any speed, so nothing about the
// assertion depends on it.
const (
	RunSpeed = "Ultrafast"
	RunBoost = true
)

// RunInterval is RunUntil's default pause between probes.
const RunInterval = 250 * time.Millisecond

// Tick reads the current game tick through lifecycle_read_identity; an
// unloaded game is an error.
func (h *Harness) Tick(ctx context.Context) (uint64, error) {
	reply, err := h.Wire(ctx, "tick", "lifecycle_read_identity", map[string]any{})
	if err != nil {
		return 0, err
	}
	_, loaded, err := Outcome(reply, "loaded")
	if err != nil {
		return 0, err
	}
	loadedContext, _ := AsMap(loaded["context"])
	return uint64(AsNumber(loadedContext["tick"])), nil
}

// RunUntil runs the game at RunSpeed and polls probe until it is done, then
// pauses; ticks is the game-time budget and w's own bounds still apply
// (Interval defaults to 2s, Tick is set here). It returns how many ticks
// the game advanced. The probe's signature may be empty: the tick is
// always part of the signature, so a game that stops ticking stalls.
func RunUntil(ctx context.Context, h *Harness, label string, ticks uint64, w Wait, probe Probe) (uint64, error) {
	if ticks == 0 {
		return 0, fmt.Errorf("%s: a tick budget is required", label)
	}
	if _, err := h.Call(ctx, label+"-run", "rimworld/set_time_speed", map[string]any{"speed": RunSpeed, "ultraSpeedBoost": RunBoost}); err != nil {
		return 0, err
	}
	// A probe is a ~100ms round trip and the game runs thousands of ticks
	// a second here, so WaitProgress's 2s default is most of a short
	// harness's run in overshoot; poll often instead.
	if w.Interval <= 0 {
		w.Interval = RunInterval
	}
	var latest uint64
	w.Ticks = ticks
	w.Tick = func(ctx context.Context) (uint64, error) {
		tick, err := h.Tick(ctx)
		latest = tick
		return tick, err
	}
	first := uint64(0)
	haveFirst := false
	err := WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		if !haveFirst {
			first, haveFirst = latest, true
		}
		signature, done, err := probe(ctx)
		return Signature(latest, signature), done, err
	})
	pauseCtx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if _, pauseErr := h.Call(pauseCtx, label+"-pause", "rimworld/set_time_speed", map[string]any{"speed": "Paused", "ultraSpeedBoost": false}); pauseErr != nil && err == nil {
		err = pauseErr
	}
	elapsed := uint64(0)
	if latest > first {
		elapsed = latest - first
	}
	return elapsed, err
}

// ObserveCompleted runs the game (RunUntil) until every attempt is observed
// Completed through receipts_observe_progress, within ticks of game time,
// and returns each attempt's completed body in order. An attempt observed
// Unsuccessful fails the wait at once.
func ObserveCompleted(ctx context.Context, h *Harness, label string, ticks uint64, attempts ...map[string]any) ([]map[string]any, error) {
	completed := make([]map[string]any, len(attempts))
	_, err := RunUntil(ctx, h, label, ticks, Wait{Stall: StallBudget()}, func(ctx context.Context) (string, bool, error) {
		pending := 0
		for i, attempt := range attempts {
			if completed[i] != nil {
				continue
			}
			reply, err := h.Wire(ctx, fmt.Sprintf("%s-observe-%d", label, i), "receipts_observe_progress", attempt)
			if err != nil {
				return "", false, err
			}
			_, progress, err := Outcome(reply, "progress")
			if err != nil {
				return "", false, err
			}
			if unsuccessful, ok := AsMap(progress["unsuccessful"]); ok {
				return "", false, fmt.Errorf("%s: attempt %d became unsuccessful before completion: %#v", label, i, unsuccessful)
			}
			if body, ok := AsMap(progress["completed"]); ok {
				completed[i] = body
				continue
			}
			pending++
		}
		return Signature(len(attempts) - pending), pending == 0, nil
	})
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	return completed, nil
}

// ClockSpeedEnv overrides the clock speed a serve-driven harness passes to
// rimgovernor serve (--clock-speed); the default is Fast. The clock wire
// admits Normal, Fast and Superfast only.
const ClockSpeedEnv = "RIMGOVERNOR_ACCEPT_CLOCK_SPEED"

// ClockSpeed is ClockSpeedEnv or Fast.
func ClockSpeed() string {
	if v := os.Getenv(ClockSpeedEnv); v != "" {
		return v
	}
	return "Fast"
}
