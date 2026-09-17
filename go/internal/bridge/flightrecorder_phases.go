package bridge

import (
	"encoding/json"
	"fmt"
	"io"
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
	Records   uint64       `json:"records"`
	Gaps      uint64       `json:"gaps"`
	Untimed   uint64       `json:"untimed_calls"`
	WallSecs  float64      `json:"wall_seconds"`
	Tools     []ToolPhases `json:"tools"`
	Clock     ClockSample  `json:"clock"`
	Steps     StepSample   `json:"steps"`
	FirstWall float64      `json:"first_wall_time"`
	LastWall  float64      `json:"last_wall_time"`
}

// StepSample aggregates the "clock_step" rows a ClockScheduler step publishes
// from its ReadTally: how many steps the timeline covers, the native round
// trips they issued in total and at most, the reads the step cache served
// instead, and the round trips per tool summed over all steps (divide by
// Steps for a per-step mean). Rows are absent when the controller ran
// without a scheduler, leaving Steps at 0.
type StepSample struct {
	Steps     uint64            `json:"steps"`
	Reads     uint64            `json:"reads"`
	MaxReads  uint64            `json:"max_reads"`
	CacheHits uint64            `json:"cache_hits"`
	Tools     map[string]uint64 `json:"tools,omitempty"`
}

// ClockSample is wall TPS derived from the observation-context ticks carried
// by native replies, excluding intervals where the tick went backwards
// (load, rewind, map change), plus the paused fraction of clock_read_status
// samples. Wall TPS includes paused time by construction.
type ClockSample struct {
	TickSamples   uint64  `json:"tick_samples"`
	TicksAdvanced int64   `json:"ticks_advanced"`
	WallSecs      float64 `json:"wall_seconds"`
	WallTPS       float64 `json:"wall_tps"`
	Resets        uint64  `json:"resets"`
	PausedSamples uint64  `json:"paused_samples"`
	ClockSamples  uint64  `json:"clock_samples"`
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
			if tools, ok := row.Payload["tools"].(map[string]any); ok {
				if steps.Tools == nil {
					steps.Tools = map[string]uint64{}
				}
				for tool := range tools {
					steps.Tools[tool] += uint64(field(tools, tool))
				}
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

// replyClock reads the observation tick and, for clock status replies, the
// actual-paused flag out of a recorded games_call_tool result. The recorded
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
		if value, ok := body["actualPaused"].(bool); ok {
			paused, hasPaused = value, true
		}
	}
	return
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
			fmt.Fprintf(w, ", paused %d/%d status samples", clock.PausedSamples, clock.ClockSamples)
		}
		fmt.Fprintln(w)
	}
	if steps := summary.Steps; steps.Steps > 0 {
		fmt.Fprintf(w, "steps: %d, reads/step mean %.1f max %d, cache hits/step %.1f", steps.Steps, float64(steps.Reads)/float64(steps.Steps), steps.MaxReads, float64(steps.CacheHits)/float64(steps.Steps))
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
