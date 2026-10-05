package combatlab

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// The v2 rows (clock_stop, dispatch, a decision combat_order) count the same
// as the legacy kinds the bundle holds.
func TestScanFlightV2Rows(t *testing.T) {
	path := filepath.Join(t.TempDir(), FlightFile)
	rows := []string{
		`{"sequence":1,"kind":"clock_stop","context":{},"payload":{"reason":"combat_event","event":"downed","resume_latency_ms":250}}`,
		`{"sequence":2,"kind":"combat_order","context":{},"payload":{"verdict":"applied","reason":"","target":"p1","dur_ms":0,"attrs":{"index":0}}}`,
		`{"sequence":3,"kind":"combat_order","context":{},"payload":{"verdict":"refused","reason":"unreachable","target":"p2","dur_ms":0,"attrs":{"index":1}}}`,
		`{"sequence":4,"kind":"dispatch","context":{},"payload":{"verdict":"completed","reason":"","target":"routine-defense-p1-hold","dur_ms":0,"attrs":{}}}`,
	}
	if err := os.WriteFile(path, []byte(strings.Join(rows, "\n")+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	m := Metrics{StepLatencyP95Ms: -1}
	if err := m.ScanFlight(path); err != nil {
		t.Fatal(err)
	}
	if m.OrdersIssued != 2 || m.OrdersRefused != 1 || m.StepLatencyP95Ms != 250 {
		t.Fatalf("%+v", m)
	}
}

// A recorded bundle aggregates to its metrics: final-read downs, deaths
// and a hostile gone from the map (fled), the cumulative damage ledger
// (colonist damage, friendly fire, first contact), resolution on the read
// where no hostile stands, and the flight rows' orders and p95 latency.
func TestAggregateBundle(t *testing.T) {
	m, err := AggregateBundle("testdata/bundle", "lab-test")
	if err != nil {
		t.Fatal(err)
	}
	want := Metrics{Version: MetricsVersion, Fixture: "lab-test", Ticks: 500,
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

// Every #854 fixture has a committed baseline (#855 done-when).
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
