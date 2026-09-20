package nativeaccept

import (
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
)

func TestParseSpeedCases(t *testing.T) {
	cases, err := ParseSpeedCases(DefaultSpeedMatrix)
	if err != nil {
		t.Fatal(err)
	}
	if len(cases) != 8 || cases[4].Name != "uncapped" || cases[4].Speed != "Ultrafast" || !cases[4].TestAcceleration || cases[4].BlindTicks != 0 {
		t.Fatalf("unexpected cases %+v", cases)
	}
	// The governor-off and viewer rows (#621) are uncapped's speed; only
	// governor-off is outside the outcome comparison.
	if cases[6].Name != "governor-off" || !cases[6].GovernorOff || cases[6].Compared() || !cases[6].TestAcceleration || cases[6].Speed != "Ultrafast" {
		t.Fatalf("unexpected governor-off case %+v", cases[6])
	}
	if cases[7].Name != "viewer" || !cases[7].Viewer || !cases[7].Compared() || !cases[7].TestAcceleration {
		t.Fatalf("unexpected viewer case %+v", cases[7])
	}
	if got := cases[4].ServeArgs(); len(got) != 3 || got[2] != "--clock-test-acceleration" {
		t.Fatalf("uncapped args %v", got)
	}
	// The regulated row (#583) is uncapped under the blind-tick budget.
	if cases[5].Name != "regulated" || !cases[5].TestAcceleration || cases[5].BlindTicks != RegulatedBlindTicks {
		t.Fatalf("unexpected regulated case %+v", cases[5])
	}
	if got := cases[5].ServeArgs(); len(got) != 5 || got[3] != "--clock-blind-ticks" || got[4] != "300" {
		t.Fatalf("regulated args %v", got)
	}
	if got := cases[0].ServeArgs(); len(got) != 2 || got[1] != "Normal" {
		t.Fatalf("normal args %v", got)
	}
	if _, err := ParseSpeedCases("Normal,normal"); err == nil {
		t.Fatal("repeat accepted")
	}
	if _, err := ParseSpeedCases("Warp"); err == nil {
		t.Fatal("unknown speed accepted")
	}
	if _, err := ParseSpeedCases(" , "); err == nil {
		t.Fatal("empty list accepted")
	}
}

func TestCompareOutcomes(t *testing.T) {
	agree := []SpeedOutcome{
		{Case: "Normal", StoredUnits: 100, StoredStacks: 4, WallsBuilt: 6, HealthyColonists: 3},
		{Case: "Fast", StoredUnits: 99, StoredStacks: 4, WallsBuilt: 6, HealthyColonists: 3},
	}
	if problems := CompareOutcomes(agree, 1); len(problems) != 0 {
		t.Fatalf("unexpected problems %v", problems)
	}
	disagree := append(agree, SpeedOutcome{Case: "uncapped", StoredUnits: 75, StoredStacks: 2, WallsBuilt: 6, HealthyColonists: 3, UnsuccessfulStages: 2})
	problems := CompareOutcomes(disagree, 1)
	if len(problems) != 3 {
		t.Fatalf("expected stored_units, stored_stacks and the unsuccessful stage, got %v", problems)
	}
	if problems := CompareOutcomes(nil, 1); len(problems) != 0 {
		t.Fatalf("empty matrix reported %v", problems)
	}
}

func TestSummarizeStops(t *testing.T) {
	page := func(events ...map[string]any) map[string]any {
		body, _ := json.Marshal(map[string]any{"page": map[string]any{"events": events}})
		return map[string]any{
			"native_tool": clockEventsTool, "tool": "games_call_tool",
			"result": map[string]any{"payload": string(body)},
		}
	}
	budget := map[string]any{"cursor": "7", "observedAtUnixMs": "1000000", "stopped": map[string]any{"reason": "STOP_REASON_TICK_BUDGET"}}
	latched := map[string]any{"cursor": "8", "observedAtUnixMs": float64(1000500), "stopped": map[string]any{"reason": "STOP_REASON_WATCH_LATCHED"}}
	started := map[string]any{"cursor": "9", "started": map[string]any{}}
	rows := []bridge.TimelineRecord{
		{Kind: "native_response", Sequence: 1, WallTime: 1000.250, Payload: page(budget, started)},
		// The same page read again after a hold: cursor 7 counts once.
		{Kind: "native_response", Sequence: 2, WallTime: 1000.900, Payload: page(budget, latched)},
		{Kind: "native_response", Sequence: 3, WallTime: 1001, Payload: map[string]any{"native_tool": "rimgovernor/clock_read_status", "result": map[string]any{"payload": "{}"}}},
		{Kind: "recording_gap"},
	}
	got := SummarizeStops(rows, 0)
	if len(got.Latencies) != 2 || got.Latencies[0].Cursor != 7 || got.Latencies[1].Cursor != 8 || got.Latencies[0].ObserveMs != nil || got.Latencies[0].ReadmitMs != nil {
		t.Fatalf("latency rows without native ages or starts: %+v", got.Latencies)
	}
	if got.Stops != 2 || got.BudgetStops != 1 || got.ReactiveStops != 1 {
		t.Fatalf("unexpected counts %+v", got)
	}
	if got.Reasons["STOP_REASON_WATCH_LATCHED"] != 1 || got.Reasons["STOP_REASON_TICK_BUDGET"] != 1 {
		t.Fatalf("unexpected reasons %+v", got.Reasons)
	}
	if got.LatencyCount != 2 || got.MaxLatencyMs != 400 || got.MeanLatencyMs != 325 {
		t.Fatalf("unexpected latency %+v", got)
	}
	if later := SummarizeStops(rows, 1000200); later.Stops != 1 || later.BudgetStops != 0 {
		t.Fatalf("since bound ignored: %+v", later)
	}
	if empty := SummarizeStops(nil, 0); empty.Stops != 0 || empty.Reasons != nil {
		t.Fatalf("empty timeline %+v", empty)
	}
}

