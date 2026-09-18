package nativeaccept

import (
	"encoding/json"
	"fmt"
	"sort"
	"strconv"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

// SpeedCase is one column of the speed matrix (issue #111): the serve
// --clock-speed flag it runs under and whether the headless-only test
// acceleration (#109) is on. "uncapped" is Ultrafast with acceleration; the
// rendered profile refuses it, so a harness checks -rendered before opening.
type SpeedCase struct {
	Name             string `json:"name"`
	Speed            string `json:"speed"`
	TestAcceleration bool   `json:"test_acceleration"`
}

// DefaultSpeedMatrix is the -speeds default: every native speed plus uncapped.
const DefaultSpeedMatrix = "Normal,Fast,Superfast,Ultrafast,uncapped"

// ParseSpeedCases turns a comma-separated -speeds value into cases, in order,
// refusing unknown names and repeats.
func ParseSpeedCases(spec string) ([]SpeedCase, error) {
	known := map[string]SpeedCase{
		"normal":    {Name: "Normal", Speed: "Normal"},
		"fast":      {Name: "Fast", Speed: "Fast"},
		"superfast": {Name: "Superfast", Speed: "Superfast"},
		"ultrafast": {Name: "Ultrafast", Speed: "Ultrafast"},
		"uncapped":  {Name: "uncapped", Speed: "Ultrafast", TestAcceleration: true},
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
			return nil, fmt.Errorf("unknown speed %q (want Normal, Fast, Superfast, Ultrafast or uncapped)", strings.TrimSpace(part))
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

// ServeArgs are the serve flags a case adds: its clock speed and, for
// uncapped, the test-acceleration opt-in.
func (c SpeedCase) ServeArgs() []string {
	args := []string{"--clock-speed", c.Speed}
	if c.TestAcceleration {
		args = append(args, "--clock-test-acceleration")
	}
	return args
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
}

const clockEventsTool = "rimgovernor/clock_read_events"

// SummarizeStops scans the timeline's clock_read_events replies. Events are
// keyed by cursor so a page re-read after a hold counts once. Events
// observed before sinceUnixMs are skipped: the native event journal
// survives a reload in the same process, so a service's first page carries
// the previous cases' stops too. Zero keeps every event.
func SummarizeStops(rows []bridge.TimelineRecord, sinceUnixMs int64) StopSummary {
	summary := StopSummary{Reasons: map[string]int{}}
	seen := map[string]bool{}
	var total float64
	for _, row := range rows {
		if row.Kind != "native_response" {
			continue
		}
		if tool, _ := row.Payload["native_tool"].(string); tool != clockEventsTool {
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
			stopped, ok := event["stopped"].(map[string]any)
			if !ok {
				continue
			}
			observed, hasObserved := int64Value(event["observedAtUnixMs"])
			if sinceUnixMs > 0 && (!hasObserved || observed < sinceUnixMs) {
				continue
			}
			key := fmt.Sprint(event["cursor"])
			if event["cursor"] == nil {
				key = fmt.Sprintf("row-%d-%v", row.Sequence, event["observedAtUnixMs"])
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
	if len(summary.Reasons) == 0 {
		summary.Reasons = nil
	}
	return summary
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
// cases/speedmatrix): the fields CheckSpeedMetrics reads, decoded from the
// report's metrics rows.
type SpeedMetrics struct {
	Case           string
	Speed          string
	WallTPS        float64
	PausedFraction float64
}

// SpeedMetricsFromRows decodes the "case", "speed", "wall_tps" and
// "paused_fraction" fields of each metrics row.
func SpeedMetricsFromRows(rows []map[string]any) []SpeedMetrics {
	out := make([]SpeedMetrics, 0, len(rows))
	for _, row := range rows {
		out = append(out, SpeedMetrics{Case: AsString(row["case"]), Speed: AsString(row["speed"]), WallTPS: AsNumber(row["wall_tps"]), PausedFraction: AsNumber(row["paused_fraction"])})
	}
	return out
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
		if maxPausedFraction > 0 && (row.Speed == "Superfast" || row.Speed == "Ultrafast") && row.PausedFraction > maxPausedFraction {
			problems = append(problems, fmt.Sprintf("%s: paused fraction %.2f exceeds %.2f", row.Case, row.PausedFraction, maxPausedFraction))
		}
	}
	if minUltrafastRatio > 0 && fast != nil && ultrafast != nil && ultrafast.WallTPS < minUltrafastRatio*fast.WallTPS {
		problems = append(problems, fmt.Sprintf("Ultrafast wall TPS %.1f is under %.1fx the Fast wall TPS %.1f", ultrafast.WallTPS, minUltrafastRatio, fast.WallTPS))
	}
	return problems
}
