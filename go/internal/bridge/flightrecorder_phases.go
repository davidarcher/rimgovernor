package bridge

import (
	"encoding/json"
	"fmt"
	"io"
	"math"
	"sort"
	"strings"
)

// ToolPhases aggregates the recorded call phases for one native tool.
// Milliseconds are summed over Calls; divide for means. NativeQueueMs and
// NativeExecuteMs are the companion's own split of the round trip (main
// thread wait, tool body), summed over the NativeTimed calls whose reply
// carried it; a companion that predates the field leaves NativeTimed at 0,
// which the report shows as absent rather than zero. NativeTool is the
// inner rimgovernor/* method for games_call_tool and games_tool_detail rows,
// so describe round trips for a method appear under the same name with
// Wrapper "games_tool_detail". CacheHits are reads of the method the
// scheduler's per-step cache served without a round trip (bridge.StepReadCache);
// they are not counted in Calls.
type ToolPhases struct {
	NativeTool      string  `json:"native_tool"`
	Wrapper         string  `json:"wrapper"`
	Calls           uint64  `json:"calls"`
	CacheHits       uint64  `json:"cache_hits"`
	Errors          uint64  `json:"errors"`
	GateWaitMs      float64 `json:"gate_wait_ms"`
	CallMs          float64 `json:"call_ms"`
	DecodeMs        float64 `json:"decode_ms"`
	ProtoDecodeMs   float64 `json:"proto_decode_ms"`
	TotalMs         float64 `json:"total_ms"`
	ResponseBytes   uint64  `json:"response_bytes"`
	NativeTimed     uint64  `json:"native_timed"`
	NativeQueueMs   float64 `json:"native_queue_ms"`
	NativeExecuteMs float64 `json:"native_execute_ms"`
}

// PhaseSummary is a read-only aggregation of one flight-recorder timeline:
// per-tool phase totals plus the game-clock progress visible in observation
// replies. It changes nothing in the game and needs no authority.
type PhaseSummary struct {
	Records   uint64         `json:"records"`
	Gaps      uint64         `json:"gaps"`
	Untimed   uint64         `json:"untimed_calls"`
	WallSecs  float64        `json:"wall_seconds"`
	Tools     []ToolPhases   `json:"tools"`
	Clock     ClockSample    `json:"clock"`
	Steps     StepSample     `json:"steps"`
	Dispatch  DispatchSample `json:"dispatch"`
	FirstWall float64        `json:"first_wall_time"`
	LastWall  float64        `json:"last_wall_time"`
}

// StepSample aggregates the "clock_step" rows a ClockScheduler step publishes
// from its ReadTally: how many steps the timeline covers, the native round
// trips they issued in total and at most, the reads the step cache served
// instead, the reads the cross-step FactCache served (ParentHits), and the
// round trips per tool summed over all steps (divide by Steps for a
// per-step mean), plus the colony windows the steps sized by wall time
// (issue #126): how many steps reached the admission tail (Windows), the
// ticks they asked for in total and at most, and the largest wall target;
// the steps by the reason they acted on (timer, wake, settled, full) and
// the clock stops the wake steps answered (issue #112); and the
// stop-to-readmit pauses the admitting steps closed (issue #162): how many
// admissions followed a stopped window, the wall seconds those pauses
// summed to and the longest. Rows are absent when the controller ran
// without a scheduler, leaving Steps at 0.
type StepSample struct {
	Steps          uint64            `json:"steps"`
	Reads          uint64            `json:"reads"`
	MaxReads       uint64            `json:"max_reads"`
	CacheHits      uint64            `json:"cache_hits"`
	ParentHits     uint64            `json:"parent_hits"`
	Tools          map[string]uint64 `json:"tools,omitempty"`
	Windows        uint64            `json:"windows"`
	WindowTicks    uint64            `json:"window_ticks"`
	MaxWindowTicks uint64            `json:"max_window_ticks"`
	MaxWindowSecs  float64           `json:"max_window_target_secs"`
	Pauses         uint64            `json:"pauses"`
	PauseSecs      float64           `json:"pause_seconds"`
	MaxPauseSecs   float64           `json:"max_pause_seconds"`
	Reasons        map[string]uint64 `json:"reasons,omitempty"`
	Stops          StopSample        `json:"stops"`
}