// CheckSpeedMetrics bounds the paused fraction only at Superfast and
// faster, compares the Ultrafast wall TPS against Fast only when both ran,
// and is silent with both thresholds off.
func TestCheckSpeedMetrics(t *testing.T) {
	rows := SpeedMetricsFromRows([]map[string]any{
		{"case": "Normal", "speed": "Normal", "wall_tps": 49.0, "paused_fraction": 0.36},
		{"case": "Fast", "speed": "Fast", "wall_tps": 111.0, "paused_fraction": 0.57},
		{"case": "Superfast", "speed": "Superfast", "wall_tps": 138.0, "paused_fraction": 0.74},
		{"case": "Ultrafast", "speed": "Ultrafast", "wall_tps": 188.0, "paused_fraction": 0.91},
		{"case": "uncapped", "speed": "Ultrafast", "wall_tps": 188.0, "paused_fraction": 0.4},
	})
	if got := CheckSpeedMetrics(rows, 0, 0); len(got) != 0 {
		t.Fatal(got)
	}
	got := CheckSpeedMetrics(rows, 0.5, 2)
	if len(got) != 3 || got[0] != "Superfast: paused fraction 0.74 exceeds 0.50" || got[1] != "Ultrafast: paused fraction 0.91 exceeds 0.50" || got[2] != "Ultrafast wall TPS 188.0 is under 2.0x the Fast wall TPS 111.0" {
		t.Fatal(got)
	}
	if got := CheckSpeedMetrics(rows[2:], 0.5, 2); len(got) != 2 {
		t.Fatal(got)
	}
	rows[3].WallTPS, rows[3].PausedFraction, rows[2].PausedFraction = 222, 0.5, 0.49
	if got := CheckSpeedMetrics(rows, 0.5, 2); len(got) != 0 {
		t.Fatal(got)
	}
}

// The per-stop latency split (#621): ticks from the stop event alone
// (occurrence -> detected -> stop), observe_ms from native's own age of
// the row at reply plus the reply's transport residual, readmit_ms from the
// carrying reply to the next clock_start receipt on the controller's
// clock. No leg subtracts one process's Unix time from another's, and a
// stop no start followed has no readmit leg.
func TestSummarizeStopsLatencySplit(t *testing.T) {
	page := func(wall float64, timing map[string]any, events ...map[string]any) bridge.TimelineRecord {
		body, _ := json.Marshal(map[string]any{"page": map[string]any{"events": events}})
		return bridge.TimelineRecord{Kind: "native_response", WallTime: wall, Payload: map[string]any{
			"native_tool": clockEventsTool, "tool": "games_call_tool", "timing": timing, "result": map[string]any{"payload": string(body)},
		}}
	}
	start := func(wall float64, payload string) bridge.TimelineRecord {
		return bridge.TimelineRecord{Kind: "native_response", WallTime: wall, Payload: map[string]any{"native_tool": clockStartTool, "tool": "games_call_tool", "result": map[string]any{"payload": payload}}}
	}
	injury := map[string]any{"cursor": "3", "observedAtUnixMs": "1000000", "ageAtReplyMs": "120", "context": map[string]any{"tick": "5040"},
		"stopped": map[string]any{"reason": "STOP_REASON_COLONIST_INJURY", "detectedTick": "5030", "occurrenceTick": "5000"}}
	budget := map[string]any{"cursor": "4", "observedAtUnixMs": "1000000", "ageAtReplyMs": "5", "context": map[string]any{"tick": "6000"},
		"stopped": map[string]any{"reason": "STOP_REASON_TICK_BUDGET", "detectedTick": "6000"}}
	rows := []bridge.TimelineRecord{
		page(100.0, map[string]any{"call_ms": 60.0, "native_queue_ms": 15.0, "native_execute_ms": 5.0}, injury),
		start(100.5, `{"failure":{"code":"FAILURE_CODE_INVALID_REQUEST"}}`),
		start(101.0, `{"receipt":{"applied":{}}}`),
		page(200.0, map[string]any{"call_ms": 40.0}, budget),
	}
	got := SummarizeStops(rows, 0).Latencies
	if len(got) != 2 {
		t.Fatalf("rows: %+v", got)
	}
	first := got[0]
	if first.StopTick != 5040 || *first.DetectedTick != 5030 || *first.OccurrenceTick != 5000 || *first.DetectTicks != 30 || *first.StopTicks != 10 {
		t.Fatalf("ticks: %+v", first)
	}
	if *first.ObserveMs != 160 || *first.ReadmitMs != 1000 {
		t.Fatalf("observe %v readmit %v", *first.ObserveMs, *first.ReadmitMs)
	}
	second := got[1]
	if second.OccurrenceTick != nil || second.DetectTicks != nil || *second.StopTicks != 0 || *second.ObserveMs != 45 || second.ReadmitMs != nil {
		t.Fatalf("budget stop: %+v", second)
	}
}

