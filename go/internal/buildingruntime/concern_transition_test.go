package buildingruntime

import (
	"context"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"github.com/davidarcher/RimGovernor/go/internal/store/storetest"
)

func filingFor(v Verdict) map[policy.ConcernID]plannerFiling {
	note, _ := plannerRecordReason(v)
	return map[policy.ConcernID]plannerFiling{policy.MaintainShelter: {Note: note, Outcome: v.Outcome}}
}

// emitted writes the transitions into a real session store and reads them
// back, so the row shape is what the launcher's Explain page reads.
func emitted(t *testing.T, transitions []concernTransition, methods map[policy.ConcernID]string) []store.ConcernTransition {
	t.Helper()
	ctx := context.Background()
	db, err := store.Open(ctx, storetest.Path(t))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err = db.RecordConcernTransitions(ctx, transitionRows(transitions, methods, 1, time.UnixMilli(5))); err != nil {
		t.Fatal(err)
	}
	rows, err := db.ConcernTransitions(ctx)
	if err != nil {
		t.Fatal(err)
	}
	return rows
}

func TestConcernTransitionsRefusalWaitAdmitted(t *testing.T) {
	var log plannerReasonLog
	steps := []struct {
		tick    int64
		verdict Verdict
	}{
		{100, noSpace("bed")},
		{110, noSpace("bed")},
		{120, BuildingBunksOpen},
		{125, BuildingBunksOpen},
		{200, BuildingReasonAdmitted},
		{210, BuildingReasonNoDeficit},
	}
	var all []concernTransition
	for _, s := range steps {
		all = append(all, log.changed(filingFor(s.verdict), s.tick)...)
	}
	if len(all) != 3 {
		t.Fatalf("%d transitions, want 3: %+v", len(all), all)
	}
	rows := emitted(t, all, map[policy.ConcernID]string{policy.MaintainShelter: "build"})
	if len(rows) != 3 {
		t.Fatalf("%d rows", len(rows))
	}
	want := []struct {
		verdict, reason, previous string
		hasPrevious               bool
		held                      int64
		hasHeld                   bool
	}{
		{"refused", "no_space", "", false, 0, false},
		{"waiting", "shelter_bunks_open", "no_space", true, 20, true},
		{"admitted", "", "shelter_bunks_open", true, 80, true},
	}
	for i, w := range want {
		row := rows[i]
		if row.Verdict != w.verdict || row.Cause != w.reason || row.Concern != string(policy.MaintainShelter) {
			t.Fatalf("row %d: %+v", i, row)
		}
		if row.Method != "build" {
			t.Fatalf("row %d method: %+v", i, row)
		}
		if row.PreviousKnown != w.hasPrevious || row.Previous != w.previous {
			t.Fatalf("row %d previous: %+v", i, row)
		}
		if (row.Held != nil) != w.hasHeld || (row.Held != nil && *row.Held != w.held) {
			t.Fatalf("row %d held: %+v", i, row)
		}
	}
	if rows[0].Subject != "bed" {
		t.Fatalf("subject: %+v", rows[0])
	}
}

func TestConcernTransitionsQuietCases(t *testing.T) {
	var log plannerReasonLog
	if got := log.changed(filingFor(BuildingReasonNoDeficit), 5); len(got) != 0 {
		t.Fatalf("a goal first seen clear filed %+v", got)
	}
	if got := log.changed(filingFor(noSpace("bed")), 6); len(got) != 1 || !got[0].HadPrevious || got[0].Previous != "" {
		t.Fatalf("clear to refusal: %+v", got)
	}
	// A subject change under the same cause is not a transition and does not
	// restart the held count.
	if got := log.changed(filingFor(noSpace("wood")), 50); len(got) != 0 {
		t.Fatalf("subject change filed %+v", got)
	}
	got := log.changed(filingFor(BuildingReasonAdmitted), 60)
	if len(got) != 1 || got[0].Held == nil || *got[0].Held != 54 {
		t.Fatalf("held after a subject change: %+v", got)
	}
}

// After a restart the log is empty: the first cause has no known past, and a
// clock that went backwards leaves the held count unknown rather than negative.
func TestConcernTransitionsDoNotInventAPast(t *testing.T) {
	var log plannerReasonLog
	first := log.changed(filingFor(noSpace("bed")), 9000)
	if len(first) != 1 || first[0].HadPrevious || first[0].Held != nil {
		t.Fatalf("first after restart: %+v", first)
	}
	back := log.changed(filingFor(BuildingBunksOpen), 100)
	if len(back) != 1 || !back[0].HadPrevious || back[0].Held != nil {
		t.Fatalf("backwards clock: %+v", back)
	}
	for _, row := range emitted(t, append(first, back...), nil) {
		if row.Held != nil {
			t.Fatalf("unknown held written: %+v", row)
		}
	}
}
