package main

import (
	"context"
	"path/filepath"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// transitionRow is a concern_transitions row at tick; the wall clock follows
// the tick so a later row is later.
func transitionRow(tick int64, concern, verdict, cause, subject, method string) store.ConcernTransition {
	return store.ConcernTransition{WallMS: (1000 + tick) * 1000, Tick: tick, Concern: concern, Verdict: verdict, Cause: cause, Subject: subject, Method: method}
}

func withPrevious(r store.ConcernTransition, previous string, held int64) store.ConcernTransition {
	r.PreviousKnown, r.Previous = true, previous
	if held >= 0 {
		r.Held = &held
	}
	return r
}

// seedState records rows in a session store named state-<stamp>.sqlite under dir.
func seedState(t *testing.T, dir, stamp string, rows ...store.ConcernTransition) {
	t.Helper()
	ctx := context.Background()
	s, err := store.Open(ctx, filepath.Join(dir, "state-"+stamp+".sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	if err = s.RecordConcernTransitions(ctx, rows); err != nil {
		t.Fatal(err)
	}
}

func TestExplainTailBuildsOrderedTimelinesWithDurations(t *testing.T) {
	dir := t.TempDir()
	tail := newExplainTail(dir)
	if v := tail.view(); v.Available || !strings.Contains(v.Empty, "No explanation history") || v.Concerns == nil {
		t.Fatalf("missing store: %+v", v)
	}
	seedState(t, dir, "20260101-000000",
		transitionRow(100, "food", "refused", "no_worker", "", ""),
		transitionRow(150, "shelter", "waiting", "shelter_bunks_open", "north", "build"),
		withPrevious(transitionRow(600, "food", "admitted", "", "", "forage"), "no_worker", 500),
	)
	v := tail.view()
	if !v.Available || len(v.Concerns) != 2 {
		t.Fatalf("%+v", v)
	}
	food := v.Timeline("food")
	if food == nil || v.Concerns[0].Concern != "food" || len(food.Entries) != 2 {
		t.Fatalf("food newest first with two entries: %+v", v.Concerns)
	}
	first, second := food.Entries[0], food.Entries[1]
	if first.Seq >= second.Seq || first.Tick != 100 || !first.HasTick {
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

	// A row recorded after the first view shows on the next one, and a clear
	// previous state reads as such.
	ctx := context.Background()
	s, err := store.Open(ctx, filepath.Join(dir, "state-20260101-000000.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	if err = s.RecordConcernTransitions(ctx, []store.ConcernTransition{withPrevious(transitionRow(900, "shelter", "refused", "no_space", "", ""), "", -1)}); err != nil {
		t.Fatal(err)
	}
	v = tail.view()
	got := v.Timeline("shelter").Entries
	if len(got) != 2 || !got[1].PreviousClear || got[1].HasHeld || v.Concerns[0].Concern != "shelter" {
		t.Fatalf("%+v", v.Concerns)
	}
}

func TestExplainTailWordsEveryCause(t *testing.T) {
	dir := t.TempDir()
	var rows []store.ConcernTransition
	for i, c := range policy.Causes {
		rows = append(rows, transitionRow(int64(i), "c", "refused", string(c), "", ""))
	}
	seedState(t, dir, "20260101-000000", rows...)
	entries := newExplainTail(dir).view().Timeline("c").Entries
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

// The newest session store answers; an older one is a finished session.
func TestExplainTailReadsTheNewestSessionStore(t *testing.T) {
	dir := t.TempDir()
	seedState(t, dir, "20260101-000000", transitionRow(1, "old", "refused", "no_space", "", ""))
	seedState(t, dir, "20260102-000000", transitionRow(2, "new", "refused", "no_space", "", ""))
	v := newExplainTail(dir).view()
	if v.Timeline("new") == nil || v.Timeline("old") != nil {
		t.Fatalf("%+v", v.Concerns)
	}
}

func TestExplainTailReportsAnUnreadableStore(t *testing.T) {
	dir := t.TempDir()
	seedState(t, dir, "20260101-000000")
	v := newExplainTail(dir).view()
	if v.Available || v.Empty != "The explanation history is empty." {
		t.Fatalf("empty table: %+v", v)
	}
	// A file that is not a session store is a reason, not a crash.
	appendNotAStore(t, filepath.Join(dir, "state-20260103-000000.sqlite"))
	if v = newExplainTail(dir).view(); v.Available || !strings.Contains(v.Empty, "could not be read") {
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
