package logview

import (
	"fmt"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

type row struct {
	seq                     int
	run, kind, level, comp  string
	trace                   string
	tick                    int
	verdict, reason, target string
	decision                bool
}

// records writes rows through the real file sink and reads them back as the
// CLI does; the sequence of a record is its 1-based position. The run and
// trace of a row are set on the record afterwards, as one service run and one
// span context cannot differ per row here.
func records(t *testing.T, rows ...row) []bridge.TimelineRecord {
	t.Helper()
	var emitted []telemetrytest.Row
	for _, r := range rows {
		level := r.level
		if level == "" {
			level = "INFO"
		}
		payload := map[string]any{"note": "x"}
		if r.decision {
			payload = map[string]any{"verdict": r.verdict, "reason": r.reason, "target": r.target, "dur_ms": 2.0, "attrs": map[string]any{}}
		}
		emitted = append(emitted, telemetrytest.Row{Kind: r.kind, Context: map[string]any{"level": level, "tick": r.tick, "component": r.comp}, Payload: payload})
	}
	recs, err := bridge.ReadOTLPTimeline(telemetrytest.WriteLogs(t, t.TempDir(), emitted...))
	if err != nil {
		t.Fatal(err)
	}
	if len(recs) != len(rows) {
		t.Fatalf("read %d rows, wrote %d", len(recs), len(rows))
	}
	for i := range recs {
		recs[i].Run = rows[i].run
		if rows[i].trace != "" {
			recs[i].Context[telemetry.TraceIDKey] = rows[i].trace
		}
	}
	return recs
}

func seqs(records []bridge.TimelineRecord) []int {
	var out []int
	for _, r := range records {
		out = append(out, int(r.Sequence))
	}
	return out
}

func TestFilters(t *testing.T) {
	records := records(t,
		row{seq: 1, run: "a", kind: "coverage", comp: "bridge", tick: 1},
		row{seq: 2, run: "a", kind: "admission", comp: "clock", level: "WARN", tick: 10, trace: "t1", decision: true, verdict: "refused", reason: "r", target: "w"},
		row{seq: 3, run: "b", kind: "planner_step", comp: "worker", level: "ERROR", tick: 20, trace: "t2", decision: true, verdict: "failed"},
		row{seq: 4, run: "b", kind: "planner_step", comp: "worker", tick: 30, trace: "t2", decision: true, verdict: "admitted"},
	)
	cases := []struct {
		name   string
		filter Filter
		want   []int
	}{
		{"none", Filter{}, []int{1, 2, 3, 4}},
		{"kind", Filter{Kind: "planner_step"}, []int{3, 4}},
		{"component", Filter{Component: "clock"}, []int{2}},
		{"level minimum", Filter{Level: "warn"}, []int{2, 3}},
		{"level error", Filter{Level: "ERROR"}, []int{3}},
		{"trace", Filter{Trace: "t2"}, []int{3, 4}},
		{"since run", Filter{SinceRun: true}, []int{3, 4}},
		{"tick range", Filter{Ticks: TickRange{Min: 10, Max: 20, HasMin: true, HasMax: true}}, []int{2, 3}},
		{"open tick range", Filter{Ticks: TickRange{Min: 20, HasMin: true}}, []int{3, 4}},
		{"combined", Filter{Kind: "planner_step", Level: "WARN"}, []int{3}},
	}
	for _, c := range cases {
		if got := seqs(c.filter.Apply(records)); fmt.Sprint(got) != fmt.Sprint(c.want) {
			t.Errorf("%s: got %v want %v", c.name, got, c.want)
		}
	}
}

func TestParseTickRange(t *testing.T) {
	for text, want := range map[string]TickRange{
		"5..9": {Min: 5, Max: 9, HasMin: true, HasMax: true},
		"5..":  {Min: 5, HasMin: true},
		"..9":  {Max: 9, HasMax: true},
		"7":    {Min: 7, Max: 7, HasMin: true, HasMax: true},
	} {
		if got, err := ParseTickRange(text); err != nil || got != want {
			t.Errorf("%q: got %+v, %v", text, got, err)
		}
	}
	for _, bad := range []string{"x", "9..5", "1..b"} {
		if _, err := ParseTickRange(bad); err == nil {
			t.Errorf("%q: want error", bad)
		}
	}
}

func TestCollapseStuckLoop(t *testing.T) {
	var rows []row
	for i := 1; i <= 50; i++ {
		rows = append(rows, row{seq: i, kind: "planner_step", comp: "worker", tick: 100 + i, decision: true, verdict: "waiting", reason: "no_work", target: "stock"})
	}
	rows = append(rows, row{seq: 51, kind: "planner_step", comp: "worker", level: "WARN", tick: 200, decision: true, verdict: "refused", reason: "no_work", target: "stock"})
	entries := Collapse(records(t, rows...))
	if len(entries) != 2 {
		t.Fatalf("want 2 entries, got %d", len(entries))
	}
	e := entries[0]
	if e.Count != 50 || *e.FirstTick != 101 || *e.LastTick != 150 {
		t.Fatalf("entry %+v", e)
	}
	if line := e.Line(); !strings.Contains(line, "planner_step waiting no_work stock") || !strings.Contains(line, "x 50, first/last tick 101/150") {
		t.Fatalf("line %q", line)
	}
}

func TestCollapseHTTPAccessByRoute(t *testing.T) {
	access := func(method, path string, status, ms float64) bridge.TimelineRecord {
		return bridge.TimelineRecord{Kind: "http_access",
			Context: map[string]any{"level": "WARN", "component": "httpapi", "tick": float64(7)},
			Payload: map[string]any{"method": method, "path": path, "status": status, "dur_ms": ms, "bytes": float64(65)}}
	}
	entries := Collapse([]bridge.TimelineRecord{
		access("POST", "/api/lifecycle/load", 503, 28.5),
		access("POST", "/api/lifecycle/load", 503, 118.1),
		access("POST", "/api/lifecycle/load", 201, 40),
		access("GET", "/api/lifecycle/load", 503, 3),
	})
	if len(entries) != 3 || entries[0].Count != 2 {
		t.Fatalf("entries %+v", entries)
	}
	if line := entries[0].Line(); !strings.Contains(line, "http_access POST /api/lifecycle/load 503  x 2") {
		t.Fatalf("line %q", line)
	}
	if words := entries[1].Words(); words != "http_access POST /api/lifecycle/load 201 40.0ms" {
		t.Fatalf("words %q", words)
	}
}

func TestCompactPayloadLeadsWithWhatHappened(t *testing.T) {
	text := compactPayload(map[string]any{"seq": 6, "source": "watchdog", "level": "warn", "msg": "hop slow"})
	if !strings.HasPrefix(text, `msg="hop slow"`) {
		t.Fatal(text)
	}
}

func TestCollapseKeyAndInterleaving(t *testing.T) {
	recs := records(t,
		row{seq: 1, kind: "admission", comp: "a", tick: 1, decision: true, verdict: "refused", reason: "r", target: "t"},
		row{seq: 2, kind: "admission", comp: "a", tick: 2, decision: true, verdict: "admitted", reason: "r", target: "t"},
		row{seq: 3, kind: "admission", comp: "a", tick: 3, level: "WARN", decision: true, verdict: "refused", reason: "r", target: "t"},
		row{seq: 4, kind: "admission", comp: "b", tick: 4, decision: true, verdict: "refused", reason: "r", target: "t"},
		row{seq: 5, kind: "native_call", comp: "a", tick: 5},
		row{seq: 6, kind: "native_call", comp: "a", tick: 6},
	)
	entries := Collapse(recs)
	// Differing verdict or component never collapse; rows without the
	// decision shape never collapse; interleaved repeats do.
	if len(entries) != 5 {
		t.Fatalf("entries %d: %+v", len(entries), entries)
	}
	if entries[0].Count != 2 || entries[0].Level != "WARN" || *entries[0].LastTick != 3 {
		t.Fatalf("first %+v", entries[0])
	}
	if entries[3].Count != 1 || entries[4].Count != 1 {
		t.Fatalf("plain rows collapsed: %+v", entries[3:])
	}
}

func TestPath(t *testing.T) {
	if got := Path(filepath.Join("p"), ""); got != filepath.Join("p", "otlp", "logs.jsonl") {
		t.Fatal(got)
	}
	if Path("", "x.jsonl") != "x.jsonl" {
		t.Fatal("file path kept")
	}
}

func TestFoodCreditRowShowsFactorAndState(t *testing.T) {
	rec := bridge.TimelineRecord{Kind: "food_credit", Context: map[string]any{"component": "routine"}, Payload: map[string]any{
		telemetry.VerdictKey: "credited", telemetry.ReasonKey: "window", telemetry.TargetKey: "crop:z7",
		telemetry.AttrsKey: map[string]any{"factor": 0.25, "state": "delivering", "expected": 4.0, "observed": 1.0, "window_days": 7.0}}}
	if got := NewEntry(rec).Words(); got != "food_credit credited window crop:z7 factor 0.25 (delivering)" {
		t.Fatalf("Words = %q", got)
	}
}

func TestBuildStalledRowShowsCountsAndReasons(t *testing.T) {
	rec := bridge.TimelineRecord{Kind: "build_stalled", Context: map[string]any{"component": "building-planner"}, Payload: map[string]any{
		telemetry.VerdictKey: "waiting", telemetry.ReasonKey: "awaiting_materials", telemetry.TargetKey: "bedroom-shell-105-99",
		telemetry.AttrsKey: map[string]any{"blueprints": 8.0, "frames": 2.0, "reasons": map[string]any{"awaiting_materials": 8.0, "no_eligible_builder": 2.0}}}}
	if got := NewEntry(rec).Words(); got != "build_stalled waiting awaiting_materials bedroom-shell-105-99 8 blueprints, 2 frames awaiting_materials=8 no_eligible_builder=2" {
		t.Fatalf("Words = %q", got)
	}
}
