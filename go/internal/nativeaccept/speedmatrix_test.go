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
	if len(cases) != 6 || cases[4].Name != "uncapped" || cases[4].Speed != "Ultrafast" || !cases[4].TestAcceleration || cases[4].BlindTicks != 0 {
		t.Fatalf("unexpected cases %+v", cases)
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
