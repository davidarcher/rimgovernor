package bridge

import (
	"encoding/json"
	"fmt"
	"io"
	"math"
	"sort"
	"strings"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
)

// liveStepReason is the clock_step "reason" a step that planned under a
// running window carries (buildingruntime.StepLive); this package cannot
// import buildingruntime, which records the rows.
const liveStepReason = "live"

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
	// The companion's split of the execute leg (#642), summed over the
	// ObservationHops whose reply carried the observation account: reading
	// game state, then ProtoJSON formatting and its UTF-8 size checks, the
	// formatting passes and the payload bytes they produced. Zero
	// ObservationHops means absent, not free. FormatMs is always formatting
	// on the game thread; a detached reply's worker formatting (#644) is
	// EncodeMs over EncodeHops, outside the execute leg.
	ObservationHops uint64  `json:"observation_hops"`
	CaptureMs       float64 `json:"capture_ms"`
	FormatMs        float64 `json:"format_ms"`
	FormatPasses    uint64  `json:"format_passes"`
	PayloadBytes    uint64  `json:"payload_bytes"`
	EncodeHops      uint64  `json:"encode_hops,omitempty"`
	EncodeMs        float64 `json:"encode_ms,omitempty"`
}

// PhaseSummary is a read-only aggregation of one flight-recorder timeline:
// per-tool phase totals plus the game-clock progress visible in observation
// replies. It changes nothing in the game and needs no authority.
type PhaseSummary struct {
	Records  uint64         `json:"records"`
	Gaps     uint64         `json:"gaps"`
	Untimed  uint64         `json:"untimed_calls"`
	WallSecs float64        `json:"wall_seconds"`
	Tools    []ToolPhases   `json:"tools"`
	Clock    ClockSample    `json:"clock"`
	Steps    StepSample     `json:"steps"`
	Dispatch DispatchSample `json:"dispatch"`
	// The observation capture account and the frame recorder (#642). Both
	// are unknown rather than zero against a companion without them:
	// Observation.Hops and Frames.Samples say whether anything was read.
	Observation ObservationSample `json:"observation"`
	Frames      FrameSample       `json:"frames"`
	FirstWall   float64           `json:"first_wall_time"`
	LastWall    float64           `json:"last_wall_time"`
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
	Pauses         uint64            `json:"pauses"`
	PauseSecs      float64           `json:"pause_seconds"`
	MaxPauseSecs   float64           `json:"max_pause_seconds"`
	Reasons        map[string]uint64 `json:"reasons,omitempty"`
	Stops          StopSample        `json:"stops"`
	// JournalMs sums the steps' journal_ms, the wall time each step's own
	// obligation reads spent in the journal (#634); MaxJournalMs is the
	// slowest step's.
	JournalMs    float64 `json:"journal_ms"`
	MaxJournalMs float64 `json:"max_journal_ms"`
	// The step's own wall time (clock_step "elapsed_ms") summed and at
	// most, and the wait for the player gate before it began
	// ("gate_wait_ms", #593): a step whose wall is mostly gate wait was
	// queued behind the Worker's dispatch step, not slow itself.
	ElapsedMs     float64 `json:"elapsed_ms"`
	MaxElapsedMs  float64 `json:"max_elapsed_ms"`
	GateWaitMs    float64 `json:"gate_wait_ms"`
	MaxGateWaitMs float64 `json:"max_gate_wait_ms"`
	// The live steps alone (reason "live": planning under a running
	// window, the steps whose cost bounds throughput at speed, #593).
	// Cold and stopped steps read whole families and are not comparable.
	LiveSteps        uint64  `json:"live_steps"`
	LiveReads        uint64  `json:"live_reads"`
	MaxLiveReads     uint64  `json:"max_live_reads"`
	LiveElapsedMs    float64 `json:"live_elapsed_ms"`
	MaxLiveElapsedMs float64 `json:"max_live_elapsed_ms"`
}

// LiveReadsPerStep is the native round trips a live step issued on average,
// the reads-per-step measure of #593; 0 without a live step.
func (s StepSample) LiveReadsPerStep() float64 {
	if s.LiveSteps == 0 {
		return 0
	}
	return float64(s.LiveReads) / float64(s.LiveSteps)
}

