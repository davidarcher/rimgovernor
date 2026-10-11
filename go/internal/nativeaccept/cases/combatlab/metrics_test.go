package combatlab

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/combatmetrics"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

// The v2 rows (clock_stop, dispatch, a decision combat_order) count the same
// as the legacy kinds the bundle holds.
func TestScanLogsV2Rows(t *testing.T) {
	decision := func(kind, verdict, reason, target string, attrs map[string]any) telemetrytest.Row {
		return telemetrytest.Row{Kind: kind, Context: map[string]any{}, Payload: map[string]any{"verdict": verdict, "reason": reason, "target": target, "dur_ms": 0.0, "attrs": attrs}}
	}
	path := telemetrytest.WriteLogs(t, t.TempDir(),
		telemetrytest.Row{Kind: "clock_stop", Context: map[string]any{}, Payload: map[string]any{"reason": "combat_event", "event": "downed", "resume_latency_ms": 250}},
		decision("combat_order", "applied", "", "p1", map[string]any{"index": 0}),
		decision("combat_order", "refused", "unreachable", "p2", map[string]any{"index": 1}),
		decision("dispatch", "completed", "", "routine-defense-p1-hold", map[string]any{}),
	)
	m := combatmetrics.Metrics{StepLatencyP95Ms: -1}
	if err := ScanLogs(&m, path); err != nil {
		t.Fatal(err)
	}
	if m.OrdersIssued != 2 || m.OrdersRefused != 1 || m.StepLatencyP95Ms != 250 {
		t.Fatalf("%+v", m)
	}
}

// A rotated ring keeps its early combat rows in logs.jsonl.N: the bundle copy
// reads them first, oldest segment (highest N) to the live file.
func TestWriteCombatRowsReadsRotatedSegments(t *testing.T) {
	dir := t.TempDir()
	live := filepath.Join(dir, "logs.jsonl")
	row := func(target, kind, verdict string) telemetrytest.Row {
		return telemetrytest.Row{Kind: kind, Context: map[string]any{}, Payload: map[string]any{"verdict": verdict, "reason": "", "target": target, "dur_ms": 0.0, "attrs": map[string]any{}}}
	}
	files := map[string][]telemetrytest.Row{
		live + ".2": {row("first", "combat_order", "applied"), row("dropped", "native_call", "ok")},
		live + ".1": {row("second", "combat_order", "refused")},
		live:        {row("third", "combat_order", "applied")},
	}
	for path, rows := range files {
		lines := telemetrytest.LogLines(t, rows...)
		if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	out := filepath.Join(dir, CombatRowsFile)
	if err := writeCombatRows(live, out); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(out)
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(string(data)), "\n")
	if len(lines) != 3 || !strings.Contains(lines[0], `"first"`) || !strings.Contains(lines[1], `"second"`) || !strings.Contains(lines[2], `"third"`) {
		t.Fatalf("rows out of order or dropped:\n%s", data)
	}
	m := combatmetrics.Metrics{StepLatencyP95Ms: -1}
	if err := ScanLogs(&m, out); err != nil {
		t.Fatal(err)
	}
	if m.OrdersIssued != 2 || m.OrdersRefused != 1 {
		t.Fatalf("%+v", m)
	}
}

// A recorded bundle aggregates to its metrics: final-read downs, deaths
// and a hostile gone from the map (fled), the cumulative damage ledger
// (colonist damage, friendly fire, first contact), resolution on the read
// where no hostile stands, and the rows' orders and p95 latency.
func TestAggregateBundle(t *testing.T) {
	m, err := AggregateBundle("testdata/bundle", "lab-test")
	if err != nil {
		t.Fatal(err)
	}
	want := combatmetrics.Metrics{Version: combatmetrics.Version, Fixture: "lab-test", Ticks: 500,
		ColonistDamage: 13.3, ColonistDowns: 1, EnemyDowns: 1, EnemyDeaths: 1, EnemyFled: 1,
		FirstContactTick: 1100, ResolvedTick: 1500, ContactToResolution: 400, Winner: Colonist,
		FriendlyFireHits: 1, OrdersIssued: 1, OrdersRefused: 1, StepLatencyP95Ms: 400}
	if m != want {
		t.Fatalf("metrics\n got %+v\nwant %+v", m, want)
	}
}

// No reads past staging: unresolved, no contact, no planner.
func TestAggregateUnresolved(t *testing.T) {
	m := Aggregate("x", map[string]string{"a": Colonist, "b": Hostile}, []map[string]any{{"tick": 10.0, "pawns": []any{map[string]any{"id": "a"}, map[string]any{"id": "b"}}}})
	if m.ResolvedTick != -1 || m.ContactToResolution != -1 || m.FirstContactTick != -1 || m.StepLatencyP95Ms != -1 || m.Winner != "" {
		t.Fatalf("got %+v", m)
	}
}

// Every metrics fixture has a committed baseline.
func TestBaselinesCommitted(t *testing.T) {
	for _, name := range Names {
		m, err := Baseline(name)
		if err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if m.Fixture != name || m.Ticks <= 0 {
			t.Fatalf("%s baseline: %+v", name, m)
		}
	}
}

// Raiders still in their pods are absent from the staging read: that read
// resolves nothing, and the later one decides.
func TestAggregateStagingReadResolvesNothing(t *testing.T) {
	staged := map[string]string{"a": Colonist, "b": Hostile}
	alive := func(ids ...string) []any {
		var rows []any
		for _, id := range ids {
			rows = append(rows, map[string]any{"id": id})
		}
		return rows
	}
	m := Aggregate("x", staged, []map[string]any{
		{"tick": 1.0, "pawns": alive("a")},
		{"tick": 500.0, "pawns": alive("a", "b")},
	})
	if m.ResolvedTick != -1 || m.Winner != "" {
		t.Fatalf("got %+v", m)
	}
}
