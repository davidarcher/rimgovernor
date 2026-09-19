package nativeaccept

import (
	"context"
	"fmt"
	"os"
	"strings"
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

// PauseCause is why the game stopped ticking under RunUntil (#353): what
// home/status showed when a probe found the game paused although the
// harness asked for RunSpeed. Letters are the stack's rows (id, label,
// letterDef), Windows the open windows that force a pause (type, title).
// A wait fails with it at once instead of burning its stall budget.
type PauseCause struct {
	Label       string
	Tick        uint64
	ForcePaused bool
	Letters     []map[string]any
	Windows     []map[string]any
	Status      map[string]any
}

func (c *PauseCause) Error() string {
	var parts []string
	for _, letter := range c.Letters {
		parts = append(parts, fmt.Sprintf("letter %q (%s)", AsString(letter["label"]), AsString(letter["letterDef"])))
	}
	for _, window := range c.Windows {
		parts = append(parts, fmt.Sprintf("window %s %q", AsString(window["type"]), AsString(window["title"])))
	}
	cause := strings.Join(parts, ", ")
	if cause == "" {
		cause = "no letter or force-pausing window on home/status"
	}
	return fmt.Sprintf("%s: game paused at tick %d while running (forcePaused=%t): %s", c.Label, c.Tick, c.ForcePaused, cause)
}

// resolvePause is RunUntil's answer to a probe that found the game paused:
// it reads home/status and either clears the cause and resumes RunSpeed,
// or returns the *PauseCause classifyPause found.
func (h *Harness) resolvePause(ctx context.Context, label string, tick uint64) error {
	status, err := h.Call(ctx, label+"-paused", "home/status", map[string]any{"colonists": false, "threats": false})
	if err != nil {
		return err
	}
	cause, dismiss := classifyPause(status, h.Tools, label, tick)
	if cause != nil {
		return cause
	}
	for _, letter := range dismiss {
		reply, err := h.Call(ctx, label+"-dismiss", DismissLetterTool, map[string]any{"letterId": letter["id"]})
		if err != nil {
			return err
		}
		if removed, _ := AsBool(reply["removed"]); !removed {
			return fmt.Errorf("%s: letter %v was not on the stack: %#v", label, letter["id"], reply)
		}
	}
	if len(dismiss) == 0 {
		return nil
	}
	_, err = h.Call(ctx, label+"-resume", "rimworld/set_time_speed", map[string]any{"speed": RunSpeed, "ultraSpeedBoost": RunBoost})
	return err
}

// classifyPause decides what a paused home/status means for a running
// wait. A status that shows the game running again is a transient (nil,
// nil). A stack of letters that are all in AcknowledgedLetterDefs is
// returned to dismiss (DismissLetterTool, when tools carries it), as the
// service acknowledges them, since a bridge-only case has no routine layer
// to do it. A force-pausing window, any other letter, or a pause with no
// visible cause is the *PauseCause that fails the wait.
func classifyPause(status map[string]any, tools []string, label string, tick uint64) (cause *PauseCause, dismiss []map[string]any) {
	timeStatus, _ := AsMap(status["time"])
	if paused, _ := AsBool(timeStatus["paused"]); !paused {
		return nil, nil
	}
	cause = &PauseCause{Label: label, Tick: tick, Status: status}
	cause.ForcePaused, _ = AsBool(timeStatus["forcePaused"])
	for _, raw := range AsSlice(status["letters"]) {
		if row, ok := AsMap(raw); ok {
			cause.Letters = append(cause.Letters, map[string]any{"id": row["id"], "label": row["label"], "letterDef": row["letterDef"]})
		}
	}
	ui, _ := AsMap(status["ui"])
	for _, raw := range AsSlice(ui["windows"]) {
		if row, ok := AsMap(raw); ok {
			if force, _ := AsBool(row["forcePause"]); force {
				cause.Windows = append(cause.Windows, map[string]any{"type": row["type"], "title": row["title"]})
			}
		}
	}
	if len(cause.Windows) > 0 || len(cause.Letters) == 0 || !Contains(tools, DismissLetterTool) {
		return cause, nil
	}
	for _, letter := range cause.Letters {
		if !AcknowledgedLetterDefs[AsString(letter["letterDef"])] {
			return cause, nil
		}
	}
	return nil, cause.Letters
}

// RunUntil runs the game at RunSpeed and polls probe until it is done, then
// pauses; ticks is the game-time budget and w's own bounds still apply
// (Interval defaults to 2s, Tick is set here). It returns how many ticks
// the game advanced. The probe's signature may be empty: the tick is
// always part of the signature, so a game that stops ticking stalls. A
// game found paused is resolved before the probe (resolvePause): benign
// letters are acknowledged and the run resumed, anything else fails the
// wait at once with a *PauseCause.
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
	var paused bool
	w.Ticks = ticks
	w.Tick = func(ctx context.Context) (uint64, error) {
		loaded, err := readLoaded(ctx, h, "tick")
		if err != nil {
			return 0, err
		}
		loadedContext, _ := AsMap(loaded["context"])
		latest = uint64(AsNumber(loadedContext["tick"]))
		paused, _ = AsBool(loaded["paused"])
		return latest, nil
	}
	first := uint64(0)
	haveFirst := false
	err := WaitProgress(ctx, w, func(ctx context.Context) (string, bool, error) {
		if !haveFirst {
			first, haveFirst = latest, true
		}
		if paused {
			if err := h.resolvePause(ctx, label, latest); err != nil {
				return "", false, err
			}
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
// rimgovernor serve (--clock-speed); the default is Ultrafast (#265). The
// clock wire admits Normal, Fast, Superfast and Ultrafast. Ultrafast also
// asks for test acceleration (the native dev tick boost), which only a
// headless.Prepare launch admits: under a rendered profile native refuses
// the window. Live steps keep dispatching at that pace because the
// scheduler widens the planning tolerance by the running window's
// measured pace (domain.LiveDrift, #345). A case that wants the game held
// to a slower pace opts out through this variable.
const ClockSpeedEnv = "RIMGOVERNOR_ACCEPT_CLOCK_SPEED"

// ClockSpeed is ClockSpeedEnv or Ultrafast.
func ClockSpeed() string {
	if v := os.Getenv(ClockSpeedEnv); v != "" {
		return v
	}
	return "Ultrafast"
}

// ClockSpeedArgs is the serve flag set for ClockSpeed: --clock-speed, plus
// --clock-test-acceleration at Ultrafast.
func ClockSpeedArgs() []string { return ClockSpeedFlags(ClockSpeed()) }

// ClockSpeedFlags is ClockSpeedArgs for an explicit speed.
func ClockSpeedFlags(speed string) []string {
	args := []string{"--clock-speed", speed}
	if speed == "Ultrafast" {
		args = append(args, "--clock-test-acceleration")
	}
	return args
}
