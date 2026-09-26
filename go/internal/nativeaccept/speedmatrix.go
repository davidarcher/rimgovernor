package nativeaccept

import (
	"encoding/json"
	"fmt"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// SpeedCase is one column of the speed matrix (issue #111): the serve
// --clock-speed flag it runs under and whether the test acceleration (#109)
// is on. "uncapped" is Ultrafast with acceleration; both acceptance profiles
// (headless and rendered) admit it, a player launch does not. "regulated"
// is uncapped under the native blind-tick regulator (#583), the budget in
// BlindTicks: the row that must match the capped speeds' outcome while
// beating their wall time. "governor-off" (#621) is uncapped native play
// with no controller attached, the simulation ceiling on the same save and
// renderer; it builds nothing, so it is outside the outcome comparison.
// "viewer" is uncapped with one dashboard client streaming video at the
// default cadence beside the governor, the viewing overhead. "player" (#627)
// is Ultrafast under player acceleration (--clock-pacing player): no test
// acceleration, native paces ticks per frame against its frame budget and
// the controller backs off before its evidence goes stale, the mode a
// player launch runs.
type SpeedCase struct {
	Name             string `json:"name"`
	Speed            string `json:"speed"`
	TestAcceleration bool   `json:"test_acceleration"`
	BlindTicks       uint   `json:"blind_ticks,omitempty"`
	GovernorOff      bool   `json:"governor_off,omitempty"`
	Viewer           bool   `json:"viewer,omitempty"`
	Player           bool   `json:"player,omitempty"`
	// ObservationLoad (#656) adds ObservationLoadReaders concurrent state
	// readers and a second, stalled video viewer (a consumer that holds its
	// socket and never reads) beside the Viewer row's draining one.
	ObservationLoad bool `json:"observation_load,omitempty"`
}

// Compared reports whether the case's pawn outcome takes part in the
// cross-speed comparison: every governed row does.
func (c SpeedCase) Compared() bool { return !c.GovernorOff }

// DefaultSpeedMatrix is the -speeds default: every native speed plus
// uncapped, regulated, governor-off and viewer.
const DefaultSpeedMatrix = "Normal,Fast,Superfast,Ultrafast,uncapped,regulated,governor-off,viewer"

// RegulatedBlindTicks is the regulated row's budget: the planning fact
// tolerance and the combat window already encode this horizon (#583).
const RegulatedBlindTicks = 300

// ParseSpeedCases turns a comma-separated -speeds value into cases, in order,
// refusing unknown names and repeats.
func ParseSpeedCases(spec string) ([]SpeedCase, error) {
	known := map[string]SpeedCase{
		"normal":           {Name: "Normal", Speed: "Normal"},
		"fast":             {Name: "Fast", Speed: "Fast"},
		"superfast":        {Name: "Superfast", Speed: "Superfast"},
		"ultrafast":        {Name: "Ultrafast", Speed: "Ultrafast"},
		"uncapped":         {Name: "uncapped", Speed: "Ultrafast", TestAcceleration: true},
		"regulated":        {Name: "regulated", Speed: "Ultrafast", TestAcceleration: true, BlindTicks: RegulatedBlindTicks},
		"governor-off":     {Name: "governor-off", Speed: "Ultrafast", TestAcceleration: true, GovernorOff: true},
		"viewer":           {Name: "viewer", Speed: "Ultrafast", TestAcceleration: true, Viewer: true},
		"player":           {Name: "player", Speed: "Ultrafast", Player: true},
		"observation-load": {Name: "observation-load", Speed: "Ultrafast", TestAcceleration: true, Viewer: true, ObservationLoad: true},
	}
	var cases []SpeedCase
	seen := map[string]bool{}
	for _, part := range strings.Split(spec, ",") {
		key := strings.ToLower(strings.TrimSpace(part))
		if key == "" {
			continue
		}
		c, ok := known[key]
		if !ok {
			return nil, fmt.Errorf("unknown speed %q (want Normal, Fast, Superfast, Ultrafast, uncapped, regulated, governor-off, viewer, observation-load or player)", strings.TrimSpace(part))
		}
		if seen[key] {
			return nil, fmt.Errorf("speed %q listed twice", c.Name)
		}
		seen[key] = true
		cases = append(cases, c)
	}
	if len(cases) == 0 {
		return nil, fmt.Errorf("no speeds selected")
	}
	return cases, nil
}

// ServeArgs are the serve flags a case adds: its clock speed, for uncapped
// the test-acceleration opt-in and for regulated the blind-tick budget.
func (c SpeedCase) ServeArgs() []string {
	args := []string{"--clock-speed", c.Speed}
	if c.TestAcceleration {
		args = append(args, "--clock-test-acceleration")
	}
	if c.BlindTicks > 0 {
		args = append(args, "--clock-blind-ticks", strconv.FormatUint(uint64(c.BlindTicks), 10))
	}
	if c.Player {
		args = append(args, "--clock-pacing", "player")
	}
	return args
}

// The player row's bounds (#627). A command queued for the game's main
// thread waits behind at most one frame's tick work, so the frame budget
// holding is the dispatch bound: at most MaxPlayerOverBudgetShare of the
// paced frames may exceed it (the frame that trips the decrease, a long
// single tick). Recovery from a stop resumes the backoff's earned rate
// rather than restarting at full speed, so tick-rate changes stay at most
// MaxPlayerSpeedChangesPer6000 per 6000 ticks.
const (
	MaxPlayerOverBudgetShare     = 0.05
	MaxPlayerSpeedChangesPer6000 = 6
	// PlayerFrameBudgetMS is native's default frame budget, the one the
	// player row runs under (it passes no --clock-frame-budget).
	PlayerFrameBudgetMS = 30
)

// PlayerRow is what the player row's checks read.
type PlayerRow struct {
	HazardGaps              []bridge.HazardGap
	SpeedChanges            int
	Ticks                   uint64
	PacedFrames, OverBudget uint64
	// LastPacingReason is the pacing reason of the last step that found a
	// window running.
	LastPacingReason string
	// Dispatch is the main-thread queue wait of the commands the row's
	// service queued while the clock ran (the observation hops' queueMs):
	// its p95 must land inside the frame budget.
	Dispatch bridge.Quantiles
}

// PlayerRowProblems lists every player-row bound the run broke.
func PlayerRowProblems(row PlayerRow) []string {
	var problems []string
	for _, gap := range row.HazardGaps {
		if gap.BoundTicks > 0 && gap.MaxTickGap > gap.BoundTicks {
			problems = append(problems, fmt.Sprintf("hazard %s: widest detection gap %d ticks past its %d-tick bound", gap.HazardClass, gap.MaxTickGap, gap.BoundTicks))
		}
	}
	if row.PacedFrames == 0 {
		problems = append(problems, "no frame ran under player pacing")
	} else if share := float64(row.OverBudget) / float64(row.PacedFrames); share > MaxPlayerOverBudgetShare {
		problems = append(problems, fmt.Sprintf("%d of %d paced frames (%.1f%%) exceeded the frame budget", row.OverBudget, row.PacedFrames, share*100))
	}
	if row.Dispatch.Samples == 0 {
		problems = append(problems, "no queued command reported its main-thread dispatch wait")
	} else if row.Dispatch.P95 > PlayerFrameBudgetMS {
		problems = append(problems, fmt.Sprintf("queued commands waited %.1fms (p95 over %d) for dispatch, past the %dms frame budget", row.Dispatch.P95, row.Dispatch.Samples, PlayerFrameBudgetMS))
	}
	if row.Ticks > 0 {
		if per := float64(row.SpeedChanges) * 6000 / float64(row.Ticks); per > MaxPlayerSpeedChangesPer6000 {
			problems = append(problems, fmt.Sprintf("%.1f tick-rate changes per 6000 ticks (%d over %d ticks) oscillate past %d", per, row.SpeedChanges, row.Ticks, MaxPlayerSpeedChangesPer6000))
		}
	}
	switch row.LastPacingReason {
	case "accelerated", "frame_budget", "forced_slowdown":
	case "":
		problems = append(problems, "no running step reported a pacing reason")
	default:
		problems = append(problems, fmt.Sprintf("the last running window was held at %q, not the accelerated rate", row.LastPacingReason))
	}
	return problems
}

// LastPacingReason is the pacing reason of the newest clock_step row that
// carries one.
func LastPacingReason(rows []bridge.TimelineRecord) string {
	for i := len(rows) - 1; i >= 0; i-- {
		if rows[i].Kind != "clock_step" {
			continue
		}
		if reason, ok := rows[i].Payload["pacing_reason"].(string); ok && reason != "" {
			return reason
		}
	}
	return ""
}

// SpeedOutcome is what the pawns achieved in one case: the postconditions
// the matrix requires to agree within a tolerance across speeds.
type SpeedOutcome struct {
	Case               string `json:"case"`
	StoredStacks       int    `json:"stored_stacks"`
	StoredUnits        int    `json:"stored_units"`
	LooseUnits         int    `json:"loose_units"`
	WallsBuilt         int    `json:"walls_built"`
	HealthyColonists   int    `json:"healthy_colonists"`
	UnsuccessfulStages int    `json:"unsuccessful_stages"`
}

// OutcomeFromControl reads the counters a test/throughput_control reply
// carries; colonists and plan stages are the harness's own reads.
func OutcomeFromControl(name string, control map[string]any) SpeedOutcome {
	return SpeedOutcome{
		Case:         name,
		StoredStacks: int(AsNumber(control["storedStacks"])),
		StoredUnits:  int(AsNumber(control["storedUnits"])),
		LooseUnits:   int(AsNumber(control["looseUnits"])),
		WallsBuilt:   int(AsNumber(control["wallsBuilt"])),
	}
}

// CompareOutcomes lists every postcondition whose spread across the cases
// exceeds tolerance, plus any case that recorded an unsuccessful plan stage.
// An empty result means the matrix agrees.
func CompareOutcomes(outcomes []SpeedOutcome, tolerance int) []string {
	var problems []string
	fields := []struct {
		name string
		get  func(SpeedOutcome) int
	}{
		{"stored_units", func(o SpeedOutcome) int { return o.StoredUnits }},
		{"stored_stacks", func(o SpeedOutcome) int { return o.StoredStacks }},
		{"walls_built", func(o SpeedOutcome) int { return o.WallsBuilt }},
		{"healthy_colonists", func(o SpeedOutcome) int { return o.HealthyColonists }},
	}
	for _, f := range fields {
		if len(outcomes) == 0 {
			break
		}
		lo, hi := f.get(outcomes[0]), f.get(outcomes[0])
		for _, o := range outcomes[1:] {
			v := f.get(o)
			if v < lo {
				lo = v
			}
			if v > hi {
				hi = v
			}
		}
		if hi-lo > tolerance {
			var parts []string
			for _, o := range outcomes {
				parts = append(parts, fmt.Sprintf("%s=%d", o.Case, f.get(o)))
			}
			problems = append(problems, fmt.Sprintf("%s differs by %d (tolerance %d): %s", f.name, hi-lo, tolerance, strings.Join(parts, " ")))
		}
	}
	for _, o := range outcomes {
		if o.UnsuccessfulStages > 0 {
			problems = append(problems, fmt.Sprintf("%s: %d unsuccessful plan stage(s)", o.Case, o.UnsuccessfulStages))
		}
	}
	return problems
}

// StopSummary classifies the clock stops one serve process observed, read
// back from its flight-recorder timeline: every stopped event a
// clock_read_events reply carried, split into budget stops (the window's
// tick budget ran out) and reactive stops (everything else: a watch latch,
// a letter, a requested pause), with the latency from the companion's
// observed_at stamp to the reply reaching the service.
type StopSummary struct {
	Stops         int            `json:"stops"`
	BudgetStops   int            `json:"budget_stops"`
	ReactiveStops int            `json:"reactive_stops"`
	Reasons       map[string]int `json:"reasons,omitempty"`
	LatencyCount  int            `json:"latency_samples"`
	MeanLatencyMs float64        `json:"mean_latency_ms"`
	MaxLatencyMs  float64        `json:"max_latency_ms"`
	// SpeedChanges counts the SpeedChanged rows the same pages carried:
	// under the blind-tick regulator (#583) its throttle and release
	// transitions, which end no window. MaxBlindTicks is the widest blind
	// span a regulator row reported.
	SpeedChanges  int   `json:"speed_changes"`
	MaxBlindTicks int64 `json:"max_blind_ticks"`
	// Latencies is the per-stop split (#621), in cursor order.
	Latencies []StopLatency `json:"latencies,omitempty"`
}

// StopLatency splits one clock stop's latency (#621) along occurrence ->
// native detection -> stop -> controller observation -> readmit. Ticks come
// from the stop event alone (native's context tick, the tick the stop was
// raised at and, where the hazard carries one, the tick it arose).
// ObserveMs is native's own age of the row when the page carrying it was
// composed (age_at_reply_ms) plus the round trip's transport residual on
// the controller's side (call_ms less native queue and execute), each on
// one process's clock; ReadmitMs is the controller's wall time from that
// reply to the next clock_start receipt. Nothing subtracts one process's
// Unix time from another's; the cursor is the only cross-process key.
type StopLatency struct {
	Cursor         int64    `json:"cursor"`
	Reason         string   `json:"reason"`
	StopTick       int64    `json:"stop_tick"`
	DetectedTick   *int64   `json:"detected_tick,omitempty"`
	OccurrenceTick *int64   `json:"occurrence_tick,omitempty"`
	DetectTicks    *int64   `json:"detect_ticks,omitempty"`
	StopTicks      *int64   `json:"stop_ticks,omitempty"`
	ObserveMs      *float64 `json:"observe_ms,omitempty"`
	ReadmitMs      *float64 `json:"readmit_ms,omitempty"`
}

const (
	clockEventsTool = "rimgovernor/clock_read_events"
	bundleTool      = "rimgovernor/observations_read_bundle"
	clockStartTool  = "rimgovernor/clock_start"
)

// SummarizeStops scans the timeline's clock_read_events replies and the
// bundle replies that carry an events page (the service's poll since
// issue #127; a scan keyed on the events tool alone reported no stops
// against a bundle-polling service). Events are
// keyed by cursor so a page re-read after a hold counts once. Events
// observed before sinceUnixMs are skipped: the native event journal
// survives a reload in the same process, so a service's first page carries
// the previous cases' stops too. Zero keeps every event.
func SummarizeStops(rows []bridge.TimelineRecord, sinceUnixMs int64) StopSummary {
	summary := StopSummary{Reasons: map[string]int{}}
	seen := map[string]bool{}
	var total float64
	// carried is the wall time of the reply that carried each stop, for
	// the readmit leg; starts the wall time of each clock_start receipt.
	carried := map[int]float64{}
	var starts []float64
	for _, row := range rows {
		if row.Kind != "native_response" {
			continue
		}
		tool, _ := row.Payload["native_tool"].(string)
		if tool == clockStartTool {
			if wrapper, ok := row.Payload["result"].(map[string]any); ok {
				if payload, ok := wrapper["payload"].(string); ok && strings.Contains(payload, `"receipt"`) {
					starts = append(starts, row.WallTime)
				}
			}
			continue
		}
		if tool != clockEventsTool && tool != bundleTool {
			continue
		}
		wrapper, ok := row.Payload["result"].(map[string]any)
		if !ok {
			continue
		}
		payload, ok := wrapper["payload"].(string)
		if !ok {
			continue
		}
		var reply any
		if json.Unmarshal([]byte(payload), &reply) != nil {
			continue
		}
		for _, event := range findEvents(reply) {
			observed, hasObserved := int64Value(event["observedAtUnixMs"])
			if sinceUnixMs > 0 && (!hasObserved || observed < sinceUnixMs) {
				continue
			}
			key := fmt.Sprint(event["cursor"])
			if event["cursor"] == nil {
				key = fmt.Sprintf("row-%d-%v", row.Sequence, event["observedAtUnixMs"])
			}
			if changed, ok := event["speedChanged"].(map[string]any); ok {
				if seen[key] {
					continue
				}
				seen[key] = true
				summary.SpeedChanges++
				if blind, ok := int64Value(changed["blindTicks"]); ok && blind > summary.MaxBlindTicks {
					summary.MaxBlindTicks = blind
				}
				continue
			}
			stopped, ok := event["stopped"].(map[string]any)
			if !ok {
				continue
			}
			if seen[key] {
				continue
			}
			seen[key] = true
			reason, _ := stopped["reason"].(string)
			if reason == "" {
				reason = "STOP_REASON_UNSPECIFIED"
			}
			summary.Stops++
			summary.Reasons[reason]++
			if reason == "STOP_REASON_TICK_BUDGET" {
				summary.BudgetStops++
			} else {
				summary.ReactiveStops++
			}
			carried[len(summary.Latencies)] = row.WallTime
			summary.Latencies = append(summary.Latencies, stopLatency(event, stopped, reason, row))
			if hasObserved && row.WallTime > 0 {
				latency := row.WallTime*1000 - float64(observed)
				if latency >= 0 {
					summary.LatencyCount++
					total += latency
					if latency > summary.MaxLatencyMs {
						summary.MaxLatencyMs = latency
					}
				}
			}
		}
	}
	if summary.LatencyCount > 0 {
		summary.MeanLatencyMs = total / float64(summary.LatencyCount)
	}
	for i := range summary.Latencies {
		at := carried[i]
		for _, start := range starts {
			if start > at {
				readmit := (start - at) * 1000
				summary.Latencies[i].ReadmitMs = &readmit
				break
			}
		}
	}
	if len(summary.Reasons) == 0 {
		summary.Reasons = nil
	}
	return summary
}

// stopLatency derives one stop's tick and observation legs from its event
// and the reply row that carried it; the readmit leg is filled in once the
// scan has seen the starts that followed.
func stopLatency(event, stopped map[string]any, reason string, row bridge.TimelineRecord) StopLatency {
	out := StopLatency{Cursor: 0, Reason: reason}
	out.Cursor, _ = int64Value(event["cursor"])
	if context, ok := event["context"].(map[string]any); ok {
		out.StopTick, _ = int64Value(context["tick"])
	}
	if detected, ok := int64Value(stopped["detectedTick"]); ok {
		out.DetectedTick = &detected
		stopTicks := out.StopTick - detected
		out.StopTicks = &stopTicks
		if occurrence, ok := int64Value(stopped["occurrenceTick"]); ok {
			out.OccurrenceTick = &occurrence
			detectTicks := detected - occurrence
			out.DetectTicks = &detectTicks
		}
	}
	if age, ok := int64Value(event["ageAtReplyMs"]); ok && age >= 0 {
		observe := float64(age)
		if timing, ok := row.Payload["timing"].(map[string]any); ok {
			// Only the legs the row carries: a missing key reads as -1.
			residual := timingMs(timing, "call_ms") - timingMs(timing, "native_queue_ms") - timingMs(timing, "native_execute_ms")
			if residual > 0 {
				observe += residual
			}
		}
		out.ObserveMs = &observe
	}
	return out
}

// timingMs reads one leg of a native_response timing map, zero when absent.
func timingMs(timing map[string]any, key string) float64 {
	if _, ok := timing[key]; !ok {
		return 0
	}
	return AsNumber(timing[key])
}

// findEvents returns every object in an "events" array anywhere in the
// decoded ProtoJSON reply, in document order.
func findEvents(v any) []map[string]any {
	var out []map[string]any
	switch node := v.(type) {
	case map[string]any:
		keys := make([]string, 0, len(node))
		for k := range node {
			keys = append(keys, k)
		}
		sort.Strings(keys)
		for _, k := range keys {
			if k == "events" {
				if list, ok := node[k].([]any); ok {
					for _, item := range list {
						if event, ok := item.(map[string]any); ok {
							out = append(out, event)
						}
					}
					continue
				}
			}
			out = append(out, findEvents(node[k])...)
		}
	case []any:
		for _, item := range node {
			out = append(out, findEvents(item)...)
		}
	}
	return out
}

// int64Value accepts ProtoJSON int64 encodings: a JSON string or a number.
func int64Value(v any) (int64, bool) {
	switch value := v.(type) {
	case float64:
		return int64(value), true
	case string:
		n, err := strconv.ParseInt(value, 10, 64)
		return n, err == nil
	case json.Number:
		n, err := value.Int64()
		return n, err == nil
	}
	return 0, false
}

// SpeedMetrics is the per-case row a speed matrix reports (caseMetrics in
// cases/speedmatrix): the fields CheckSpeedMetrics and SpeedRowProblems
// read, decoded from the report's metrics rows. PausedFraction is the
// status-sample ratio (a sampling diagnostic); PausedFractionNative is
// native's own account of its stop/start transitions (#621), the paused
// share the thresholds bound where the row carries it.
type SpeedMetrics struct {
	Case                 string
	Speed                string
	WallTPS              float64
	TicksAdvanced        float64
	PausedFraction       float64
	PausedFractionNative float64
	NativePauseSamples   float64
	// The live steps of #593: how many the row ran, the native round trips
	// the costliest issued and the mean wall of one. A row without a live
	// step (no running window was planned under) leaves LiveSteps at 0 and
	// CheckLiveStepCost skips it.
	LiveSteps        float64
	MaxLiveStepReads float64
	LiveStepMs       float64
}

// PausedShare is the paused fraction a threshold bounds: native's account
// when the row sampled it, else the sample ratio.
func (m SpeedMetrics) PausedShare() float64 {
	if m.NativePauseSamples > 0 {
		return m.PausedFractionNative
	}
	return m.PausedFraction
}

// SpeedMetricsFromRows decodes the "case", "speed", "wall_tps",
// "ticks_advanced", "paused_fraction", "paused_fraction_native",
// "native_pause_samples", "live_steps", "max_live_step_reads" and
// "live_step_ms_mean" fields of each metrics row.
func SpeedMetricsFromRows(rows []map[string]any) []SpeedMetrics {
	out := make([]SpeedMetrics, 0, len(rows))
	for _, row := range rows {
		out = append(out, SpeedMetrics{Case: AsString(row["case"]), Speed: AsString(row["speed"]), WallTPS: AsNumber(row["wall_tps"]), TicksAdvanced: AsNumber(row["ticks_advanced"]),
			PausedFraction: AsNumber(row["paused_fraction"]), PausedFractionNative: AsNumber(row["paused_fraction_native"]), NativePauseSamples: AsNumber(row["native_pause_samples"]),
			LiveSteps: AsNumber(row["live_steps"]), MaxLiveStepReads: AsNumber(row["max_live_step_reads"]), LiveStepMs: AsNumber(row["live_step_ms_mean"])})
	}
	return out
}

// SpeedRowProblems is the runner-boundary check (#621) the outcome and
// metric comparators cannot make, since both accept an empty matrix: every
// required case must have a metrics row that advanced the tick and, when
// the case is compared, an outcome row. Each problem names the row. An
// empty result means every required row is present and non-empty.
func SpeedRowProblems(required []SpeedCase, outcomes []SpeedOutcome, metrics []SpeedMetrics) []string {
	var problems []string
	byCase := map[string]SpeedMetrics{}
	for _, m := range metrics {
		byCase[m.Case] = m
	}
	outcomeByCase := map[string]bool{}
	for _, o := range outcomes {
		outcomeByCase[o.Case] = true
	}
	for _, c := range required {
		m, ok := byCase[c.Name]
		switch {
		case !ok:
			problems = append(problems, fmt.Sprintf("%s: no metrics row", c.Name))
		case m.TicksAdvanced <= 0 || m.WallTPS <= 0:
			problems = append(problems, fmt.Sprintf("%s: empty metrics row (ticks_advanced=%.0f wall_tps=%.1f)", c.Name, m.TicksAdvanced, m.WallTPS))
		}
		if c.Compared() && !outcomeByCase[c.Name] {
			problems = append(problems, fmt.Sprintf("%s: no outcome row", c.Name))
		}
	}
	return problems
}

// CheckLiveStepCost lists the rows whose costliest live step issued more
// than maxReads native round trips, the step-cost bound of issue #593: a
// live step plans under a running window off the facts its one review
// bundle carries, so its round trips are the bundle plus what an event
// within the step made stale. Rows that ran no live step are skipped, and a
// maxReads of 0 disables the check. The step's wall time is reported beside
// it (live_step_ms_mean) but not bounded: wall measures the box and the
// GABS transport floor, the call count measures this repository.
func CheckLiveStepCost(rows []SpeedMetrics, maxReads float64) []string {
	if maxReads <= 0 {
		return nil
	}
	var problems []string
	for _, row := range rows {
		if row.LiveSteps <= 0 {
			continue
		}
		if row.MaxLiveStepReads > maxReads {
			problems = append(problems, fmt.Sprintf("%s: a live step issued %.0f native reads, over %.0f (%.0f live steps, wall mean %.0fms)",
				row.Case, row.MaxLiveStepReads, maxReads, row.LiveSteps, row.LiveStepMs))
		}
	}
	return problems
}

// CheckSpeedMetrics lists the clock-throughput expectations of issue #126
// the matrix missed. maxPausedFraction, when positive, bounds the paused
// fraction of every case at Superfast or faster (the speeds whose windows
// the wall-time sizing must widen). minUltrafastRatio, when positive,
// requires the Ultrafast case's wall TPS to be at least that multiple of
// the Fast case's; it is skipped unless both ran. An empty result means
// the matrix met every enabled expectation.
func CheckSpeedMetrics(rows []SpeedMetrics, maxPausedFraction, minUltrafastRatio float64) []string {
	var problems []string
	var fast, ultrafast *SpeedMetrics
	for i := range rows {
		row := &rows[i]
		switch row.Case {
		case "Fast":
			fast = row
		case "Ultrafast":
			ultrafast = row
		}
		if maxPausedFraction > 0 && (row.Speed == "Superfast" || row.Speed == "Ultrafast") && row.PausedShare() > maxPausedFraction {
			problems = append(problems, fmt.Sprintf("%s: paused fraction %.2f exceeds %.2f", row.Case, row.PausedShare(), maxPausedFraction))
		}
	}
	if minUltrafastRatio > 0 && fast != nil && ultrafast != nil && ultrafast.WallTPS < minUltrafastRatio*fast.WallTPS {
		problems = append(problems, fmt.Sprintf("Ultrafast wall TPS %.1f is under %.1fx the Fast wall TPS %.1f", ultrafast.WallTPS, minUltrafastRatio, fast.WallTPS))
	}
	return problems
}

// The observation-load row's reader count and polling interval (#656):
// several dashboards' worth of state reads, well above the dashboard's own
// cadence, so readers contend with the controller's refreshes.
const (
	ObservationLoadReaders  = 3
	ObservationLoadInterval = 250 * time.Millisecond
)
