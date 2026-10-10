package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func transitionLine(seq int, tick int, concern, verdict, reason, attrs string) string {
	return fmt.Sprintf(`{"version":2,"run":"r1","sequence":%d,"wall_time":%d,"kind":"concern_transition","context":{"level":"INFO","tick":%d,"component":"clock-scheduler"},"payload":{"verdict":%q,"reason":%q,"target":%q,"dur_ms":0,"attrs":%s}}`+"\n",
		seq, 1000+seq, tick, verdict, reason, concern, attrs)
}

func appendFile(t *testing.T, path, s string) {
	t.Helper()
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		t.Fatal(err)
	}
	defer f.Close()
	f.WriteString(s)
}

func TestExplainTailBuildsOrderedTimelinesWithDurations(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, explainFile)
	tail := newExplainTail(filepath.Join(dir, "flight.jsonl"))
	if v := tail.view(); v.Available || !strings.Contains(v.Empty, "No explanation history") || v.Concerns == nil {
		t.Fatalf("missing file: %+v", v)
	}
	appendFile(t, path, transitionLine(1, 100, "food", "refused", "no_worker", `{"subject":"","method":""}`))
	appendFile(t, path, transitionLine(2, 150, "shelter", "waiting", "shelter_bunks_open", `{"subject":"north","method":"build"}`))
	appendFile(t, path, transitionLine(3, 600, "food", "admitted", "", `{"subject":"","method":"forage","previous_reason":"no_worker","held_ticks":500}`))
	appendFile(t, path, "not json\n")
	appendFile(t, path, `{"version":2,"run":"r1","sequence":5,"wall_time":1005,"kind":"planner_step","context":{},"payload":{}}`+"\n")
	v := tail.view()
	if !v.Available || len(v.Concerns) != 2 {
		t.Fatalf("%+v", v)
	}
	food := v.Timeline("food")
	if food == nil || v.Concerns[0].Concern != "food" || len(food.Entries) != 2 {
		t.Fatalf("food newest first with two entries: %+v", v.Concerns)
	}
	first, second := food.Entries[0], food.Entries[1]
	if first.Seq != 1 || second.Seq != 3 || first.Tick != 100 || !first.HasTick {
		t.Fatalf("order: %+v", food.Entries)
	}
	if first.Text != policy.Wording(policy.CauseNoWorker, "") || first.HasHeld || first.Previous != "" || first.PreviousClear {
		t.Fatalf("first row has unknown history: %+v", first)
	}
	if second.Held != "held for 12 min" || second.Previous != policy.Wording(policy.CauseNoWorker, "") ||
		second.Method != "forage" || second.Text != "The bot started on it." || food.Current != second.Text {
		t.Fatalf("%+v", second)
	}
	shelter := v.Timeline("shelter")
	if shelter.Entries[0].Text != policy.Wording(policy.CauseBunksOpen, "north") || !strings.Contains(shelter.Entries[0].Text, "(north)") {
		t.Fatalf("%+v", shelter)
	}
	// An appended row shows on the next poll; an unchanged ring is served from cache.
	appendFile(t, path, transitionLine(6, 900, "shelter", "refused", "no_space", `{"previous_reason":""}`))
	v = tail.view()
	got := v.Timeline("shelter").Entries
	if len(got) != 2 || !got[1].PreviousClear || got[1].HasHeld || v.Concerns[0].Concern != "shelter" {
		t.Fatalf("%+v", v.Concerns)
	}
}

func TestExplainTailWordsEveryCause(t *testing.T) {
	var b strings.Builder
	for i, c := range policy.Causes {
		b.WriteString(transitionLine(i+1, i, "c", "refused", string(c), `{}`))
	}
	dir := t.TempDir()
	appendFile(t, filepath.Join(dir, explainFile), b.String())
	v := newExplainTail(filepath.Join(dir, "flight.jsonl")).view()
	entries := v.Timeline("c").Entries
	if len(entries) != len(policy.Causes) {
		t.Fatalf("%d entries", len(entries))
	}
	for i, e := range entries {
		want := policy.Wording(policy.Causes[i], "")
		if e.Text != want || strings.Contains(e.Text, "cannot name") {
			t.Fatalf("%s: %q", policy.Causes[i], e.Text)
		}
	}
}

func TestExplainTailToleratesUnknownCauseAndRotation(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, explainFile)
	tail := newExplainTail(filepath.Join(dir, "flight.jsonl"))
	appendFile(t, path, transitionLine(1, 1, "x", "refused", "a_future_cause", `{}`))
	if e := tail.view().Timeline("x").Entries[0]; e.Cause != "a_future_cause" || e.Text == "" {
		t.Fatalf("%+v", e)
	}
	// Rotation: the active file moves to .1 and a new one starts.
	if err := os.Rename(path, path+".1"); err != nil {
		t.Fatal(err)
	}
	appendFile(t, path, transitionLine(2, 2, "y", "refused", "no_space", `{}`))
	v := tail.view()
	if v.Timeline("x") == nil || v.Timeline("y") == nil {
		t.Fatalf("%+v", v.Concerns)
	}
	// The ring vanishing leaves an unavailable view, not a stale one.
	os.Remove(path)
	os.Remove(path + ".1")
	if v = tail.view(); v.Available || v.Empty == "" {
		t.Fatalf("%+v", v)
	}
}

func TestGameDuration(t *testing.T) {
	for ticks, want := range map[int64]string{10: "under a minute", 500: "12 min", 2500: "1 h", 10000: "4 h", 60000: "1 day", 150000: "3 days"} {
		if got := gameDuration(ticks); got != want {
			t.Errorf("%d: %q want %q", ticks, got, want)
		}
	}
}