// LiveStepMs is the mean wall time of a live step; 0 without one.
func (s StepSample) LiveStepMs() float64 {
	if s.LiveSteps == 0 {
		return 0
	}
	return s.LiveElapsedMs / float64(s.LiveSteps)
}

// StepMs is the mean wall time of a step of any reason; 0 without one.
func (s StepSample) StepMs() float64 {
	if s.Steps == 0 {
		return 0
	}
	return s.ElapsedMs / float64(s.Steps)
}

// DispatchSample aggregates the "worker_dispatch" rows the routine Worker
// publishes for each run that reached native (#243): how many there were,
// how many began while the scheduler's window was running (Live), how many
// left a refused receipt (Refused, LiveRefused of those live), how many
// were held on stale facts before dispatch (StaleHolds, #624) and the
// receipts by kind. RefusedFraction is Refused over Calls.
type DispatchSample struct {
	Calls       uint64            `json:"calls"`
	Live        uint64            `json:"live"`
	Refused     uint64            `json:"refused"`
	LiveRefused uint64            `json:"live_refused"`
	StaleHolds  uint64            `json:"stale_holds"`
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
	Count uint64 `json:"count"`
	// Orders counts the coupled orders that became ready under a running
	// window (clock_step "coupled_orders") and Coupled the steps that
	// ended the window for them ("coupled_stop"). Native journals such a
	// stop as the controller's own cleanup, so the step row is the only
	// place a coupled stop is distinguishable; a ready order with no stop
	// was prepared live (#584).
	Coupled        uint64  `json:"coupled"`
	Orders         uint64  `json:"coupled_orders"`
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
	TickSamples   uint64 `json:"tick_samples"`
	TicksAdvanced int64  `json:"ticks_advanced"`
	// LastTick is the tick of the newest sample, the game time the service
	// last observed; a harness waiting out a game-time budget under a
	// running window reads it, since the routine review's tick only moves
	// once per full step (#244).
	LastTick      int64   `json:"last_tick"`
	WallSecs      float64 `json:"wall_seconds"`
	WallTPS       float64 `json:"wall_tps"`
	Resets        uint64  `json:"resets"`
	PausedSamples uint64  `json:"paused_samples"`
	ClockSamples  uint64  `json:"clock_samples"`
	PausedSecs    float64 `json:"paused_seconds"`
	SampledSecs   float64 `json:"sampled_seconds"`
	// NativePausedMs and NativeRunningMs are native's own account of the
	// wall time between the first and last status samples (issue #621):
	// the supervisor's stop-to-start gaps and its epochs' running time,
	// measured on native's monotonic clock across every transition,
	// polled or not. The sample ratio above is a sampling diagnostic;
	// PausedFractionNative is the paused share to report.
	NativePausedMs     uint64 `json:"native_paused_ms"`
	NativeRunningMs    uint64 `json:"native_running_ms"`
	NativePauseSamples uint64 `json:"native_pause_samples"`
	// NativeProbeMs and NativeDigestMs split the supervisor's main-thread
	// time between the same first and last status samples (#626): the
	// hazard probe and the fact-change digests, with the passes each
	// took; NativeMaxProbeTickGap is the widest tick gap between
	// consecutive probes the session reported and NativeHazardGaps the
	// per-class detection gap and declared bound from the last sample.
	NativeProbeMs         float64     `json:"native_probe_ms"`
	NativeDigestMs        float64     `json:"native_digest_ms"`
	NativeProbes          uint64      `json:"native_probes"`
	NativeDigests         uint64      `json:"native_digests"`
	NativeMaxProbeTickGap int64       `json:"native_max_probe_tick_gap"`
	NativeHazardGaps      []HazardGap `json:"native_hazard_gaps,omitempty"`
	// Player acceleration's frame account (#627) between the same
	// samples: frames paced, those over the frame budget, and the widest
	// frame's tick work the session reported.
	NativePacedFrames     uint64  `json:"native_paced_frames,omitempty"`
	NativePacedOverBudget uint64  `json:"native_paced_over_budget,omitempty"`
	NativeMaxPacedFrameMs float64 `json:"native_max_paced_frame_ms,omitempty"`
}

