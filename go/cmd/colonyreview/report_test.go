package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func ptr(v float64) *float64 { return &v }

// record writes a fake recording: hours rows, a colonist whose mood
// collapses at hour 5, a death at hour 8 and a daily map shot at hour 0.
func record(t *testing.T, hours int) string {
	t.Helper()
	dir := t.TempDir()
	var lines []string
	for h := 0; h < hours; h++ {
		mood := 0.6
		if h >= 5 {
			mood = 0.1
		}
		dead := 0
		if h >= 8 {
			dead = 1
		}
		shots := []string{fmt.Sprintf("colony-%08d.jpg", h*2500)}
		if h == 0 {
			shots = append(shots, "map-00000000.jpg")
		}
		row := Row{Tick: h * 2500, Day: 0, Hour: h, Date: fmt.Sprintf("1st of Aprimay, %dh", h), Season: "Spring",
			Weather: "Clear", ColonistCount: 3 - dead, DeadColonists: dead, Nutrition: 20, Buildings: 10 + h, Wealth: 5000,
			Colonists: []Colonist{{Name: "Ana", Mood: ptr(mood), Health: ptr(1), Job: "Constructing wall."},
				{Name: "Bo <script>", Mood: ptr(0.7), Health: ptr(1), Job: "Standing."}},
			Events: []Event{{Tick: h * 2500, Kind: "letter", Label: "Raid"}}, Shots: shots}
		data, _ := json.Marshal(row)
		lines = append(lines, string(data))
		for _, s := range shots {
			os.WriteFile(filepath.Join(dir, s), []byte("jpg"), 0644)
		}
	}
	os.WriteFile(filepath.Join(dir, "stats.jsonl"), []byte(strings.Join(lines, "\n")+"\n"), 0644)
	return dir
}

func TestReportAndSite(t *testing.T) {
	in := record(t, 10)
	site := t.TempDir()
	runs := filepath.Join(site, "runs")
	if err := Report(in, filepath.Join(runs, "2026-10-02-1"), map[string]string{"date": "2026-10-02", "biome": "TemperateForest"}); err != nil {
		t.Fatal(err)
	}
	page, _ := os.ReadFile(filepath.Join(runs, "2026-10-02-1", "index.html"))
	for _, want := range []string{"Ana mood 10%", "1 colonist(s) died", "idle 6 hours running (Standing.)", "map-00000000.jpg", "TemperateForest", "Bo &lt;script&gt;"} {
		if !strings.Contains(string(page), want) {
			t.Errorf("report lacks %q", want)
		}
	}
	if _, err := os.Stat(filepath.Join(runs, "2026-10-02-1", "colony-00022500.jpg")); err != nil {
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
	if _, err := os.Stat(filepath.Join(out, "2026-10-02-1", "index.html")); err != nil {
		t.Error("run not copied into site:", err)
	}
}

func TestDeriveSummary(t *testing.T) {
	rows, err := Load(record(t, 10))
	if err != nil {
		t.Fatal(err)
	}
	s := Derive(rows, nil)
	if s.FirstColonists != 3 || s.LastColonists != 2 || s.Deaths != 1 || s.Hours != 10 {
		t.Fatalf("summary %+v", s)
	}
	if s.MinFoodDays < 4.1 || s.MinFoodDays > 4.2 { // 20 nutrition / (1.6 x 3)
		t.Fatalf("min food days %v", s.MinFoodDays)
	}
}
