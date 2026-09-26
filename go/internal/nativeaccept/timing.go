package nativeaccept

import (
	"encoding/json"
	"fmt"
	"strings"
	"sync"
	"time"
)

// Every result.json carries the run's timing so a slower harness is visible
// without inferring runtimes from evidence mtimes (#134): NewReport stamps
// started_at, Finalize adds finished_at, wall_ms, boot_ms (from OpenSession
// or game_reuse.openMs), ticks_advanced (the game ticks the harness observed
// pass, see observeTick) and wall_tps, and fails a passing run whose wall
// time exceeds its budget_ms.

// StartedAtKey .. TicksAdvancedKey name the timing fields on a Report.
const (
	StartedAtKey     = "started_at"
	FinishedAtKey    = "finished_at"
	WallMsKey        = "wall_ms"
	BootMsKey        = "boot_ms"
	BudgetMsKey      = "budget_ms"
	TicksAdvancedKey = "ticks_advanced"
	WallTPSKey       = "wall_tps"
)

// SetBudget records the report's budget (budget_ms): the wall-clock budget
// a passing run fails on when its wall time exceeds it, separate from the
// -timeout safety net. The case runner sets it from the case's Budget (the
// checklist's item 6) or its -budget override; a report without one is
// not held to any.
func (r Report) SetBudget(budget time.Duration) { r[BudgetMsKey] = budget.Milliseconds() }

// finalizeTiming fills the timing fields at Finalize and applies the
// budget: a passing report over budget fails with budget_exceeded.
func (r Report) finalizeTiming(finished time.Time) {
	r[FinishedAtKey] = finished.UTC().Format(time.RFC3339Nano)
	started, ok := parseReportTime(r[StartedAtKey])
	if !ok {
		return
	}
	wall := finished.Sub(started)
	r[WallMsKey] = wall.Milliseconds()
	if _, has := r[BootMsKey]; !has {
		if reuse, ok := AsMap(r["game_reuse"]); ok {
			if ms, ok := asUint64(reuse["openMs"]); ok {
				r[BootMsKey] = int64(ms)
			}
		}
	}
	advanced, ok := asUint64(r[TicksAdvancedKey])
	if !ok {
		advanced = TicksAdvanced()
		r[TicksAdvancedKey] = advanced
	}
	if _, has := r[WallTPSKey]; !has && wall > 0 {
		r[WallTPSKey] = float64(advanced) / wall.Seconds()
	}
	var budget time.Duration
	if ms, ok := asUint64(r[BudgetMsKey]); ok {
		budget = time.Duration(ms) * time.Millisecond
	}
	if budget > 0 && wall > budget {
		r["budget_exceeded"] = true
		if passed, _ := r["passed"].(bool); passed {
			r["passed"] = false
			r["error"] = fmt.Sprintf("run exceeded its budget: %s > %s", wall.Round(time.Millisecond), budget)
		}
	}
}

func parseReportTime(value any) (time.Time, bool) {
	text, _ := value.(string)
	if text == "" {
		return time.Time{}, false
	}
	t, err := time.Parse(time.RFC3339Nano, text)
	return t, err == nil
}

// tickStats accumulates the game ticks the process has seen pass: every
// Harness reply that carries the game tick (an identity read, home/status,
// home/colony_facts, the supervised clock) feeds observeTick, which sums
// the forward deltas between consecutive observations. A load
// (resetTickBaseline) starts a new baseline so a save's tick is not
// counted as progress, and a rewind only re-baselines.
var tickStats struct {
	mu       sync.Mutex
	seen     bool
	last     uint64
	advanced uint64
}

func observeTick(tick uint64) {
	tickStats.mu.Lock()
	defer tickStats.mu.Unlock()
	if tickStats.seen && tick > tickStats.last {
		tickStats.advanced += tick - tickStats.last
	}
	tickStats.seen, tickStats.last = true, tick
}

func resetTickBaseline() {
	tickStats.mu.Lock()
	defer tickStats.mu.Unlock()
	tickStats.seen = false
}

// ResetTickStats empties the process's tick count so a runner that
// executes several cases in one process reports each case's own.
func ResetTickStats() {
	tickStats.mu.Lock()
	defer tickStats.mu.Unlock()
	tickStats.seen, tickStats.last, tickStats.advanced = false, 0, 0
}

// TicksAdvanced is the game ticks observed to pass so far.
func TicksAdvanced() uint64 {
	tickStats.mu.Lock()
	defer tickStats.mu.Unlock()
	return tickStats.advanced
}

// observeReplyTick feeds observeTick with the tick a reply carried
// (replyTick) and re-baselines after a load or start.
func observeReplyTick(tool string, tick *uint64) {
	switch tool {
	case "rimworld/load_game_ready", "rimworld/start_debug_game_ready", "rimgovernor/lifecycle_load":
		resetTickBaseline()
		return
	}
	if tick != nil {
		observeTick(*tick)
	}
}

// replyTick is the game tick a decoded reply carries, when it does:
// home/status, home/colony_facts, and any
// rimgovernor/* ProtoJSON reply whose payload is a lifecycle "loaded"
// context (an identity read, a load, an authority acquisition).
func replyTick(tool string, payload map[string]any) (uint64, bool) {
	switch tool {
	case "home/status":
		if t, ok := AsMap(payload["time"]); ok {
			return asUint64(t["ticksGame"])
		}
	case "home/colony_facts":
		return asUint64(payload["tick"])
	}
	if !strings.HasPrefix(tool, "rimgovernor/") {
		return 0, false
	}
	encoded, ok := payload["payload"].(string)
	if !ok {
		return 0, false
	}
	var message map[string]any
	if err := json.Unmarshal([]byte(encoded), &message); err != nil {
		return 0, false
	}
	return wireTick(message)
}

// wireTick is a decoded lifecycle reply's loaded.context.tick.
func wireTick(message map[string]any) (uint64, bool) {
	if loaded, ok := AsMap(message["loaded"]); ok {
		if context, ok := AsMap(loaded["context"]); ok {
			return asUint64(context["tick"])
		}
	}
	return 0, false
}

// asUint64 reads a non-negative integer as Go, JSON or ProtoJSON (string)
// carries it.
func asUint64(value any) (uint64, bool) {
	switch v := value.(type) {
	case uint64:
		return v, true
	case int64:
		return uint64(v), v >= 0
	case int:
		return uint64(v), v >= 0
	case float64:
		return uint64(v), v >= 0 && v == float64(uint64(v))
	case string:
		var n uint64
		_, err := fmt.Sscan(v, &n)
		return n, err == nil
	}
	return 0, false
}