// SpeedRowProblems is the runner-boundary check (#621): every required
// row needs a metrics row that advanced the tick, and every compared row
// an outcome row, each problem naming the row; the comparators accept an
// empty matrix, so this is what fails an empty run.
func TestSpeedRowProblems(t *testing.T) {
	required, err := ParseSpeedCases("Normal,uncapped,governor-off,viewer")
	if err != nil {
		t.Fatal(err)
	}
	metrics := SpeedMetricsFromRows([]map[string]any{
		{"case": "Normal", "speed": "Normal", "wall_tps": 60.0, "ticks_advanced": 2300.0},
		{"case": "uncapped", "speed": "Ultrafast", "wall_tps": 0.0, "ticks_advanced": 0.0},
		{"case": "governor-off", "speed": "Ultrafast", "wall_tps": 5000.0, "ticks_advanced": 30000.0},
	})
	outcomes := []SpeedOutcome{{Case: "Normal", StoredUnits: 100, WallsBuilt: 6}, {Case: "uncapped", StoredUnits: 100, WallsBuilt: 6}}
	got := SpeedRowProblems(required, outcomes, metrics)
	want := []string{"uncapped: empty metrics row (ticks_advanced=0 wall_tps=0.0)", "viewer: no metrics row", "viewer: no outcome row"}
	if len(got) != len(want) {
		t.Fatalf("got %v want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got %v want %v", got, want)
		}
	}
	if got := SpeedRowProblems(required, nil, nil); len(got) != 7 {
		t.Fatalf("empty run passed: %v", got)
	}
	if got := CompareOutcomes(nil, 1); len(got) != 0 {
		t.Fatalf("CompareOutcomes is not the boundary: %v", got)
	}
	// A row read back in memory carries its tick count as the integer the
	// case computed, never the float64 JSON would decode.
	full := SpeedMetricsFromRows([]map[string]any{
		{"case": "Normal", "speed": "Normal", "wall_tps": 60.0, "ticks_advanced": uint64(2300)},
		{"case": "uncapped", "speed": "Ultrafast", "wall_tps": 4000.0, "ticks_advanced": 30000.0},
		{"case": "governor-off", "speed": "Ultrafast", "wall_tps": 5000.0, "ticks_advanced": 30000.0},
		{"case": "viewer", "speed": "Ultrafast", "wall_tps": 3500.0, "ticks_advanced": 30000.0},
	})
	if got := SpeedRowProblems(required, append(outcomes, SpeedOutcome{Case: "viewer"}), full); len(got) != 0 {
		t.Fatal(got)
	}
}

// The paused threshold bounds native's account where the row sampled it
// (#621) and the status-sample ratio where it did not.
func TestCheckSpeedMetricsPrefersNativePausedFraction(t *testing.T) {
	rows := SpeedMetricsFromRows([]map[string]any{
		{"case": "Ultrafast", "speed": "Ultrafast", "wall_tps": 188.0, "paused_fraction": 0.91, "paused_fraction_native": 0.2, "native_pause_samples": 12.0},
		{"case": "uncapped", "speed": "Ultrafast", "wall_tps": 188.0, "paused_fraction": 0.7, "paused_fraction_native": 0.0, "native_pause_samples": 0.0},
	})
	got := CheckSpeedMetrics(rows, 0.5, 0)
	if len(got) != 1 || got[0] != "uncapped: paused fraction 0.70 exceeds 0.50" {
		t.Fatal(got)
	}
}
