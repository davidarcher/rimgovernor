package buildingruntime

import (
	"context"
	"slices"
	"strings"
	"time"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// combatStopEvents is the armed list a combat window carries (#849):
// native stops the epoch on the tick one of these happens. Aim warmup,
// shots and ordinary damage are never armed; stopping on them would keep
// the clock from running. A window arms them only while an admitted fight
// plan owns the combat (#852, armedCombatStops); the stop then re-invokes
// the defense planner, whose DecideCombat answers it.
var combatStopEvents = []k.CombatEvent{
	k.CombatEvent_COMBAT_EVENT_DOWNED,
	k.CombatEvent_COMBAT_EVENT_SERIOUS_INJURY,
	k.CombatEvent_COMBAT_EVENT_SHIELD_BROKEN,
	k.CombatEvent_COMBAT_EVENT_ENTERED_RANGE,
	k.CombatEvent_COMBAT_EVENT_MELEE_CONTACT,
	k.CombatEvent_COMBAT_EVENT_EXPLOSIVE_LAUNCHED,
	k.CombatEvent_COMBAT_EVENT_RAID_PHASE,
	k.CombatEvent_COMBAT_EVENT_HOSTILE_ARRIVED,
	k.CombatEvent_COMBAT_EVENT_BREACH,
	k.CombatEvent_COMBAT_EVENT_MENTAL_BREAK,
	k.CombatEvent_COMBAT_EVENT_PRISON_BREAK,
}

// combatStopMetrics measures one combat's stops for the service log
// (#849): stops by event kind (the tick budget is the backstop), the wall
// latency from a stop to the next window's admission, and the ticks
// between stops. A combat is the run of consecutive combat windows; its
// summary is logged when a colony window follows. Touched only under the
// player gate.
type combatStopMetrics struct {
	active       bool
	byKind       map[string]int
	latencies    []time.Duration
	tickGaps     []int64
	lastStopTick int64
	lastKnown    bool
}

// combatStopKind names a stop for the metrics: the armed combat event, or
// the stop reason (tick_budget is the backstop).
func combatStopKind(stopped *k.Stopped) string {
	if e := stopped.GetCombatEvent(); e != k.CombatEvent_COMBAT_EVENT_UNSPECIFIED {
		return strings.ToLower(strings.TrimPrefix(e.String(), "COMBAT_EVENT_"))
	}
	return strings.ToLower(strings.TrimPrefix(stopped.GetReason().String(), "STOP_REASON_"))
}

// admitted records a window admission: stopped is the status the window
// resumes from (nil when the clock was not stopped), tick its tick, now
// the admission's wall time and combat the admitted window's mode.
func (m *combatStopMetrics) admitted(ctx context.Context, stopped *k.Stopped, tick int64, now time.Time, combat bool) {
	if m.active && stopped != nil {
		kind := combatStopKind(stopped)
		if m.byKind == nil {
			m.byKind = map[string]int{}
		}
		m.byKind[kind]++
		attrs := []any{"event", kind, "tick", tick}
		// The stop that ends the combat resumes into a colony window after
		// the full review; its latency is not a combat reaction (#890).
		if at := stopped.GetStoppedAtUnixMs(); at > 0 && combat {
			latency := now.Sub(time.UnixMilli(at))
			m.latencies = append(m.latencies, latency)
			attrs = append(attrs, "resume_latency_ms", latency.Milliseconds())
		}
		if m.lastKnown {
			m.tickGaps = append(m.tickGaps, tick-m.lastStopTick)
			attrs = append(attrs, "ticks_since_stop", tick-m.lastStopTick)
		}
		m.lastStopTick, m.lastKnown = tick, true
		clockEvent(ctx, "clock-scheduler", "combat_stop", "combat window stopped", attrs...)
	}
	if m.active && !combat {
		m.log(ctx)
		*m = combatStopMetrics{}
	}
	if combat && !m.active {
		m.active, m.lastStopTick, m.lastKnown = true, tick, true
	}
}

func (m *combatStopMetrics) log(ctx context.Context) {
	stops := 0
	for _, n := range m.byKind {
		stops += n
	}
	clockEvent(ctx, "clock-scheduler", "combat_stops", "combat ended",
		"stops", stops, "by_event", m.byKind,
		"resume_latency_p50_ms", percentile(m.latencies, 50).Milliseconds(),
		"resume_latency_p95_ms", percentile(m.latencies, 95).Milliseconds(),
		"ticks_between_stops_p50", percentile(m.tickGaps, 50),
		"ticks_between_stops_p95", percentile(m.tickGaps, 95))
}

// percentile is the nearest-rank percentile of values, zero when empty.
func percentile[T time.Duration | int64](values []T, p int) T {
	if len(values) == 0 {
		var zero T
		return zero
	}
	sorted := slices.Clone(values)
	slices.Sort(sorted)
	rank := (p*len(sorted) + 99) / 100
	return sorted[max(rank, 1)-1]
}
