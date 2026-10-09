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
		mood, colonists, need := 0.6, 3, "met"
		if h >= 5 {
			mood = 0.1
		}
		if h >= 8 {
			colonists = 2
		}
		if h >= 2 {
			need = "unmet"
		}
		timeline = append(timeline, map[string]any{
			"tick": tick, "need": "met", "status": "active",
			"MaintainHousing": map[string]any{"need": need, "status": "planning"},
			"colony": map[string]any{"colonists": colonists, "foodRunwayDays": 4.2, "wealthTotal": 5000, "moodMean": 0.5,
				"pawns": []map[string]any{{"label": "Ana", "mood": mood, "food": 0.8, "downed": false},
					{"label": "Bo, <color=#ffcc00>Cook</color> <3 </script>", "mood": 0.7, "food": 0.8, "downed": false}}},
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
	if err := Report(in, filepath.Join(runs, "2026-10-02-1"), map[string]string{"date": "2026-10-02"}, ""); err != nil {
		t.Fatal(err)
	}
	page, _ := os.ReadFile(filepath.Join(runs, "2026-10-02-1", "index.html"))
	for _, want := range []string{"Ana mood 10%", "colonists fell 3 → 2", "MaintainHousing in deficit 12 hours running",
		"MaintainHousing met → unmet", `"m":"map-00000010.jpg"`, "TemperateForest", `Bo, Cook \u003c3`} {
		if !strings.Contains(string(page), want) {
			t.Errorf("report lacks %q", want)
		}
	}
	if strings.Contains(string(page), "<color") {
		t.Error("report shows the game's color tags")
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

// A fast game outruns the timeline: every hourly shot still gets a row,
// carrying the last sample before it.
func TestSparseSamplesKeepEveryHour(t *testing.T) {
	dir := caseOutput(t, 8)
	data, _ := os.ReadFile(filepath.Join(dir, "result.json"))
	var res map[string]any
	json.Unmarshal(data, &res)
	res["timeline"] = res["timeline"].([]any)[:1]
	data, _ = json.Marshal(res)
	os.WriteFile(filepath.Join(dir, "result.json"), data, 0644)
	rows, _, err := Load(dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 8 || rows[5].ColonyShot != "colony-00012510.jpg" || rows[5].Sampled != "Day 1, 00h" || rows[5].Census.Colonists == nil {
		t.Fatalf("rows %d, hour 5 %+v", len(rows), rows[5])
	}
}

// Zone counts per role show in the report; supplies forbidden for a day are flagged.
func TestStorageFlags(t *testing.T) {
	dir := caseOutput(t, 30)
	data, _ := os.ReadFile(filepath.Join(dir, "result.json"))
	var res map[string]any
	json.Unmarshal(data, &res)
	for _, sample := range res["timeline"].([]any) {
		colony, ok := sample.(map[string]any)["colony"].(map[string]any)
		if !ok {
			continue
		}
		colony["stockpiles"] = []map[string]any{{"role": "general", "zones": 1, "cells": 40, "used": 10}, {"role": "yard", "zones": 1, "cells": 9, "used": 0}}
		colony["forbiddenSupplies"] = true
	}
	data, _ = json.Marshal(res)
	os.WriteFile(filepath.Join(dir, "result.json"), data, 0644)
	out := t.TempDir()
	if err := Report(dir, out, nil, ""); err != nil {
		t.Fatal(err)
	}
	page, _ := os.ReadFile(filepath.Join(out, "index.html"))
	for _, want := range []string{"starting supplies still forbidden after 24 hours", "yard", "still forbidden"} {
		if !strings.Contains(string(page), want) {
			t.Errorf("report lacks %q", want)
		}
	}
}

func TestReportOfAFailedRun(t *testing.T) {
	out := filepath.Join(t.TempDir(), "runs", "2026-10-07-1")
	meta := map[string]string{"date": "2026-10-07", "outcome": "failure"}
	if err := Report(t.TempDir(), out, meta, ""); err != nil {
		t.Fatal(err)
	}
	page, _ := os.ReadFile(filepath.Join(out, "index.html"))
	for _, want := range []string{"The run failed: the case left no result.json (play step: failure)", "Nothing was recorded"} {
		if !strings.Contains(string(page), want) {
			t.Errorf("failed-run page lacks %q", want)
		}
	}
	if err := Site(filepath.Dir(out), t.TempDir()); err != nil {
		t.Fatal(err)
	}
}

// TestFramesCarryMoodLedger passes the status route's ranked sources and
// unowned bucket through to the player untouched, and a missing ledger stays
// absent rather than becoming an empty one.
func TestFramesCarryMoodLedger(t *testing.T) {
	ledger := &MoodLedger{
		Sources:     []MoodSource{{Def: "NeedJoy", Pawns: 2, Lost: -8, Owners: []string{"EnsureComfort"}, Unverified: 2}, {Def: "SleptInBarracks", Pawns: 1, Lost: -4, Owners: []string{}, Unverified: 1}},
		Unowned:     []MoodSource{{Def: "SleptInBarracks", Pawns: 1, Lost: -4, Owners: []string{}, Unverified: 1}},
		Expectation: []MoodExpectation{},
	}
	got := frames([]Row{{Census: Census{MoodLedger: ledger}}, {}})
	if got[0].Ledger != ledger || got[1].Ledger != nil {
		t.Fatalf("ledger frames = %+v", got)
	}
	data, err := json.Marshal(got[0])
	if err != nil {
		t.Fatal(err)
	}
	want := `"ml":{"sources":[{"def":"NeedJoy","pawns":2,"lost":-8,"owners":["EnsureComfort"],"unverified":2},{"def":"SleptInBarracks","pawns":1,"lost":-4,"owners":[],"unverified":1}],"unowned":[{"def":"SleptInBarracks","pawns":1,"lost":-4,"owners":[],"unverified":1}],"unknownPawns":0,"expectation":[]}`
	if !strings.Contains(string(data), want) {
		t.Fatalf("frame json = %s", data)
	}
}
