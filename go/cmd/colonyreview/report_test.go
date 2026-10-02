package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// caseOutput writes a fake case output: a timeline of hours samples (a
// colonist lost at hour 8, Ana's mood collapsing at hour 5, housing in
// deficit from hour 2) and a screenshot per hour plus a map at hour 0.
func caseOutput(t *testing.T, hours int) string {
	t.Helper()
	dir := t.TempDir()
	os.MkdirAll(filepath.Join(dir, "review"), 0755)
	var timeline []map[string]any
	for h := 0; h < hours; h++ {
		tick := h * 2500
		mood, colonists, need := 0.6, 3, "recovered"
		if h >= 5 {
			mood = 0.1
		}
		if h >= 8 {
			colonists = 2
		}
		if h >= 2 {
			need = "deficit"
		}
		timeline = append(timeline, map[string]any{
			"tick": tick, "need": "recovered", "status": "active",
			"MaintainHousing": map[string]any{"need": need, "status": "planning"},
			"colony": map[string]any{"colonists": colonists, "foodRunwayDays": 4.2, "wealthTotal": 5000, "moodMean": 0.5,
				"pawns": []map[string]any{{"label": "Ana", "mood": mood, "food": 0.8, "downed": false},
					{"label": "Bo <script>", "mood": 0.7, "food": 0.8, "downed": false}}},
		})
		os.WriteFile(filepath.Join(dir, "review", fmt.Sprintf("colony-%08d.jpg", tick+10)), []byte("jpg"), 0644)
	}
	timeline = append(timeline, map[string]any{"error": "no tick"}) // skipped
	os.WriteFile(filepath.Join(dir, "review", "map-00000010.jpg"), []byte("jpg"), 0644)
	data, _ := json.Marshal(map[string]any{"passed": true, "timeline": timeline,
		"review": map[string]any{"seed": "review-x", "biome": "TemperateForest"}})
	os.WriteFile(filepath.Join(dir, "result.json"), data, 0644)
	return dir
}

func TestReportAndSite(t *testing.T) {
	in := caseOutput(t, 14)
	site := t.TempDir()
	runs := filepath.Join(site, "runs")
	if err := Report(in, filepath.Join(runs, "2026-10-02-1"), map[string]string{"date": "2026-10-02"}); err != nil {
		t.Fatal(err)
	}
	page, _ := os.ReadFile(filepath.Join(runs, "2026-10-02-1", "index.html"))
	for _, want := range []string{"Ana mood 10%", "colonists fell 3 → 2", "MaintainHousing in deficit 12 hours running",
		"MaintainHousing recovered → deficit", "review/map-00000010.jpg", "TemperateForest", "Bo &lt;script&gt;"} {
		if !strings.Contains(string(page), want) {
			t.Errorf("report lacks %q", want)
		}
	}
	if _, err := os.Stat(filepath.Join(runs, "2026-10-02-1", "review", "colony-00022510.jpg")); err != nil {
		t.Error("images not copied:", err)
	}
	out := filepath.Join(site, "out")
	if err := Site(runs, out); err != nil {
		t.Fatal(err)
	}
	index, _ := os.ReadFile(filepath.Join(out, "index.html"))
	if !strings.Contains(string(index), `href="2026-10-02-1/index.html"`) {
		t.Errorf("site index lacks the run:\n%s", index)
	}
}

func TestDeriveSummary(t *testing.T) {
	rows, _, err := Load(caseOutput(t, 10))
	if err != nil {
		t.Fatal(err)
	}
	s := Derive(rows, nil)
	if s.FirstColonists != 3 || s.LastColonists != 2 || s.Hours != 10 || s.MinFoodDays != 4.2 {
		t.Fatalf("summary %+v", s)
	}
	if rows[3].ColonyShot != "colony-00007510.jpg" {
		t.Fatalf("hour 3 shot %q", rows[3].ColonyShot)
	}
}