// DispatchSample aggregates the "worker_dispatch" rows the routine Worker
// publishes for each run that reached native (#243): how many there were,
// how many began while the scheduler's window was running (Live), how many
// left a refused receipt (Refused, LiveRefused of those live), and the
// receipts by kind. RefusedFraction is Refused over Calls.
type DispatchSample struct {
	Calls       uint64            `json:"calls"`
	Live        uint64            `json:"live"`
	Refused     uint64            `json:"refused"`
	LiveRefused uint64            `json:"live_refused"`
	Receipts    map[string]uint64 `json:"receipts,omitempty"`
}

// RefusedFraction is the share of the Worker's native runs that native
// refused, or 0 without any.
func (d DispatchSample) RefusedFraction() float64 {
	if d.Calls == 0 {
		return 0
	}
	return float64(d.Refused) / float64(d.Calls)
}

// StopSample counts the clock_step rows a committed clock stop woke
// (payload "stop") and their stop latency: the wall time from the native
// stop stamp (the Stopped event's observed_at) to the step that acted on
// it, over the rows that carried "stop_latency_ms" (LatencySamples; a
// negative sample, clock skew, is dropped).
type StopSample struct {
	Count          uint64  `json:"count"`
	LatencySamples uint64  `json:"latency_samples"`
	MeanLatencyMs  float64 `json:"mean_latency_ms"`
	MaxLatencyMs   float64 `json:"max_latency_ms"`
}

// ClockSample is wall TPS derived from the observation-context ticks carried
// by native replies, excluding intervals where the tick went backwards
// (load, rewind, map change), plus the paused state the clock status
// samples report: as a count of samples and weighted by the wall time each
// sample stood for (until the next one). The count over-represents pauses,
// when the service issues most of its reads; the time-weighted fraction
// (PausedSecs / SampledSecs) is the share of the sampled wall time the
// clock was paused. Wall TPS includes paused time by construction.
type ClockSample struct {
	TickSamples   uint64  `json:"tick_samples"`
	TicksAdvanced int64   `json:"ticks_advanced"`
	WallSecs      float64 `json:"wall_seconds"`
	WallTPS       float64 `json:"wall_tps"`
	Resets        uint64  `json:"resets"`
	PausedSamples uint64  `json:"paused_samples"`
	ClockSamples  uint64  `json:"clock_samples"`
	PausedSecs    float64 `json:"paused_seconds"`
	SampledSecs   float64 `json:"sampled_seconds"`
}

// PausedFraction is the time-weighted share of the sampled wall time the
// clock was paused, or the sample count ratio when no sample spans time.
func (c ClockSample) PausedFraction() float64 {
	if c.SampledSecs > 0 {
		return c.PausedSecs / c.SampledSecs
	}
	if c.ClockSamples > 0 {
		return float64(c.PausedSamples) / float64(c.ClockSamples)
	}
	return 0
}

