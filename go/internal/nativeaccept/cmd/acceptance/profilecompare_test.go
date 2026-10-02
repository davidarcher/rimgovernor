package main

import (
	"encoding/json"
	"io"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func perfSummary(total float64, families map[string]float64) profileSummary {
	s := profileSummary{Count: 20, Total: profileStat{Name: "total", P90: total}}
	for name, p90 := range families {
		s.Families = append(s.Families, profileStat{Name: name, P90: p90})
	}
	return s
}

func TestComparePerf(t *testing.T) {
	var history []perfRecord
	for _, noise := range []float64{0, 0.4, -0.3, 0.2, 0.1, -0.2, 0.3} {
		history = append(history, perfRecord{Profile: perfSummary(20+noise, map[string]float64{"pawns": 10 + noise, "things": 1 + noise/10})})
	}
	// Normal noise: within 30%.
	if got := comparePerf(perfSummary(21, map[string]float64{"pawns": 11, "things": 1.05}), history, 0.3, 0.5); len(got) != 0 {
		t.Fatalf("noise flagged: %+v", got)
	}
	// A slow family trips; a small family over the ratio but under min-ms does not.
	got := comparePerf(perfSummary(26, map[string]float64{"pawns": 16, "things": 1.4, "new": 99}), history, 0.3, 0.5)
	if len(got) != 1 || got[0].Name != "pawns" || got[0].Baseline != 10.1 || got[0].Samples != 7 {
		t.Fatalf("regressions %+v", got)
	}
	// Too little history: no baseline.
	if got := comparePerf(perfSummary(99, nil), history[:2], 0.3, 0.5); len(got) != 0 {
		t.Fatalf("baseline from 2 samples: %+v", got)
	}
}

func TestProfileCompareFilesIssue(t *testing.T) {
	dir := t.TempDir()
	hist := filepath.Join(dir, "hist")
	start := time.Date(2026, 9, 1, 0, 0, 0, 0, time.UTC)
	for i := range 8 {
		r := perfRecord{Commit: "c" + string(rune('0'+i)), At: start.Add(time.Duration(i) * 24 * time.Hour), Profile: perfSummary(20, map[string]float64{"pawns": 10})}
		data, _ := json.Marshal(r)
		sub := filepath.Join(hist, string(rune('a'+i)), perfArtifact+"x")
		os.MkdirAll(sub, 0755)
		os.WriteFile(filepath.Join(sub, "record.json"), data, 0644)
	}
	records, err := readPerfHistory(hist, 7)
	if err != nil || len(records) != 7 || records[0].Commit != "c7" {
		t.Fatalf("history %d %v", len(records), err)
	}
	current, _ := json.Marshal(perfSummary(20, map[string]float64{"pawns": 20}))
	currentPath := filepath.Join(dir, "profile.json")
	os.WriteFile(currentPath, current, 0644)

	var calls [][]string
	old := ghRunner
	defer func() { ghRunner = old }()
	ghRunner = func(args ...string) ([]byte, error) {
		calls = append(calls, args)
		if args[0] == "issue" && args[1] == "list" {
			return []byte(`[{"number":5,"title":"other"},{"number":9,"title":"` + perfIssueTitle + `"}]`), nil
		}
		return nil, nil
	}
	var out strings.Builder
	code := profileCompare([]string{"-current", currentPath, "-commit", "head", "-out", filepath.Join(dir, "out", "record.json"), "-history", hist, "-issue"}, &out, io.Discard)
	if code != 0 || !strings.Contains(out.String(), "| pawns | 20.00 | 10.00 | +100% |") || !strings.Contains(out.String(), "compare/c7...head") {
		t.Fatalf("code %d report %s", code, out.String())
	}
	if len(calls) != 2 || calls[1][1] != "comment" || calls[1][2] != "9" {
		t.Fatalf("gh calls %v", calls)
	}
	if _, err := os.Stat(filepath.Join(dir, "out", "record.json")); err != nil {
		t.Fatal(err)
	}
}
