package nativeaccept

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// A flight recording of two calls (one an error, one timed by the
// companion), a cache hit and two scheduler steps, plus a restart's
// recording under service-2.
func writeRecordings(t *testing.T, output string) {
	t.Helper()
	first := strings.Join([]string{
		`{"sequence":1,"wall_time":10,"kind":"native_response","context":{},"payload":{"request":1,"tool":"games_call_tool","native_tool":"x/read","timing":{"total_ms":5,"response_bytes":100,"native_queue_ms":2,"native_execute_ms":4}}}`,
		`{"sequence":2,"wall_time":11,"kind":"native_error","context":{},"payload":{"request":2,"tool":"games_call_tool","native_tool":"x/read","timing":{"total_ms":1,"response_bytes":20}}}`,
		`{"sequence":3,"wall_time":12,"kind":"native_cache_hit","context":{},"payload":{"tool":"games_call_tool","native_tool":"x/read"}}`,
		`{"sequence":4,"wall_time":13,"kind":"clock_step","context":{},"payload":{"reads":3,"reason":"timer"}}`,
		`{"sequence":5,"wall_time":14,"kind":"clock_step","context":{},"payload":{"reads":1,"reason":"timer"}}`,
	}, "\n") + "\n"
	if err := os.WriteFile(FlightRecorderPath(output), []byte(first), 0644); err != nil {
		t.Fatal(err)
	}
	second := `{"sequence":1,"wall_time":20,"kind":"native_response","context":{},"payload":{"request":1,"tool":"games_call_tool","native_tool":"x/read","timing":{"total_ms":5,"response_bytes":30,"native_queue_ms":6,"native_execute_ms":8}}}` + "\n"
	if err := os.MkdirAll(filepath.Join(output, "service-2"), 0755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(output, "service-2", "flight.jsonl"), []byte(second), 0644); err != nil {
		t.Fatal(err)
	}
}

func TestComputeMetricsFromReportAndRecordings(t *testing.T) {
	output := t.TempDir()
	writeRecordings(t, output)
	r := Report{
		WallMsKey: int64(12000), BootMsKey: int64(3000), TicksAdvancedKey: uint64(6000), WallTPSKey: 500.0,
		"wait_stats": map[string]any{"waits": 4, "stalled": 1, "max_quiet_ms": int64(700)},
		"service":    map[string]any{"pid": 1}, "service_2": map[string]any{"pid": 2},
	}
	m := ComputeMetrics(r, output)
	want := Metrics{
		"wall_ms": 12000, "boot_ms": 3000, "ticks_advanced": 6000, "wall_tps": 500,
		"waits": 4, "waits_stalled": 1, "max_quiet_ms": 700,
		"native_calls": 3, "native_errors": 1, "native_bytes": 150,
		"reads_per_step_mean": 2, "cache_hit_ratio": 0.25,
		"native_queue_ms_mean": 4, "native_exec_ms_mean": 6,
		"service_launches": 2,
	}
	for name, value := range want {
		if m[name] != value {
			t.Errorf("%s = %v, want %v", name, m[name], value)
		}
	}
	if m["evidence_bytes"] <= 0 {
		t.Errorf("evidence_bytes = %v", m["evidence_bytes"])
	}
	if len(m) != len(MetricNames) {
		t.Errorf("block has %d metrics, want %d: %v", len(m), len(MetricNames), m)
	}
	// Without recordings or services every metric is present at zero.
	m = ComputeMetrics(Report{}, t.TempDir())
	for _, name := range MetricNames {
		if v, ok := m[name]; !ok || v != 0 {
			t.Errorf("%s = %v, %v; want present and zero", name, v, ok)
		}
	}
}

// Finalize stamps the block and writes it; a report decoded from
// result.json yields it back through MetricsOf.
func TestFinalizeWritesMetricsBlock(t *testing.T) {
	output := t.TempDir()
	r := NewReport("metrics", true)
	r["passed"] = true
	if code := r.Finalize(output); code != 0 {
		t.Fatalf("exit %d", code)
	}
	data, err := os.ReadFile(filepath.Join(output, "result.json"))
	if err != nil {
		t.Fatal(err)
	}
	var decoded map[string]any
	if err := json.Unmarshal(data, &decoded); err != nil {
		t.Fatal(err)
	}
	m, ok := MetricsOf(decoded)
	if !ok || len(m) != len(MetricNames) {
		t.Fatalf("metrics = %v (%v)", m, ok)
	}
	if m["wall_ms"] < 0 || m["waits"] != 0 {
		t.Errorf("metrics = %v", m)
	}
}

