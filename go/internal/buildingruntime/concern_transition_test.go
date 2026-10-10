package buildingruntime

import (
	"context"
	"log/slog"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

type rowCapture struct{ rows []map[string]any }

func (c *rowCapture) Event(kind string, _ map[string]any, _ bool, payload map[string]any) (uint64, error) {
	row := map[string]any{"kind": kind}
	for k, v := range payload {
		row[k] = v
	}
	c.rows = append(c.rows, row)
	return uint64(len(c.rows)), nil
}

func filingFor(v Verdict) map[policy.ConcernID]plannerFiling {
	note, _ := plannerRecordReason(v)
	return map[policy.ConcernID]plannerFiling{policy.MaintainShelter: {Note: note, Outcome: v.Outcome}}
}

// emitted writes the transitions through the real telemetry handler, so the
// row shape is what lands in explain.jsonl.
func emitted(t *testing.T, transitions []concernTransition, methods map[policy.ConcernID]string) []map[string]any {
	t.Helper()
	capture := &rowCapture{}
	old := slog.Default()
	slog.SetDefault(telemetry.New(capture))
	defer slog.SetDefault(old)
	emitTransitions(context.Background(), transitions, methods)
	return capture.rows
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
		if row["kind"] != telemetry.ConcernTransitionKind || row[telemetry.VerdictKey] != w.verdict || row[telemetry.ReasonKey] != w.reason || row[telemetry.TargetKey] != string(policy.MaintainShelter) {
			t.Fatalf("row %d: %v", i, row)
		}
		attrs := row[telemetry.AttrsKey].(map[string]any)
		if attrs["method"] != "build" {
			t.Fatalf("row %d method: %v", i, attrs)
		}
		prev, hasPrev := attrs["previous_reason"]
		if hasPrev != w.hasPrevious || (hasPrev && prev != w.previous) {
			t.Fatalf("row %d previous: %v", i, attrs)
		}
		held, hasHeld := attrs["held_ticks"]
		if hasHeld != w.hasHeld || (hasHeld && held != w.held) {
			t.Fatalf("row %d held: %v", i, attrs)
		}
		for _, field := range []any{row[telemetry.ReasonKey], prev} {
			if s, _ := field.(string); s != "" && policy.Cause(s).Validate() != nil {
				t.Fatalf("free text in %v", row)
			}
		}
	}
	if attrs := rows[0][telemetry.AttrsKey].(map[string]any); attrs["subject"] != "bed" {
		t.Fatalf("subject: %v", attrs)
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
		if _, ok := row[telemetry.AttrsKey].(map[string]any)["held_ticks"]; ok {
			t.Fatalf("unknown held written: %v", row)
		}
	}
}