// SummarizePhases aggregates rows produced by Client (native_response,
// native_error, native_decode, native_cache_hit). Rows recorded before phase timing existed
// count as Untimed and contribute only to Calls.
func SummarizePhases(records []TimelineRecord) PhaseSummary {
	summary := PhaseSummary{}
	tools := map[string]*ToolPhases{}
	requestTool := map[uint64]string{}
	var lastTick int64
	haveTick := false
	var tickWall float64
	var pausedWall float64
	var lastPaused, havePaused bool
	var stopLatencyMs float64
	for _, row := range records {
		summary.Records++
		if row.Kind == "recording_gap" {
			summary.Gaps++
			continue
		}
		if summary.FirstWall == 0 || row.WallTime < summary.FirstWall {
			summary.FirstWall = row.WallTime
		}
		if row.WallTime > summary.LastWall {
			summary.LastWall = row.WallTime
		}
		switch row.Kind {
		case "native_response", "native_error":
			key, entry := phaseEntry(tools, row)
			entry.Calls++
			if row.Kind == "native_error" {
				entry.Errors++
			}
			if request, ok := number(row.Payload["request"]); ok {
				requestTool[uint64(request)] = key
			}
			timing, ok := row.Payload["timing"].(map[string]any)
			if !ok {
				summary.Untimed++
			} else {
				entry.GateWaitMs += field(timing, "gate_wait_ms")
				entry.CallMs += field(timing, "call_ms")
				entry.DecodeMs += field(timing, "decode_ms")
				entry.TotalMs += field(timing, "total_ms")
				entry.ResponseBytes += uint64(field(timing, "response_bytes"))
				if queue, ok := number(timing["native_queue_ms"]); ok {
					entry.NativeTimed++
					entry.NativeQueueMs += queue
					entry.NativeExecuteMs += field(timing, "native_execute_ms")
				}
			}
			if row.Kind != "native_response" || entry.Wrapper != "games_call_tool" {
				continue
			}
			tick, paused, hasTick, hasPaused := replyClock(row.Payload["result"])
			if hasPaused {
				summary.Clock.ClockSamples++
				if paused {
					summary.Clock.PausedSamples++
				}
				if havePaused && row.WallTime > pausedWall {
					span := row.WallTime - pausedWall
					summary.Clock.SampledSecs += span
					if lastPaused {
						summary.Clock.PausedSecs += span
					}
				}
				lastPaused, pausedWall, havePaused = paused, row.WallTime, true
			}
			if !hasTick {
				continue
			}
			summary.Clock.TickSamples++
			if haveTick {
				if tick < lastTick {
					summary.Clock.Resets++
				} else {
					summary.Clock.TicksAdvanced += tick - lastTick
					summary.Clock.WallSecs += row.WallTime - tickWall
				}
			}
			lastTick, tickWall, haveTick = tick, row.WallTime, true
		case "native_cache_hit":
			_, entry := phaseEntry(tools, row)
			entry.CacheHits++
		case "clock_step":
			steps := &summary.Steps
			steps.Steps++
			reads := uint64(field(row.Payload, "reads"))
			steps.Reads += reads
			steps.MaxReads = max(steps.MaxReads, reads)
			steps.CacheHits += uint64(field(row.Payload, "cache_hits"))
			steps.ParentHits += uint64(field(row.Payload, "parent_hits"))
			if ticks := uint64(field(row.Payload, "window_ticks")); ticks > 0 {
				steps.Windows++
				steps.WindowTicks += ticks
				steps.MaxWindowTicks = max(steps.MaxWindowTicks, ticks)
				steps.MaxWindowSecs = math.Max(steps.MaxWindowSecs, field(row.Payload, "window_target_s"))
			}
			if pause := field(row.Payload, "stop_pause_s"); pause > 0 {
				steps.Pauses++
				steps.PauseSecs += pause
				steps.MaxPauseSecs = math.Max(steps.MaxPauseSecs, pause)
			}
			if tools, ok := row.Payload["tools"].(map[string]any); ok {
				if steps.Tools == nil {
					steps.Tools = map[string]uint64{}
				}
				for tool := range tools {
					steps.Tools[tool] += uint64(field(tools, tool))
				}
			}
			if reason, ok := row.Payload["reason"].(string); ok && reason != "" {
				if steps.Reasons == nil {
					steps.Reasons = map[string]uint64{}
				}
				steps.Reasons[reason]++
			}
			if stop, _ := row.Payload["stop"].(bool); stop {
				steps.Stops.Count++
				if latency, ok := number(row.Payload["stop_latency_ms"]); ok && latency >= 0 {
					steps.Stops.LatencySamples++
					stopLatencyMs += latency
					steps.Stops.MaxLatencyMs = math.Max(steps.Stops.MaxLatencyMs, latency)
				}
			}
		case "worker_dispatch":
			d := &summary.Dispatch
			d.Calls++
			running, _ := row.Payload["running"].(bool)
			receipt, _ := row.Payload["receipt"].(string)
			if running {
				d.Live++
			}
			if receipt == "refused" {
				d.Refused++
				if running {
					d.LiveRefused++
				}
			}
			if receipt != "" {
				if d.Receipts == nil {
					d.Receipts = map[string]uint64{}
				}
				d.Receipts[receipt]++
			}
		case "native_decode":
			request, ok := number(row.Payload["request"])
			if !ok {
				continue
			}
			key, known := requestTool[uint64(request)]
			if !known {
				continue
			}
			tools[key].ProtoDecodeMs += field(row.Payload, "proto_decode_ms")
		}
	}
	for _, entry := range tools {
		summary.Tools = append(summary.Tools, *entry)
	}
	sort.Slice(summary.Tools, func(i, j int) bool {
		if summary.Tools[i].TotalMs != summary.Tools[j].TotalMs {
			return summary.Tools[i].TotalMs > summary.Tools[j].TotalMs
		}
		return summary.Tools[i].NativeTool+summary.Tools[i].Wrapper < summary.Tools[j].NativeTool+summary.Tools[j].Wrapper
	})
	summary.WallSecs = summary.LastWall - summary.FirstWall
	if summary.Steps.Stops.LatencySamples > 0 {
		summary.Steps.Stops.MeanLatencyMs = stopLatencyMs / float64(summary.Steps.Stops.LatencySamples)
	}
	if summary.Clock.WallSecs > 0 {
		summary.Clock.WallTPS = float64(summary.Clock.TicksAdvanced) / summary.Clock.WallSecs
	}
	return summary
}

