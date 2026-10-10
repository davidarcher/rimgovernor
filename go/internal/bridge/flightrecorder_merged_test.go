package bridge

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func ringLine(seq int, wall float64, kind string) string {
	return fmt.Sprintf(`{"version":2,"run":"r","sequence":%d,"wall_time":%g,"kind":%q,"context":{},"payload":{}}`, seq, wall, kind)
}

func writeRing(t *testing.T, path string, lines ...string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o644); err != nil {
		t.Fatal(err)
	}
}

func mergedKinds(rows []TimelineRecord) string {
	var out []string
	for _, r := range rows {
		out = append(out, r.Kind)
	}
	return strings.Join(out, ",")
}

func TestMergedTimelineOrdersByWallTimeWithoutFalseGaps(t *testing.T) {
	dir := t.TempDir()
	flight, explain := filepath.Join(dir, "flight.jsonl"), filepath.Join(dir, "explain.jsonl")
	// Sequences interleave and collide across rings; only wall time orders them.
	writeRing(t, flight, ringLine(1, 10, "f1"), ringLine(2, 30, "f2"), ringLine(3, 50, "f3"))
	writeRing(t, explain, ringLine(1, 20, "e1"), ringLine(2, 40, "e2"))
	rows, err := ReadMergedTimeline(flight)
	if err != nil {
		t.Fatal(err)
	}
	if got := mergedKinds(rows); got != "f1,e1,f2,e2,f3" {
		t.Fatalf("merged kinds = %s", got)
	}
	for _, r := range rows {
		if want := strings.HasPrefix(r.Kind, "e"); (r.Stream == ExplainStream) != want {
			t.Fatalf("%s stream = %q", r.Kind, r.Stream)
		}
	}
}

func TestMergedTimelineKeepsEachRingsOwnGaps(t *testing.T) {
	dir := t.TempDir()
	flight, explain := filepath.Join(dir, "flight.jsonl"), filepath.Join(dir, "explain.jsonl")
	writeRing(t, flight, ringLine(1, 10, "f1"), ringLine(2, 30, "f2"))
	writeRing(t, explain, ringLine(1, 20, "e1"), ringLine(3, 40, "e3"))
	rows, err := ReadMergedTimeline(flight)
	if err != nil {
		t.Fatal(err)
	}
	// The explain ring skipped sequence 2: one gap, sorted before the row it precedes.
	if got := mergedKinds(rows); got != "f1,e1,f2,recording_gap,e3" {
		t.Fatalf("merged kinds = %s", got)
	}
}

func TestMergedTimelineWithoutExplainRingIsTheFlightTimeline(t *testing.T) {
	dir := t.TempDir()
	flight := filepath.Join(dir, "flight.jsonl")
	writeRing(t, flight, ringLine(1, 10, "f1"), ringLine(2, 30, "f2"))
	want, err := ReadTimeline(flight)
	if err != nil {
		t.Fatal(err)
	}
	got, err := ReadMergedTimeline(flight)
	if err != nil {
		t.Fatal(err)
	}
	if fmt.Sprint(got) != fmt.Sprint(want) {
		t.Fatalf("merged = %v, want %v", got, want)
	}
	if _, err := ReadMergedTimeline(filepath.Join(dir, "missing", "flight.jsonl")); err != nil {
		t.Fatal(err)
	}
}
