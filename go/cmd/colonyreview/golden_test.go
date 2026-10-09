package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"os"
	"path/filepath"
	"testing"
)

var update = flag.Bool("update", false, "rewrite the golden HTML files")

// goldenSummary is a fixed run with a score (one unknown component) and the
// given delta, for the rendered-HTML golden files.
func goldenSummary(date, commit string, scalar float64, delta *Delta) Summary {
	return Summary{
		Meta: map[string]string{"date": date, "commit": commit, "seed": "review-x", "biome": "TemperateForest"},
		Score: Score{Scalar: fp(scalar), Components: []Component{
			{Name: "mean_mood", Unit: "0-1", Weight: 2, Value: fp(0.5), Score: fp(0.5), Note: "mean of the hourly colony mood mean"},
			{Name: "raid_damage", Weight: 2, Note: "the timeline records no raid damage"},
		}},
		Delta: delta,
	}
}

func golden(t *testing.T, name string, got []byte) {
	t.Helper()
	path := filepath.Join("testdata", name)
	if *update {
		os.MkdirAll("testdata", 0755)
		if err := os.WriteFile(path, got, 0644); err != nil {
			t.Fatal(err)
		}
		return
	}
	want, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(bytes.ReplaceAll(want, []byte("\r\n"), []byte("\n")), got) {
		t.Errorf("%s differs from the render; rerun with -update and review the diff", name)
	}
}

func TestRunPageGolden(t *testing.T) {
	cases := map[string]*Delta{
		"run_no_baseline.html": {Status: deltaNoBaseline},
		"run_baseline.html": {Status: deltaOK, Baseline: "2026-10-01-1", BaselineCommit: "abc123", Scalar: fp(2.5),
			Components: []ComponentDelta{{Name: "mean_mood", Status: deltaOK, Delta: fp(0.1), ValueDelta: fp(0.1)},
				{Name: "raid_damage", Status: deltaUnknown}}},
	}
	for name, d := range cases {
		var buf bytes.Buffer
		if err := renderRun(&buf, nil, goldenSummary("2026-10-02", "def456", 50, d)); err != nil {
			t.Fatal(err)
		}
		golden(t, name, buf.Bytes())
	}
}

func TestSiteIndexGolden(t *testing.T) {
	runs := t.TempDir()
	write := func(dir string, s Summary) {
		os.MkdirAll(filepath.Join(runs, dir), 0755)
		data, _ := json.Marshal(s)
		os.WriteFile(filepath.Join(runs, dir, "run.json"), data, 0644)
	}
	write("2026-10-01-1", goldenSummary("2026-10-01", "abc123", 47.5, &Delta{Status: deltaNoBaseline}))
	write("2026-10-02-1", goldenSummary("2026-10-02", "def456", 50, &Delta{Status: deltaOK, Scalar: fp(2.5)}))
	out := t.TempDir()
	if err := Site(runs, out); err != nil {
		t.Fatal(err)
	}
	got, _ := os.ReadFile(filepath.Join(out, "index.html"))
	golden(t, "site_index.html", got)
}