func TestSeriesAppendReadAndDrift(t *testing.T) {
	path := filepath.Join(t.TempDir(), "runs", "metrics.jsonl")
	if rows, err := ReadSeries(path); err != nil || rows != nil {
		t.Fatalf("missing series = %v, %v", rows, err)
	}
	for i, wall := range []float64{10000, 11000, 10500, 9800, 10200} {
		row := SeriesRow{Case: "x/y", RunID: "run-" + string(rune('a'+i)), Timestamp: time.Now(), Passed: true,
			Metrics: Metrics{"wall_ms": wall, "native_errors": 0, "cache_hit_ratio": 0.5, "waits_stalled": 0, "wall_tps": 400}}
		if err := AppendSeries(path, row); err != nil {
			t.Fatal(err)
		}
	}
	// A foreign case, a failed run and a corrupt line neither count nor
	// break the read.
	if err := AppendSeries(path, SeriesRow{Case: "other/case", RunID: "run-z", Passed: true, Metrics: Metrics{"wall_ms": 1}}); err != nil {
		t.Fatal(err)
	}
	if err := AppendSeries(path, SeriesRow{Case: "x/y", RunID: "run-refused", Metrics: Metrics{"wall_ms": 57}}); err != nil {
		t.Fatal(err)
	}
	f, _ := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0644)
	f.WriteString("{not json\n")
	f.Close()
	rows, err := ReadSeries(path)
	if err != nil || len(rows) != 7 {
		t.Fatalf("rows = %d, %v", len(rows), err)
	}
	// Median wall 10200: 13000 is 1.27x and 2800 over, under the floor;
	// 16000 is past both. An error where the median is zero never flags.
	// The cache hit ratio flags when it drops; wall_tps 300 is 0.75x and
	// 100 under, past both.
	m := Metrics{"wall_ms": 13000, "native_errors": 3, "cache_hit_ratio": 0.5, "wall_tps": 300}
	flags := Drift("x/y", "run-new", m, rows)
	if len(flags) != 1 || flags[0].Metric != "wall_tps" {
		t.Errorf("flags = %+v", flags)
	}
	m = Metrics{"wall_ms": 16000, "native_errors": 0, "cache_hit_ratio": 0.3, "wall_tps": 400}
	flags = Drift("x/y", "run-new", m, rows)
	if len(flags) != 2 || flags[0].Metric != "wall_ms" || flags[1].Metric != "cache_hit_ratio" {
		t.Fatalf("flags = %+v", flags)
	}
	if flags[0].Median != 10200 || flags[0].Samples != 5 || flags[0].Value != 16000 {
		t.Errorf("wall flag = %+v", flags[0])
	}
	if !strings.Contains(flags[0].String(), "x/y wall_ms") {
		t.Errorf("String = %q", flags[0])
	}
	// The run's own earlier rows (same run id) are not its history, and a
	// case without history never flags.
	if flags = Drift("x/y", "run-a", Metrics{"wall_ms": 16000}, rows[:1]); len(flags) != 0 {
		t.Errorf("own row counted: %+v", flags)
	}
	if flags = Drift("nobody/yet", "run-new", Metrics{"wall_ms": 16000}, rows); len(flags) != 0 {
		t.Errorf("no history flagged: %+v", flags)
	}
}

// The trailing median spans the last DriftWindow rows only, so a case
// whose cost settled long ago is judged on its recent runs.
func TestDriftWindowIsTrailing(t *testing.T) {
	var rows []SeriesRow
	for i := 0; i < DriftWindow+5; i++ {
		wall := 1000.0
		if i >= 5 {
			wall = 100000
		}
		rows = append(rows, SeriesRow{Case: "x/y", RunID: "old", Passed: true, Metrics: Metrics{"wall_ms": wall}})
	}
	if flags := Drift("x/y", "new", Metrics{"wall_ms": 100000}, rows); len(flags) != 0 {
		t.Errorf("flags = %+v", flags)
	}
}

// RecordSeries appends the finalized report's block and marks the report
// with the series path and any drift.
func TestRecordSeriesAppendsAndFlags(t *testing.T) {
	path := filepath.Join(t.TempDir(), "metrics.jsonl")
	for i := 0; i < 3; i++ {
		if err := AppendSeries(path, SeriesRow{Case: "x/y", RunID: "prior", Passed: true, Metrics: Metrics{"wall_ms": 1000, "waits_stalled": 0}}); err != nil {
			t.Fatal(err)
		}
	}
	r := Report{"passed": true, MetricsKey: Metrics{"wall_ms": 20000, "waits_stalled": 2}}
	flags := RecordSeries(path, "x/y", "run-1", r)
	if len(flags) != 1 || flags[0].Metric != "wall_ms" {
		t.Errorf("flags = %+v", flags)
	}
	if r["series"] != path || r["series_error"] != nil {
		t.Errorf("report = %v", r)
	}
	if got, _ := r["drift"].([]DriftFlag); len(got) != 1 {
		t.Errorf("drift = %v", r["drift"])
	}
	rows, err := ReadSeries(path)
	if err != nil || len(rows) != 4 || rows[3].RunID != "run-1" || !rows[3].Passed || rows[3].Metrics["wall_ms"] != 20000 {
		t.Errorf("rows = %+v, %v", rows, err)
	}
	// A report without a block appends nothing.
	if flags := RecordSeries(path, "x/y", "run-2", Report{}); flags != nil {
		t.Errorf("flags = %+v", flags)
	}
	if rows, _ = ReadSeries(path); len(rows) != 4 {
		t.Errorf("rows = %d", len(rows))
	}
}
