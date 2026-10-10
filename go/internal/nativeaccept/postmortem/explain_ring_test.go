package postmortem

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestFlightFilesIncludeExplanationRing(t *testing.T) {
	dir := t.TempDir()
	for _, name := range []string{"flight.jsonl", "flight.jsonl.1", "explain.jsonl", "explain.jsonl.1", "service-2/explain.jsonl", "service-2/flight.jsonl"} {
		p := filepath.Join(dir, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		line := `{"version":2,"run":"r","sequence":7,"wall_time":1,"kind":"concern_transition","context":{},"payload":{}}` + "\n"
		if err := os.WriteFile(p, []byte(line), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	got := strings.Join(flightFiles(dir), ",")
	want := "flight.jsonl.1,flight.jsonl,explain.jsonl.1,explain.jsonl,service-2/flight.jsonl,service-2/explain.jsonl"
	if got != want {
		t.Fatalf("flightFiles = %s", got)
	}
	var evidence []string
	for _, r := range flightRows(dir) {
		evidence = append(evidence, r.evidence())
	}
	if len(evidence) != 6 || !strings.Contains(strings.Join(evidence, ","), "explain.jsonl#7") {
		t.Fatalf("evidence = %v", evidence)
	}
}