func phaseEntry(tools map[string]*ToolPhases, row TimelineRecord) (string, *ToolPhases) {
	wrapper, _ := row.Payload["tool"].(string)
	native, _ := row.Payload["native_tool"].(string)
	if native == "" {
		native = wrapper
	}
	key := wrapper + "\x00" + native
	entry := tools[key]
	if entry == nil {
		entry = &ToolPhases{NativeTool: native, Wrapper: wrapper}
		tools[key] = entry
	}
	return key, entry
}

func field(m map[string]any, key string) float64 {
	value, _ := number(m[key])
	return value
}

func number(value any) (float64, bool) {
	switch v := value.(type) {
	case float64:
		return v, true
	case json.Number:
		f, err := v.Float64()
		return f, err == nil
	default:
		return 0, false
	}
}

// replyClock reads the observation tick and, for clock status replies (or a
// bundle's clock status section), the actual-paused flag out of a recorded
// games_call_tool result. The recorded
// result is the ProtoBoundary wrapper {"payload": "<ProtoJSON>"}; the tick
// lives at <reply>.<outcome>.context.tick for observation replies and at
// <reply>.status.context.tick for clock status.
func replyClock(result any) (tick int64, paused bool, hasTick bool, hasPaused bool) {
	wrapper, ok := result.(map[string]any)
	if !ok {
		return
	}
	payload, ok := wrapper["payload"].(string)
	if !ok || len(payload) > maxProtoBytes {
		return
	}
	var reply map[string]any
	if json.Unmarshal([]byte(payload), &reply) != nil {
		return
	}
	for _, outcome := range reply {
		body, ok := outcome.(map[string]any)
		if !ok {
			continue
		}
		if context, ok := body["context"].(map[string]any); ok {
			if value, ok := tickValue(context["tick"]); ok {
				tick, hasTick = value, true
			}
		}
		if value, ok := statusPaused(body); ok {
			paused, hasPaused = value, true
		}
		// A bundle carries the clock status as a section; a control
		// receipt's applied outcome carries the status it applied.
		if status, ok := body["clockStatus"].(map[string]any); ok {
			if value, ok := statusPaused(status); ok {
				paused, hasPaused = value, true
			}
		}
		if applied, ok := body["applied"].(map[string]any); ok {
			if status, ok := applied["status"].(map[string]any); ok {
				if value, ok := statusPaused(status); ok {
					paused, hasPaused = value, true
				}
			}
		}
	}
	return
}

// statusPaused reads whether a recorded clock status shows the clock paused:
// its actual-paused flag when present, else its running/stopped state. A
// start's applied status is the one sample taken while a window runs, so
// counting it keeps the time-weighted paused share honest under polls that
// only return once the window has stopped (issue #162).
func statusPaused(status map[string]any) (bool, bool) {
	if value, ok := status["actualPaused"].(bool); ok {
		return value, true
	}
	if _, ok := status["running"].(map[string]any); ok {
		return false, true
	}
	if _, ok := status["stopped"].(map[string]any); ok {
		return true, true
	}
	return false, false
}

// tickValue accepts ProtoJSON int64 encodings: a JSON string or a number.
func tickValue(value any) (int64, bool) {
	switch v := value.(type) {
	case string:
		var tick int64
		if _, err := fmt.Sscan(v, &tick); err != nil {
			return 0, false
		}
		return tick, true
	case float64:
		return int64(v), true
	default:
		return 0, false
	}
}

