package main

import (
	"os"
	"strings"
	"testing"
)

func appendNotAStore(t *testing.T, path string) {
	t.Helper()
	if err := os.WriteFile(path, []byte("this is not a database, just text padded out to be longer than a header"), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestExplainPageNewestFirstWithPlainLabels(t *testing.T) {
	dir := t.TempDir()
	tail := newExplainTail(dir)
	if p := explainPage(tail.view(), ""); p.Available || p.Notice == "" || p.Selected != nil || p.Concerns == nil {
		t.Fatalf("empty: %+v", p)
	}
	seedState(t, dir, "20260101-000000",
		transitionRow(100, "EnsureFoodSupply", "refused", "no_worker", "", ""),
		transitionRow(150, "tend", "waiting", "no_worker", "", ""),
		withPrevious(transitionRow(2600, "EnsureFoodSupply", "admitted", "", "", "forage"), "no_worker", 2500),
		transitionRow(2700, "mystery_planner", "waiting", "no_worker", "", ""),
	)
	p := explainPage(tail.view(), "EnsureFoodSupply")
	if !p.Available || len(p.Concerns) != 3 || p.Concerns[0].ID != "mystery_planner" {
		t.Fatalf("picker: %+v", p.Concerns)
	}
	labels := map[string]string{}
	for _, c := range p.Concerns {
		labels[c.ID] = c.Label
	}
	if labels["tend"] != "Tend the wounded and sick" || labels["EnsureFoodSupply"] != "Keep the colony fed" || labels["mystery_planner"] != "mystery_planner" {
		t.Fatalf("labels: %v", labels)
	}
	d := p.Selected
	if d == nil || len(d.Entries) != 2 || d.Entries[0].Tick != "2,600" || d.Entries[1].Tick != "100" {
		t.Fatalf("newest first: %+v", d)
	}
	e := d.Entries[0]
	if e.Held != "held for 1 h" || e.Before == "" || !strings.Contains(e.Method, "forage") || !strings.Contains(e.Method, "possibly") {
		t.Fatalf("entry: %+v", e)
	}
	if explainPage(tail.view(), "nope").Selected != nil {
		t.Fatal("unknown id selects nothing")
	}
}