// HazardGap is one hazard class's detection gap as the native clock status
// reports it (#626): the declared bound in ticks, the widest gap observed
// this game session and whether a direct game hook backs the class.
type HazardGap struct {
	HazardClass string `json:"hazard_class"`
	BoundTicks  int64  `json:"bound_ticks"`
	MaxTickGap  int64  `json:"max_tick_gap"`
	Hooked      bool   `json:"hooked"`
}

// DigestShare is the digests' share of the supervisor's probe-path time,
// NativeDigestMs / (NativeProbeMs + NativeDigestMs), or 0 with nothing
// accounted.
func (c ClockSample) DigestShare() float64 {
	total := c.NativeProbeMs + c.NativeDigestMs
	if total <= 0 {
		return 0
	}
	return c.NativeDigestMs / total
}

// PausedFractionNative is native's paused share of the accounted wall
// time, NativePausedMs / (NativePausedMs + NativeRunningMs), or 0 without
// any accounted time.
func (c ClockSample) PausedFractionNative() float64 {
	return NativePausedFraction(c.NativePausedMs, c.NativeRunningMs)
}

// NativePausedFraction is paused / (paused + running), or 0 for nothing
// accounted.
func NativePausedFraction(pausedMs, runningMs uint64) float64 {
	if pausedMs+runningMs == 0 {
		return 0
	}
	return float64(pausedMs) / float64(pausedMs+runningMs)
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
	var firstPause, lastPause nativePause
	var havePause bool
	observation := &observationAccumulator{}
	frames := &frameAccumulator{}
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
					execute := field(timing, "native_execute_ms")
					entry.NativeExecuteMs += execute
					observation.timed(queue, execute)
				}
				if account, ok := readObservation(timing); ok {
					entry.ObservationHops++
					entry.CaptureMs += account.captureMs
					entry.FormatMs += account.formatMs
					entry.FormatPasses += account.formatPasses
					entry.PayloadBytes += account.payloadBytes
					if account.detached {
						entry.EncodeHops++
						entry.EncodeMs += account.encodeMs
					}
					observation.hop(account)
				}
				if account, ok := readFrames(timing); ok {
					frames.sample(account)
				}
			}
			if row.Kind != "native_response" || entry.Wrapper != "games_call_tool" {
				continue
			}
			tick, paused, hasTick, hasPaused, pause, hasPause := replyClock(row.Payload["result"])
			if hasPause {
				summary.Clock.NativePauseSamples++
				if !havePause {
					firstPause, havePause = pause, true
				}
				lastPause = pause
			}
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
			summary.Clock.LastTick = tick
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
			}
			if journal := field(row.Payload, "journal_ms"); journal > 0 {
				steps.JournalMs += journal
				steps.MaxJournalMs = math.Max(steps.MaxJournalMs, journal)
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
			elapsed := field(row.Payload, "elapsed_ms")
			steps.ElapsedMs += elapsed
			steps.MaxElapsedMs = math.Max(steps.MaxElapsedMs, elapsed)
			if gate := field(row.Payload, "gate_wait_ms"); gate > 0 {
				steps.GateWaitMs += gate
				steps.MaxGateWaitMs = math.Max(steps.MaxGateWaitMs, gate)
			}
			if reason, ok := row.Payload["reason"].(string); ok && reason != "" {
				if steps.Reasons == nil {
					steps.Reasons = map[string]uint64{}
				}
				steps.Reasons[reason]++
				// The live steps carry the cost that bounds throughput at
				// speed (#593); keep their reads and wall apart from the
				// cold and stopped steps, which read whole families.
				if reason == liveStepReason {
					steps.LiveSteps++
					steps.LiveReads += reads
					steps.MaxLiveReads = max(steps.MaxLiveReads, reads)
					steps.LiveElapsedMs += elapsed
					steps.MaxLiveElapsedMs = math.Max(steps.MaxLiveElapsedMs, elapsed)
				}
			}
			if orders := uint64(field(row.Payload, "coupled_orders")); orders > 0 {
				steps.Stops.Orders += orders
				if coupled, _ := row.Payload["coupled_stop"].(bool); coupled {
					steps.Stops.Coupled++
				}
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
			if stale, _ := row.Payload["stale"].(bool); stale {
				d.StaleHolds++
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
	if havePause && lastPause.paused >= firstPause.paused && lastPause.running >= firstPause.running {
		summary.Clock.NativePausedMs = lastPause.paused - firstPause.paused
		summary.Clock.NativeRunningMs = lastPause.running - firstPause.running
	}
	if havePause && lastPause.probeMs >= firstPause.probeMs && lastPause.digestMs >= firstPause.digestMs {
		summary.Clock.NativeProbeMs = lastPause.probeMs - firstPause.probeMs
		summary.Clock.NativeDigestMs = lastPause.digestMs - firstPause.digestMs
		if lastPause.probes >= firstPause.probes {
			summary.Clock.NativeProbes = lastPause.probes - firstPause.probes
		}
		if lastPause.digests >= firstPause.digests {
			summary.Clock.NativeDigests = lastPause.digests - firstPause.digests
		}
		summary.Clock.NativeMaxProbeTickGap = lastPause.maxProbeTickGap
		summary.Clock.NativeHazardGaps = lastPause.hazardGaps
	}
	if havePause && lastPause.pacedFrames >= firstPause.pacedFrames && lastPause.overBudget >= firstPause.overBudget {
		summary.Clock.NativePacedFrames = lastPause.pacedFrames - firstPause.pacedFrames
		summary.Clock.NativePacedOverBudget = lastPause.overBudget - firstPause.overBudget
		summary.Clock.NativeMaxPacedFrameMs = lastPause.maxFrameMs
	}
	summary.Observation = observation.result()
	summary.Frames = frames.result()
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
// nativePause is one status sample's native pause account (issue #621):
// cumulative paused and running milliseconds on native's clock.
type nativePause struct {
	paused, running uint64
	// The probe account (#626), cumulative like the pause account.
	probeMs, digestMs float64
	probes, digests   uint64
	maxProbeTickGap   int64
	hazardGaps        []HazardGap
	// The player pacing frame account (#627).
	pacedFrames, overBudget uint64
	maxFrameMs              float64
}

func replyClock(result any) (tick int64, paused bool, hasTick bool, hasPaused bool, pause nativePause, hasPause bool) {
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
	status := func(candidate map[string]any) {
		if value, ok := statusPaused(candidate); ok {
			paused, hasPaused = value, true
		}
		if value, ok := statusPause(candidate); ok {
			pause, hasPause = value, true
		}
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
		status(body)
		// A bundle carries the clock status as a section; a control
		// receipt's applied outcome carries the status it applied.
		if section, ok := body["clockStatus"].(map[string]any); ok {
			status(section)
		}
		if applied, ok := body["applied"].(map[string]any); ok {
			if section, ok := applied["status"].(map[string]any); ok {
				status(section)
			}
		}
	}
	return
}

// statusPause reads a recorded clock status's native pause account
// (pausedMs / runningMs, ProtoJSON uint64 strings), absent on a status from
// a native build without it.
func statusPause(status map[string]any) (nativePause, bool) {
	pausedMs, ok := tickValue(status["pausedMs"])
	if !ok {
		return nativePause{}, false
	}
	runningMs, _ := tickValue(status["runningMs"])
	if pausedMs < 0 || runningMs < 0 {
		return nativePause{}, false
	}
	sample := nativePause{paused: uint64(pausedMs), running: uint64(runningMs)}
	// The probe account (#626) is absent on a native build without it.
	sample.probeMs, _ = number(status["probeElapsedMs"])
	sample.digestMs, _ = number(status["digestElapsedMs"])
	if probes, ok := tickValue(status["probeTotal"]); ok && probes >= 0 {
		sample.probes = uint64(probes)
	}
	if digests, ok := tickValue(status["digestTotal"]); ok && digests >= 0 {
		sample.digests = uint64(digests)
	}
	sample.maxProbeTickGap, _ = tickValue(status["sessionMaxProbeTickGap"])
	if frames, ok := tickValue(status["pacedFrames"]); ok && frames >= 0 {
		sample.pacedFrames = uint64(frames)
	}
	if over, ok := tickValue(status["pacedFramesOverBudget"]); ok && over >= 0 {
		sample.overBudget = uint64(over)
	}
	sample.maxFrameMs, _ = number(status["maxPacedFrameMs"])
	if gaps, ok := status["hazardGaps"].([]any); ok {
		for _, raw := range gaps {
			row, ok := raw.(map[string]any)
			if !ok {
				continue
			}
			gap := HazardGap{HazardClass: fmt.Sprint(row["hazardClass"])}
			gap.BoundTicks, _ = tickValue(row["boundTicks"])
			gap.MaxTickGap, _ = tickValue(row["maxTickGap"])
			gap.Hooked, _ = row["hooked"].(bool)
			sample.hazardGaps = append(sample.hazardGaps, gap)
		}
	}
	return sample, true
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
		if clock.NativePausedMs+clock.NativeRunningMs > 0 {
			fmt.Fprintf(w, ", native paused %.1fs of %.1fs (%.0f%%)", float64(clock.NativePausedMs)/1000, float64(clock.NativePausedMs+clock.NativeRunningMs)/1000, 100*clock.PausedFractionNative())
		}
		fmt.Fprintln(w)
	}
	if steps := summary.Steps; steps.Steps > 0 {
		fmt.Fprintf(w, "steps: %d, reads/step mean %.1f max %d, cache hits/step %.1f, parent hits/step %.1f", steps.Steps, float64(steps.Reads)/float64(steps.Steps), steps.MaxReads, float64(steps.CacheHits)/float64(steps.Steps), float64(steps.ParentHits)/float64(steps.Steps))
		if steps.Windows > 0 {
			fmt.Fprintf(w, ", window ticks mean %.0f max %d over %d sized steps", float64(steps.WindowTicks)/float64(steps.Windows), steps.MaxWindowTicks, steps.Windows)
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
		if steps.ElapsedMs > 0 {
			fmt.Fprintf(w, "\nstep wall: mean %.0fms max %.0fms", steps.StepMs(), steps.MaxElapsedMs)
			if steps.GateWaitMs > 0 {
				fmt.Fprintf(w, ", player-gate wait mean %.0fms max %.0fms", steps.GateWaitMs/float64(steps.Steps), steps.MaxGateWaitMs)
			}
			if steps.LiveSteps > 0 {
				fmt.Fprintf(w, "; %d live steps: reads mean %.1f max %d, wall mean %.0fms max %.0fms", steps.LiveSteps, steps.LiveReadsPerStep(), steps.MaxLiveReads, steps.LiveStepMs(), steps.MaxLiveElapsedMs)
			}
		}
		if stops := steps.Stops; stops.Count > 0 || stops.Coupled > 0 {
			fmt.Fprintf(w, "\nstops: %d woke a step", stops.Count)
			if stops.LatencySamples > 0 {
				fmt.Fprintf(w, ", stop->step latency mean %.1fms max %.1fms over %d samples", stops.MeanLatencyMs, stops.MaxLatencyMs, stops.LatencySamples)
			}
			if stops.Coupled > 0 {
				fmt.Fprintf(w, ", %d raised for %d coupled order(s)", stops.Coupled, stops.Orders)
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
	writeObservationReport(w, summary)
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

// ClockStatusPausedFraction is PausedFractionNative over one typed clock
// status: native's cumulative paused_ms / (paused_ms + running_ms) for the
// loaded session (issue #621). A status from a native build without the
// account reads as 0.
func ClockStatusPausedFraction(status *k.Status) float64 {
	return NativePausedFraction(status.GetPausedMs(), status.GetRunningMs())
}

// writeObservationReport prints the observation capture account and the
// frame recorder (#642). Both sections are omitted entirely when the
// recording carried none of the account, so absent never reads as zero.
func writeObservationReport(w io.Writer, summary PhaseSummary) {
	if obs := summary.Observation; obs.Hops > 0 || obs.Queue.Samples > 0 {
		fmt.Fprintf(w, "observation: %d hops with a capture account", obs.Hops)
		writeQuantiles(w, "capture", obs.Capture)
		writeQuantiles(w, "format", obs.Format)
		writeQuantiles(w, "queue", obs.Queue)
		writeQuantiles(w, "exec", obs.Execute)
		fmt.Fprintln(w)
		if obs.EncodeHops > 0 {
			fmt.Fprintf(w, "  off-thread encode: %d hops", obs.EncodeHops)
			writeQuantiles(w, "queue", obs.EncodeQueue)
			writeQuantiles(w, "encode", obs.Encode)
			fmt.Fprintf(w, ", %d formatting passes (%.1fms)\n", obs.EncodeFormatPasses, obs.EncodeFormatMs)
		}
		if obs.Hops > 0 {
			fmt.Fprintf(w, "  %d formatting passes, %.1f MiB returned", obs.FormatPasses, float64(obs.PayloadBytes)/(1024*1024))
			if obs.Dropped > 0 {
				fmt.Fprintf(w, ", %d sections dropped for the envelope bound", obs.Dropped)
			}
			if len(obs.Outcomes) > 0 {
				outcomes := make([]string, 0, len(obs.Outcomes))
				for outcome := range obs.Outcomes {
					outcomes = append(outcomes, outcome)
				}
				sort.Strings(outcomes)
				fmt.Fprint(w, ", outcomes")
				for _, outcome := range outcomes {
					fmt.Fprintf(w, " %s=%d", outcome, obs.Outcomes[outcome])
				}
			}
			fmt.Fprintln(w)
		}
		if t := obs.Threats; t != nil {
			fmt.Fprintf(w, "  threats: %d scans examined %d pawns, kept %d, projected %d, %d distance scans\n",
				t.Hops, t.Examined, t.Candidates, t.Projections, t.ProximityChecks)
		}
		if len(obs.Sections) > 0 {
			fmt.Fprintf(w, "  %-28s %6s %9s %9s %10s %10s\n", "section", "hops", "ms", "max ms", "rows", "candidates")
			for _, section := range obs.Sections {
				candidates := "-"
				if section.Candidates > 0 {
					candidates = fmt.Sprint(section.Candidates)
				}
				fmt.Fprintf(w, "  %-28s %6d %9.1f %9.1f %10d %10s\n", section.Section, section.Hops, section.Ms, section.MaxMs, section.Rows, candidates)
			}
		}
	}
	frames := summary.Frames
	if frames.Samples == 0 {
		return
	}
	hooked := "no frame hook installed"
	if frames.Hooked {
		hooked = "hooked"
	}
	// Update-to-update intervals, not GPU presentation intervals.
	fmt.Fprintf(w, "frames: %d updates over %.1fs = %.1f/s (%s, %d samples), max update %.1fms",
		frames.Updates, frames.ElapsedMs/1000, frames.UpdatesPerSecond(), hooked, frames.Samples, frames.MaxIntervalMs)
	if frames.ElapsedMs > 0 {
		fmt.Fprintf(w, ", observation %.1fs (%.1f%% of update wall) over %d hops", frames.ObservationMs/1000, 100*frames.ObservationShare(), frames.Observations)
	}
	if frames.Cancelled > 0 {
		fmt.Fprintf(w, ", %d cancelled before running", frames.Cancelled)
	}
	fmt.Fprintf(w, ", recorder %.1fms", frames.RecorderMs)
	for _, bucket := range frames.Slow {
		fmt.Fprintf(w, ", >%.1fms %d", bucket.ThresholdMs, bucket.Count)
	}
	fmt.Fprintln(w)
	for _, worst := range frames.Worst {
		fmt.Fprintf(w, "  worst update %d: %.1fms (observation %.1fms, tick %d", worst.Frame, worst.IntervalMs, worst.ObservationMs, worst.Tick)
		if worst.Trace != "" {
			fmt.Fprintf(w, ", trace %s", worst.Trace)
		}
		fmt.Fprintln(w, ")")
	}
}

// writeQuantiles renders one phase's distribution, or "unknown" when the
// recording carried no sample of it.
func writeQuantiles(w io.Writer, name string, q Quantiles) {
	if q.Samples == 0 {
		fmt.Fprintf(w, ", %s unknown", name)
		return
	}
	fmt.Fprintf(w, ", %s p50 %.1f p95 %.1f p99 %.1f max %.1f ms over %d", name, q.P50, q.P95, q.P99, q.Max, q.Samples)
}