// WritePhaseReport prints a PhaseSummary as an aligned table with per-call
// means, the format the rimgovernor phases subcommand shows by default.
func WritePhaseReport(w io.Writer, summary PhaseSummary) {
	fmt.Fprintf(w, "records %d (gaps %d, untimed calls %d), wall %.1fs\n", summary.Records, summary.Gaps, summary.Untimed, summary.WallSecs)
	clock := summary.Clock
	if clock.TickSamples > 0 {
		fmt.Fprintf(w, "clock: %d ticks over %.1fs = %.1f wall TPS (%d tick samples, %d resets)", clock.TicksAdvanced, clock.WallSecs, clock.WallTPS, clock.TickSamples, clock.Resets)
		if clock.ClockSamples > 0 {
			fmt.Fprintf(w, ", paused %d/%d status samples (%.0f%% of %.1fs sampled)", clock.PausedSamples, clock.ClockSamples, 100*clock.PausedFraction(), clock.SampledSecs)
		}
		fmt.Fprintln(w)
	}
	if steps := summary.Steps; steps.Steps > 0 {
		fmt.Fprintf(w, "steps: %d, reads/step mean %.1f max %d, cache hits/step %.1f, parent hits/step %.1f", steps.Steps, float64(steps.Reads)/float64(steps.Steps), steps.MaxReads, float64(steps.CacheHits)/float64(steps.Steps), float64(steps.ParentHits)/float64(steps.Steps))
		if steps.Windows > 0 {
			fmt.Fprintf(w, ", window ticks mean %.0f max %d (target up to %.1fs) over %d sized steps", float64(steps.WindowTicks)/float64(steps.Windows), steps.MaxWindowTicks, steps.MaxWindowSecs, steps.Windows)
		}
		if steps.Pauses > 0 {
			fmt.Fprintf(w, ", stop-to-readmit pause mean %.2fs max %.2fs over %d admissions", steps.PauseSecs/float64(steps.Pauses), steps.MaxPauseSecs, steps.Pauses)
		}
		if len(steps.Reasons) > 0 {
			reasons := make([]string, 0, len(steps.Reasons))
			for reason := range steps.Reasons {
				reasons = append(reasons, reason)
			}
			sort.Strings(reasons)
			fmt.Fprint(w, ", by reason")
			for _, reason := range reasons {
				fmt.Fprintf(w, " %s=%d", reason, steps.Reasons[reason])
			}
		}
		if stops := steps.Stops; stops.Count > 0 {
			fmt.Fprintf(w, "\nstops: %d woke a step", stops.Count)
			if stops.LatencySamples > 0 {
				fmt.Fprintf(w, ", stop->step latency mean %.1fms max %.1fms over %d samples", stops.MeanLatencyMs, stops.MaxLatencyMs, stops.LatencySamples)
			}
		}
		names := make([]string, 0, len(steps.Tools))
		for tool := range steps.Tools {
			names = append(names, tool)
		}
		sort.Slice(names, func(i, j int) bool {
			if steps.Tools[names[i]] != steps.Tools[names[j]] {
				return steps.Tools[names[i]] > steps.Tools[names[j]]
			}
			return names[i] < names[j]
		})
		for _, tool := range names {
			fmt.Fprintf(w, "\n  %-52s %6.1f", tool, float64(steps.Tools[tool])/float64(steps.Steps))
		}
		fmt.Fprintln(w)
	}
	fmt.Fprintf(w, "%-52s %-17s %6s %6s %4s %9s %9s %9s %9s %9s %9s %9s %9s\n", "native tool", "wrapper", "calls", "cached", "err", "total ms", "gate ms", "call ms", "queue ms", "exec ms", "decode ms", "proto ms", "avg KiB")
	for _, tool := range summary.Tools {
		calls := float64(tool.Calls)
		if calls == 0 {
			calls = 1
		}
		wrapper := strings.TrimPrefix(tool.Wrapper, "games_")
		// The native split is a mean over the calls that reported it; "-"
		// marks a tool whose replies never carried timing (older
		// companion, multi-hop media capture), not a zero.
		queue, execute := "-", "-"
		if tool.NativeTimed > 0 {
			queue = fmt.Sprintf("%.1f", tool.NativeQueueMs/float64(tool.NativeTimed))
			execute = fmt.Sprintf("%.1f", tool.NativeExecuteMs/float64(tool.NativeTimed))
		}
		fmt.Fprintf(w, "%-52s %-17s %6d %6d %4d %9.1f %9.1f %9.1f %9s %9s %9.2f %9.2f %9.1f\n", tool.NativeTool, wrapper, tool.Calls, tool.CacheHits, tool.Errors,
			tool.TotalMs/calls, tool.GateWaitMs/calls, tool.CallMs/calls, queue, execute, tool.DecodeMs/calls, tool.ProtoDecodeMs/calls, float64(tool.ResponseBytes)/calls/1024)
	}
}
